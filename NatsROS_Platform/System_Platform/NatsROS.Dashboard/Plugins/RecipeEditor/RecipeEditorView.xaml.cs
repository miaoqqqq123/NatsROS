using NATS.Client.Core;
using NatsROS.Core;
using NatsROS.Core.Attributes;
using NatsROS.Core.SystemMessages;
using NatsROS.Dashboard.Models;
using NatsROS.Dashboard.Plugins.NodeManager;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using MessageBox = System.Windows.MessageBox;
using UserControl = System.Windows.Controls.UserControl;

namespace NatsROS.Dashboard.Plugins.RecipeEditor
{
    /// <summary>
    /// RecipeEditorView.xaml 的交互逻辑
    /// </summary>
    public partial class RecipeEditorView : UserControl, IDisposable
    {
        private readonly INatsClient _nats;
        public ObservableCollection<AvailableNodeInfo> AvailableNodes { get; set; } = new();
        public ObservableCollection<RecipeNodeItem> RecipeNodes { get; set; } = new();

        private RecipeNodeItem? _currentEditingRecipe;
        private object? _dummyProxyObject;

        public RecipeEditorView(INatsClient nats)
        {
            InitializeComponent();
            _nats = nats;

            GridAvailableNodes.ItemsSource = AvailableNodes;
            GridRecipeNodes.ItemsSource = RecipeNodes;

            // 【新增】：初始化自愈策略下拉框数据字典
            CboRestartPolicySettings.ItemsSource = new[]
            {
                new { Id = (byte)0, Name = "0: Never (死亡后不重启)" },
                new { Id = (byte)1, Name = "1: OnFailure (故障时重启)" },
                new { Id = (byte)2, Name = "2: Always (总是常驻后台)" }
            };

            ScanAvailableNodesWithAttributes();
        }

        // ==========================================
        // 1. 扫描组件库 (支持提取自定义标签)
        // ==========================================
        private void ScanAvailableNodesWithAttributes()
        {
            try
            {
                var nodeTypes = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                    .Where(t => typeof(RosNode).IsAssignableFrom(t) && !t.IsAbstract)
                    .Where(n => !n.FullName!.Contains("ContainerManagerNode"));

                foreach (var t in nodeTypes)
                {
                    // 提取类上的 [RosNode] 标签
                    var nodeAttr = t.GetCustomAttribute<RosNodeAttribute>();
                    // 反射提取该节点所在的真实 DLL 的物理版本号！
                    string version = t.Assembly.GetName().Version?.ToString() ?? "1.0.0.0";

                    AvailableNodes.Add(new AvailableNodeInfo
                    {
                        AssemblyName = t.Assembly.GetName().Name ?? "",
                        TypeName = t.FullName ?? "",
                        Version = version,
                        DisplayName = nodeAttr?.DisplayName ?? t.Name, // 优先显示漂亮的中文名
                        Category = nodeAttr?.Category ?? "默认组件",
                        Description = nodeAttr?.Description ?? "无"
                    });
                }
            }
            catch { }
        }

        // ==========================================
        // 2. 双击左侧库，自动生成配方与默认参数！
        // ==========================================
        private void GridAvailableNodes_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (GridAvailableNodes.SelectedItem is not AvailableNodeInfo selectedInfo) return;

            var newItem = new RecipeNodeItem
            {
                NodeName = $"{selectedInfo.TypeName.Split('.').Last().ToLower()}_{RecipeNodes.Count + 1}",
                AssemblyName = selectedInfo.AssemblyName,
                TypeName = selectedInfo.TypeName,
                Version = selectedInfo.Version,
                RestartPolicy = 1 // 默认故障重启
            };

            // 【黑魔法】：提取该类内部所有打了 [RosProp] 标签的属性，自动填入参数表！
            var targetType = AppDomain.CurrentDomain.GetAssemblies().SelectMany(a => a.GetTypes()).FirstOrDefault(t => t.FullName == selectedInfo.TypeName);
            if (targetType != null)
            {
                var props = targetType.GetProperties().Where(p =>
                    p.GetCustomAttribute<DefaultValueAttribute>() != null ||
                    p.GetCustomAttribute<DisplayNameAttribute>() != null);
                
                foreach (var p in props)
                {
                    var defAttr = p.GetCustomAttribute<DefaultValueAttribute>();
                    newItem.Parameters[p.Name] = defAttr?.Value?.ToString() ?? "";
                }
            }

            RecipeNodes.Add(newItem);
        }

        private void BtnRemoveRecipeNode_Click(object sender, RoutedEventArgs e)
        {
            if (GridRecipeNodes.SelectedItem is RecipeNodeItem item) RecipeNodes.Remove(item);
        }

