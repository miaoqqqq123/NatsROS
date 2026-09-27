using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using NATS.Client.Core;
using NatsROS.Core.Attributes;
using NatsROS.Core.SystemMessages;
using NatsROS.Dashboard.Models;
using MessageBox = System.Windows.MessageBox;
using UserControl = System.Windows.Controls.UserControl;

namespace NatsROS.Dashboard.Plugins.NodeManager
{
    public partial class NodeManagerView : UserControl, IDisposable
    {
        private readonly INatsClient _nats;
        public ObservableCollection<AvailableNodeInfo> AvailableNodes { get; set; } = new();
        public ObservableCollection<NodeItem> RunningNodes { get; set; } = new();

        private NodeItem? _currentEditingRecipe;
        private object? _dummyProxyObject;

        public NodeManagerView(INatsClient nats)
        {
            InitializeComponent();
            _nats = nats;
            GridAvailableNodes.ItemsSource = AvailableNodes;
            GridNodes.ItemsSource = RunningNodes;

            ScanAvailableNodesWithAttributes();
            _ = RefreshNodeListAsync();
        }

        private void ScanAvailableNodesWithAttributes()
        {
            try
            {
                var nodeTypes = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                    .Where(t => typeof(NatsROS.Core.RosNode)
                    .IsAssignableFrom(t) && !t.IsAbstract)
                    .Where(n => !n.FullName!.Contains("ContainerManagerNode"));

                foreach (var t in nodeTypes)
                {
                    var attr = t.GetCustomAttribute<RosNodeAttribute>();
                    AvailableNodes.Add(new AvailableNodeInfo 
                    {
                        AssemblyName = t.Assembly.GetName().Name ?? "",
                        TypeName = t.FullName ?? "",
                        DisplayName = attr?.DisplayName ?? t.Name, // 优先显示漂亮的中文名
                        Category = attr?.Category ?? "默认组件",
                        Description = attr?.Description ?? "无"
                    });
                }
            }
            catch { }
        }

