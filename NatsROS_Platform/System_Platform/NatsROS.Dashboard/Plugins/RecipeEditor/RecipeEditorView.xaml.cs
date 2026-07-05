using DevExpress.Xpf.Grid;
using NATS.Client.Core;
using NatsROS.Core;
using NatsROS.Core.Attributes;
using NatsROS.Core.SystemMessages;
using NatsROS.Dashboard.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using MessageBox = System.Windows.MessageBox;
using UserControl = System.Windows.Controls.UserControl;

namespace NatsROS.Dashboard.Plugins.RecipeEditor
{
    /// <summary>
    /// RecipeEditorView.xaml 的交互逻辑
    /// </summary>
    public partial class RecipeEditorView : UserControl
    {
        private readonly INatsClient _nats;
        public ObservableCollection<AvailableNodeInfo> AvailableNodes { get; set; } = new();
        public ObservableCollection<RecipeNodeItem> RecipeNodes { get; set; } = new();
        public ObservableCollection<RecipeParamItem> CurrentParameters { get; set; } = new();

        private RecipeNodeItem? _currentEditingRecipe;

        public RecipeEditorView(INatsClient nats)
        {
            InitializeComponent();
            _nats = nats;

            GridAvailableNodes.ItemsSource = AvailableNodes;
            GridRecipeNodes.ItemsSource = RecipeNodes;
            GridParams.ItemsSource = CurrentParameters;

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

                    AvailableNodes.Add(new AvailableNodeInfo
                    {
                        AssemblyName = t.Assembly.GetName().Name ?? "",
                        TypeName = t.FullName ?? "",
                        DisplayName = nodeAttr?.DisplayName ?? t.Name, // 优先显示漂亮的中文名
                        Category = nodeAttr?.Category ?? "默认组件",
                        Description = nodeAttr?.Description ?? ""
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

        // ==========================================
        // 3. 点击中间配方，右侧动态显示/编辑参数
        // ==========================================
        private void GridRecipeNodes_SelectedItemChanged(object sender, DevExpress.Xpf.Grid.SelectedItemChangedEventArgs e)
        {
            if (_currentEditingRecipe != null)
            {
                _currentEditingRecipe.Parameters.Clear();
                foreach (var p in CurrentParameters) _currentEditingRecipe.Parameters[p.Key] = p.Value;
            }

            _currentEditingRecipe = e.NewItem as RecipeNodeItem;
            CurrentParameters.Clear();

            if (_currentEditingRecipe != null)
            {
                GrpParams.Header = $"⚙️ 参数配置: {_currentEditingRecipe.NodeName}";

                var targetType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                    .FirstOrDefault(t => t.FullName == _currentEditingRecipe.TypeName);

                foreach (var kvp in _currentEditingRecipe.Parameters)
                {
                    string desc = "用户自定义附加参数";
                    string category = "Misc (未分类)";
                    bool isFile = false;                 // 【新增】
                    string filter = "All Files (*.*)|*.*"; // 【新增】

                    if (targetType != null)
                    {
                        var prop = targetType.GetProperty(kvp.Key);
                        if (prop != null)
                        {
                            var descAttr = prop.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>();
                            if (descAttr != null && !string.IsNullOrEmpty(descAttr.Description)) desc = descAttr.Description;

                            var catAttr = prop.GetCustomAttribute<System.ComponentModel.CategoryAttribute>();
                            if (catAttr != null && !string.IsNullOrEmpty(catAttr.Category)) category = catAttr.Category;

                            // 【核心魔法】：捕获路径选择器标签！
                            // (就算没打标签，只要参数名叫 path 结尾，我们也极度智能地给它开启文件选择功能！)
                            var fileAttr = prop.GetCustomAttribute<NatsROS.Core.Attributes.FilePathAttribute>();
                            if (fileAttr != null)
                            {
                                isFile = true;
                                filter = fileAttr.Filter;
                            }
                            else if (kvp.Key.EndsWith("path", StringComparison.OrdinalIgnoreCase))
                            {
                                isFile = true;
                            }
                        }
                    }

                    CurrentParameters.Add(new RecipeParamItem { Key = kvp.Key, Value = kvp.Value, Description = desc, Category = category, IsFilePath = isFile, FileFilter = filter });
                }
            }
            else
            {
                GrpParams.Header = "⚙️ 参数配置 (未选择)";
            }
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
            // 确保当前正在编辑的参数被保存回对象
            if (_currentEditingRecipe != null)
            {
                _currentEditingRecipe.Parameters.Clear();
                foreach (var p in CurrentParameters) _currentEditingRecipe.Parameters[p.Key] = p.Value;
            }

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
                        CurrentParameters.Clear();
                        foreach (var n in profile.Nodes) RecipeNodes.Add(n);
                    }
                }
                catch (Exception ex) { MessageBox.Show($"解析 JSON 失败: {ex.Message}"); }
            }
        }

        private async void BtnLaunchAll_Click(object sender, RoutedEventArgs e)
        {
            if (_nats == null || RecipeNodes.Count == 0) return;

            // 同样确保参数保存
            if (_currentEditingRecipe != null)
            {
                _currentEditingRecipe.Parameters.Clear();
                foreach (var p in CurrentParameters) _currentEditingRecipe.Parameters[p.Key] = p.Value;
            }

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

        // ==========================================
        // 智能参数编辑器：文件路径浏览功能
        // ==========================================
        private void BtnBrowseFile_Click(object sender, RoutedEventArgs e)
        {
            // 拿到当前点击的单元格数据
            var buttonEdit = sender as DevExpress.Xpf.Editors.ButtonEdit;
            if (buttonEdit?.DataContext is EditGridCellData cellData && cellData.RowData.Row is RecipeParamItem paramItem)
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = paramItem.FileFilter,
                    Title = $"请选择 {paramItem.Key} 的文件路径"
                };

                if (dlg.ShowDialog() == true)
                {
                    // 将选择的路径填入单元格，DevExpress 的 PART_Editor 会自动触发数据的双向绑定！
                    buttonEdit.EditValue = dlg.FileName;
                }
            }
        }
    }
}