        // ==========================================
        // 4. 导入、导出与一键发射
        // ==========================================
        private void BtnSaveRecipe_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "JSON Files (*.json)|*.json", FileName = "launch.json" };
            if (dlg.ShowDialog() == true)
            {
                var profile = new LaunchProfile { Nodes = RecipeNodes.ToList() };
                File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));
                MessageBox.Show("开机配方已成功导出！", "成功");
            }
        }

        private void BtnLoadRecipe_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "JSON Files (*.json)|*.json" };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    var profile = JsonSerializer.Deserialize<LaunchProfile>(File.ReadAllText(dlg.FileName), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (profile != null)
                    {
                        RecipeNodes.Clear();
                        foreach (var n in profile.Nodes) RecipeNodes.Add(n);
                    }
                }
                catch (Exception ex) { MessageBox.Show($"解析 JSON 失败: {ex.Message}"); }
            }
        }

        private async void BtnLaunchAll_Click(object sender, RoutedEventArgs e)
        {
            if (_nats == null || RecipeNodes.Count == 0) return;

            int count = 0;
            foreach (var node in RecipeNodes)
            {
                var req = new LoadNodeReq(node.AssemblyName, node.TypeName, node.NodeName, node.Parameters, node.RestartPolicy, node.MaxRetries, node.RestartDelaySeconds);
                try
                {
                    var res = await _nats.RequestAsync<LoadNodeReq, LoadNodeRes>("container.load_node", req, replyOpts: new NatsSubOpts { Timeout = TimeSpan.FromSeconds(2) });
                    if (res.Data != null && res.Data.Success) count++;
                }
                catch { }
            }
            MessageBox.Show($"发射完毕！成功向母体注入 {count} 个节点。\n请前往 [母体节点大盘] 或 [话题雷达] 查看运行状态。", "发射通知");
        }

        public void Dispose()
        {
            //throw new NotImplementedException();
        }

        private void GridRecipeNodes_SelectedItemChanged(object sender, DevExpress.Xpf.Grid.SelectedItemChangedEventArgs e)
        {
            // 1. 如果之前有选中的影子对象，先把它最新的值【保存回】配方字典里
            SaveProxyToDictionary();

            _currentEditingRecipe = e.NewItem as RecipeNodeItem;

            // 清理上一轮动态生成的属性拦截规则 (保留写死的那些)
            var dynamicDefs = PropGridParams.PropertyDefinitions.Where(d => d.Tag?.ToString() == "Dynamic").ToList();
            foreach (var d in dynamicDefs) PropGridParams.PropertyDefinitions.Remove(d);

            if (_currentEditingRecipe != null)
            {
                GrpParams.Caption = $"⚙️ 参数配置: {_currentEditingRecipe.NodeName}";

                // 2. 反射查找这个节点真实的 C# 类型
                var targetType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                    .FirstOrDefault(t => t.FullName == _currentEditingRecipe.TypeName);

                if (targetType != null)
                {
                    try
                    {
                        // 3. 实例化一个不触发构造函数的【纯净影子对象】
                        _dummyProxyObject = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(targetType);

                        var props = targetType.GetProperties(BindingFlags.Public | BindingFlags.Instance);

                        foreach (var prop in props)
                        {
                            // 只处理打了 [RosProp] 标签，或者兼容你旧的 [Category] 标签的属性
                            var rosAttr = prop.GetCustomAttribute<NatsROS.Core.Attributes.RosPropAttribute>();
                            var scopeAttr = prop.GetCustomAttribute<NatsROS.Core.Attributes.ParameterScopeAttribute>();
                            var catAttr = prop.GetCustomAttribute<System.ComponentModel.CategoryAttribute>();

                            if (rosAttr != null || catAttr != null)
                            {
                                // 【核心拦截】：如果是专属运行时标定的参数，绝对不让它出现在开机配方里！
                                if (scopeAttr != null && scopeAttr.Scope == NatsROS.Core.Attributes.RosPropScope.Runtime)
                                {
                                    // 强制告诉 DevExpress 隐藏这个属性！
                                    PropGridParams.PropertyDefinitions.Add(new DevExpress.Xpf.PropertyGrid.PropertyDefinition
                                    {
                                        Path = prop.Name,
                                        Visibility = System.Windows.Visibility.Collapsed
                                    });
                                    continue;
                                }
                                   

                                // A. 智能数据灌入：从字符串字典转为强类型
                                if (_currentEditingRecipe.Parameters.TryGetValue(prop.Name, out string? strVal))
                                {
                                    try
                                    {
                                        prop.SetValue(_dummyProxyObject, Convert.ChangeType(strVal, prop.PropertyType));
                                    }
                                    catch { /* 忽略转换失败的废弃参数 */ }
                                }
                                else if (rosAttr != null && !string.IsNullOrEmpty(rosAttr.DefaultValue))
                                {
                                    // 如果字典里没有，用标签里的默认值兜底
                                    try
                                    {
                                        prop.SetValue(_dummyProxyObject, Convert.ChangeType(rosAttr.DefaultValue, prop.PropertyType));
                                    }
                                    catch
                                    {
                                    }
                                }

                                // B. 动态生成 DX 引擎的属性翻译官！
                                // 这样 UI 就能完美显示我们 [RosProp] 里的中文名和详细描述了
                                string displayName = rosAttr?.DisplayName ?? prop.GetCustomAttribute<System.ComponentModel.DisplayNameAttribute>()?.DisplayName ?? prop.Name;
                                string description = rosAttr?.Description ?? prop.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description ?? "";

                                var def = new DevExpress.Xpf.PropertyGrid.PropertyDefinition
                                {
                                    Path = prop.Name,
                                    Header = displayName,
                                    Description = description,
                                    Tag = "Dynamic" // 标记为动态生成，方便下次清空
                                };

                                // ==========================================
                                // 【核心修复】：把节点管理器里的 IO 标签选择器魔法，完美复刻过来！
                                // ==========================================
                                if (prop.GetCustomAttribute<NatsROS.Core.Attributes.IoTagSelectorAttribute>() != null)
                                {
                                    var btnSettings = new DevExpress.Xpf.Editors.Settings.ButtonEditSettings { AllowDefaultButton = true, IsTextEditable = true };
                                    btnSettings.DefaultButtonClick += (s, args) =>
                                    {
                                        // 呼叫智能 IO 标签选择弹窗
                                        var dlg = new IoTagSelectorDialog(_nats) { Owner = Window.GetWindow(this) };
                                        if (dlg.ShowDialog() == true && s is DevExpress.Xpf.Editors.ButtonEdit editor)
                                        {
                                            // 将选中的标签赋给输入框
                                            editor.EditValue = dlg.SelectedTag;
                                        }
                                    };
                                    def.EditSettings = btnSettings;
                                }

                                // ==========================================
                                // 【魔法 2 (新增)】：拦截 FilePath 特性，生成文件浏览按钮！
                                // ==========================================
                                var fileAttr = prop.GetCustomAttribute<NatsROS.Core.Attributes.FilePathAttribute>();
                                if (fileAttr != null)
                                {
                                    var btnSettings = new DevExpress.Xpf.Editors.Settings.ButtonEditSettings { AllowDefaultButton = true, IsTextEditable = true };
                                    btnSettings.DefaultButtonClick += (s, args) =>
                                    {
                                        // 弹出 Windows 原生文件选择对话框
                                        var dlg = new Microsoft.Win32.OpenFileDialog
                                        {
                                            Filter = fileAttr.Filter,
                                            Title = "请选择文件"
                                        };
                                        if (dlg.ShowDialog() == true && s is DevExpress.Xpf.Editors.ButtonEdit editor)
                                        {
                                            editor.EditValue = dlg.FileName;
                                        }
                                    };
                                    def.EditSettings = btnSettings;
                                }

                                PropGridParams.PropertyDefinitions.Add(def);
                            }
                        }

                        // 4. 将填满数据的强类型影子对象绑定给 UI！
                        PropGridParams.SelectedObject = _dummyProxyObject;
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"生成影子对象失败: {ex.Message}");
                    }
                }
            }
            else
            {
                GrpParams.Caption = "⚙️ 参数配置 (未选择)";
                PropGridParams.SelectedObject = null;
                _dummyProxyObject = null;
            }
        }

        // ==========================================
        // 核心反向提取机制：将强类型影子对象转回字典
        // ==========================================
        private void SaveProxyToDictionary()
        {
            if (_currentEditingRecipe != null && _dummyProxyObject != null)
            {
                // 注意：这里不要 Clear()，否则可能会删掉那些代码里没写，但历史遗留的参数

                var props = _dummyProxyObject.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.CanWrite &&
                    (p.GetCustomAttribute<NatsROS.Core.Attributes.RosPropAttribute>() != null ||
                     p.GetCustomAttribute<System.ComponentModel.CategoryAttribute>() != null));

                foreach (var p in props)
                {
                    var val = p.GetValue(_dummyProxyObject);
                    if (val != null)
                    {
                        // 把 true 又变回 "True" 存入字典
                        _currentEditingRecipe.Parameters[p.Name] = val.ToString() ?? "";
                    }
                }
            }
        }

        // 当用户在右侧 PropertyGrid 狂点打钩框或修改数字时触发
        private void PropGridParams_CellValueChanged(object sender, DevExpress.Xpf.PropertyGrid.CellValueChangedEventArgs e)
        {
            // 实时把用户的修改刷回底层的字典！
            SaveProxyToDictionary();
        }

        // ==========================================
        // 智能文件浏览拦截器
        // ==========================================
        private void BtnBrowseFile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is DevExpress.Xpf.Editors.ButtonEdit editor)
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "All Files (*.*)|*.*",
                    Title = "请选择文件"
                };

                if (dlg.ShowDialog() == true)
                {
                    editor.EditValue = dlg.FileName;
                }
            }
        }
    }
}