        // 双击立刻拉起一个节点
        private async void GridAvailableNodes_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (GridAvailableNodes.SelectedItem is not AvailableNodeInfo selectedInfo) return;
            string nodeName = $"{selectedInfo.TypeName.Split('.').Last().ToLower()}_{RunningNodes.Count + 1}";
            try
            {
                var req = new LoadNodeReq(selectedInfo.AssemblyName, selectedInfo.TypeName, nodeName);
                // 1. 发起请求并拿到结果
                var res = await _nats.RequestAsync<LoadNodeReq, LoadNodeRes>("container.load_node", req, replyOpts: new NatsSubOpts { Timeout = TimeSpan.FromSeconds(3) });

                // 2. 【核心修复】：如果母体返回失败，必须弹窗大声告诉用户！
                if (res.Data != null && !res.Data.Success)
                {
                    MessageBox.Show($"母体加载节点失败！\n原因: {res.Data.Message}", "加载失败", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                await RefreshNodeListAsync();
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await RefreshNodeListAsync();
        private async Task RefreshNodeListAsync()
        {
            if (_nats == null) return;
            try
            {
                var res = await _nats.RequestAsync<ListNodesReq, ListNodesRes>("container.list_nodes", new ListNodesReq(), replyOpts: new NatsSubOpts { Timeout = TimeSpan.FromSeconds(2) });
                if (res.Data != null)
                {
                    RunningNodes.Clear();
                    foreach (var n in res.Data.Nodes)
                    {
                        string s = n.State switch { 0 => "⚪ Unconfigured", 1 => "🟡 Inactive", 2 => "🟢 Active", 3 => "🔴 Faulted", _ => "⚫ Unknown" };
                        RunningNodes.Add(new NodeItem
                        {
                            NodeName = n.NodeName,
                            StateCode = n.State,
                            StateStr = s,
                            AssemblyName = n.AssemblyName,  // 【新增】接收底层真实数据
                            TypeName = n.TypeName,          // 【新增】接收底层真实数据
                            Version = n.Version             // 【新增】：接收底层上报的版本号
                        });
                    }
                    GridNodes.RefreshData();
                }
            }
            catch { }
        }

        // 生命周期状态控制
        private async void BtnStateActive_Click(object sender, RoutedEventArgs e) => await ChangeStateAsync(2);
        private async void BtnStateInactive_Click(object sender, RoutedEventArgs e) => await ChangeStateAsync(1);
        private async Task ChangeStateAsync(byte targetState)
        {
            if (GridNodes.SelectedItem is not NodeItem selectedItem) return;
            try
            {
                await _nats.RequestAsync<ChangeStateReq, ChangeStateRes>("container.change_state", new ChangeStateReq(selectedItem.NodeName, targetState), replyOpts: new NatsSubOpts { Timeout = TimeSpan.FromSeconds(2) });
                await Task.Delay(200); // 稍微等一下状态流转
                await RefreshNodeListAsync();
            }
            catch { }
        }

        private async void BtnUnloadNode_Click(object sender, RoutedEventArgs e)
        {
            if (GridNodes.SelectedItem is not NodeItem selectedItem) return;
            try
            {
                await _nats.RequestAsync<UnloadNodeReq, UnloadNodeRes>("container.unload_node", new UnloadNodeReq(selectedItem.NodeName), replyOpts: new NatsSubOpts { Timeout = TimeSpan.FromSeconds(3) });
                await RefreshNodeListAsync();
            }
            catch { }
        }

        // 参数热更部分保持原样...
        private async void GridNodes_SelectedItemChanged(object sender, DevExpress.Xpf.Grid.SelectedItemChangedEventArgs e)
        {
            SaveProxyToDictionary();
            _currentEditingRecipe = e.NewItem as NodeItem;

            var dynamicDefs = PropGridParams.PropertyDefinitions.Where(d => d.Tag?.ToString() == "Dynamic").ToList();
            foreach (var d in dynamicDefs) PropGridParams.PropertyDefinitions.Remove(d);

            if (_currentEditingRecipe != null && !string.IsNullOrEmpty(_currentEditingRecipe.TypeName))
            {
                GrpNodeParams.Caption = $"⚙️ 参数配置: {_currentEditingRecipe.NodeName} (实时拉取中...)";
                PropGridParams.SelectedObject = null;
                BtnApplyParams.IsEnabled = false;

                try
                {
                    // 1. 去底层参数服务器拉取该节点真实的当前参数
                    var paramClient = new NatsROS.Core.Parameters.RosParameterClient(_nats, _currentEditingRecipe.NodeName);
                    var keys = await paramClient.ListAsync();

                    _currentEditingRecipe.Parameters.Clear();
                    foreach (var key in keys)
                    {
                        var val = await paramClient.GetAsync(key);
                        if (val != null) _currentEditingRecipe.Parameters[key] = val;
                    }

                    // ==========================================
                    // 2. 【直接使用底层上报的真实类全名进行反射！】
                    // ==========================================
                    var targetType = AppDomain.CurrentDomain.GetAssemblies()
                        .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                        .FirstOrDefault(t => t.FullName == _currentEditingRecipe.TypeName);

                    if (targetType != null)
                    {
                        try
                        {
                            _dummyProxyObject = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(targetType);
                            var props = targetType.GetProperties(BindingFlags.Public | BindingFlags.Instance);

                            foreach (var prop in props)
                            {
                                var rosAttr = prop.GetCustomAttribute<NatsROS.Core.Attributes.RosPropAttribute>();
                                var catAttr = prop.GetCustomAttribute<System.ComponentModel.CategoryAttribute>();
                                var scopeAttr = prop.GetCustomAttribute<NatsROS.Core.Attributes.ParameterScopeAttribute>();

                                if (rosAttr != null || catAttr != null)
                                {
                                    // 【核心拦截】：如果是决定节点身份的开机参数，绝对不允许在运行时热更！
                                    if (scopeAttr != null && scopeAttr.Scope == NatsROS.Core.Attributes.RosPropScope.Launch)
                                    {
                                        // 强制告诉 DevExpress 隐藏这个属性！
                                        PropGridParams.PropertyDefinitions.Add(new DevExpress.Xpf.PropertyGrid.PropertyDefinition
                                        {
                                            Path = prop.Name,
                                            Visibility = System.Windows.Visibility.Collapsed
                                        });
                                        continue;
                                    }

                                    if (_currentEditingRecipe.Parameters.TryGetValue(prop.Name, out string? strVal))
                                    {
                                        try { prop.SetValue(_dummyProxyObject, Convert.ChangeType(strVal, prop.PropertyType)); } catch { }
                                    }
                                    else if (rosAttr != null && !string.IsNullOrEmpty(rosAttr.DefaultValue))
                                    {
                                        try { prop.SetValue(_dummyProxyObject, Convert.ChangeType(rosAttr.DefaultValue, prop.PropertyType)); } catch { }
                                    }

                                    string displayName = rosAttr?.DisplayName ?? prop.GetCustomAttribute<System.ComponentModel.DisplayNameAttribute>()?.DisplayName ?? prop.Name;
                                    string description = rosAttr?.Description ?? prop.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description ?? "";

                                    var def = new DevExpress.Xpf.PropertyGrid.PropertyDefinition
                                    {
                                        Path = prop.Name,
                                        Header = displayName,
                                        Description = description,
                                        Tag = "Dynamic"
                                    };

                                    // ==========================================
                                    // 【核心拦截魔法】：如果是 IO 标签选择器，画出一个 [...] 按钮！
                                    // ==========================================
                                    if (prop.GetCustomAttribute<NatsROS.Core.Attributes.IoTagSelectorAttribute>() != null)
                                    {
                                        var btnSettings = new DevExpress.Xpf.Editors.Settings.ButtonEditSettings { AllowDefaultButton = true, IsTextEditable = true };
                                        btnSettings.DefaultButtonClick += (s, args) =>
                                        {
                                            var dlg = new IoTagSelectorDialog(_nats) { Owner = Window.GetWindow(this) };
                                            if (dlg.ShowDialog() == true && s is DevExpress.Xpf.Editors.ButtonEdit editor)
                                            {
                                                editor.EditValue = dlg.SelectedTag;
                                            }
                                        };
                                        def.EditSettings = btnSettings;
                                    }

                                    PropGridParams.PropertyDefinitions.Add(def);
                                }
                            }

                            PropGridParams.SelectedObject = _dummyProxyObject;
                            GrpNodeParams.Caption = $"⚙️ 参数热更: {_currentEditingRecipe.NodeName}";
                            BtnApplyParams.IsEnabled = true;
                        }
                        catch { }
                    }
                    else
                    {
                        GrpNodeParams.Caption = $"⚠️ 未在内存中找到类: {_currentEditingRecipe.TypeName}";
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"拉取参数失败: {ex.Message}");
                }
            }
            else
            {
                GrpNodeParams.Caption = "⚙️ 节点参数热更 (未选择或缺少类信息)";
                PropGridParams.SelectedObject = null;
                _dummyProxyObject = null;
                BtnApplyParams.IsEnabled = false;
            }

        }

        private async void BtnApplyParams_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentEditingRecipe.NodeName) || _currentEditingRecipe == null) return;
            
            BtnApplyParams.IsEnabled = false;
            BtnApplyParams.Content = "⏳ 下发中...";

            try
            {
                // 1. 确保最新数据都在字典里
                SaveProxyToDictionary();

                // 2. 发送到大管家的参数服务器
                var paramClient = new NatsROS.Core.Parameters.RosParameterClient(_nats, _currentEditingRecipe.NodeName);
                foreach (var kvp in _currentEditingRecipe.Parameters)
                {
                    await paramClient.SetAsync(kvp.Key, kvp.Value);
                }

                MessageBox.Show("✅ 参数热更新成功！已通过 NATS 瞬间下发并生效。", "更新成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"更新失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnApplyParams.IsEnabled = true;
                BtnApplyParams.Content = "🔥 下发热更新 (Apply)";
            }
        }

        public void Dispose()
        {
            //throw new NotImplementedException();
        }

        // 当用户在右侧 PropertyGrid 狂点打钩框或修改数字时触发
        private void PropGridParams_CellValueChanged(object sender, DevExpress.Xpf.PropertyGrid.CellValueChangedEventArgs e)
        {
            // 每次用户在 UI 上打钩或修改数字，实时保存到本地内存字典（不下发）
            SaveProxyToDictionary();
        }


        // ==========================================
        // 核心反向提取机制：将强类型影子对象转回字典
        // ==========================================
        private void SaveProxyToDictionary()
        {
            if (_currentEditingRecipe != null && _dummyProxyObject != null)
            {
                var props = _dummyProxyObject.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.CanWrite &&
                                (p.GetCustomAttribute<NatsROS.Core.Attributes.RosPropAttribute>() != null ||
                                 p.GetCustomAttribute<System.ComponentModel.CategoryAttribute>() != null));

                foreach (var p in props)
                {
                    var val = p.GetValue(_dummyProxyObject);
                    if (val != null)
                    {
                        _currentEditingRecipe.Parameters[p.Name] = val.ToString() ?? "";
                    }
                }
            }
        }

    }
}