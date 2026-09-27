using DevExpress.Xpf.Bars;
using NATS.Client.Core;
using NatsROS.Core.SystemMessages;
using NatsROS.Dashboard.Models;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using NatsROS.BehaviorTree.Attributes;
using NatsROS.BehaviorTree.Core;
using MessageBox = System.Windows.MessageBox;
using UserControl = System.Windows.Controls.UserControl;

namespace NatsROS.Dashboard.Plugins.BehaviorTreeEditor
{
    // ==========================================
    // 用于绑定给 TreeListControl 的树形节点包装模型
    // ==========================================
    public class BtEditorNodeModel : INotifyPropertyChanged
    {
        public ObservableCollection<BtEditorNodeModel> Children { get; set; } = new();

        public BehaviorTreeNode NodeInstance { get; set; }

        public string Name
        {
            get => NodeInstance.Name;
            set
            {
                if (NodeInstance.Name != value)
                {
                    NodeInstance.Name = value;
                    OnPropertyChanged();
                }
            }
        }

        public BtNodeType NodeType => NodeInstance.NodeType;
        public string Summary { get; private set; } = "";

        public BtEditorNodeModel(BehaviorTreeNode instance)
        {
            NodeInstance = instance;
            UpdateSummary();
        }

        public void UpdateSummary()
        {
            var props = NodeInstance.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanWrite && p.Name != "Id" && p.Name != "Name" &&
                            (p.GetCustomAttribute<CategoryAttribute>() != null ||
                             p.GetCustomAttribute<DefaultValueAttribute>() != null ||
                             p.GetCustomAttribute<BtPropAttribute>() != null));

            var summaries = props.Select(p => $"{p.Name}={p.GetValue(NodeInstance)}").ToList();
            Summary = string.Join(", ", summaries);

            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(Summary));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public partial class BtEditorView : UserControl, IDisposable
    {
        private readonly INatsClient _nats;

        public ObservableCollection<AvailableNodeInfo> ToolboxNodes { get; set; } = new();
        public ObservableCollection<BtEditorNodeModel> TreeData { get; set; } = new();

        private BtEditorNodeModel? _rootModel;

        public BtEditorView(INatsClient nats)
        {
            InitializeComponent();
            _nats = nats;

            GridToolbox.ItemsSource = ToolboxNodes;
            TreeListBehavior.ItemsSource = TreeData;

            ScanToolboxNodes();
            InitializeDefaultTree();
        }

        private void ScanToolboxNodes()
        {
            var nodeTypes = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                .Where(t => typeof(BehaviorTreeNode).IsAssignableFrom(t) && !t.IsAbstract && t.GetCustomAttribute<BtNodeAttribute>() != null);

            ToolboxNodes.Add(new AvailableNodeInfo { TypeName = typeof(SequenceNode).FullName!, DisplayName = "顺序执行 (Sequence)", Category = "00. 核心控制流" });
            ToolboxNodes.Add(new AvailableNodeInfo { TypeName = typeof(SelectorNode).FullName!, DisplayName = "选择执行 (Selector)", Category = "00. 核心控制流" });
            ToolboxNodes.Add(new AvailableNodeInfo { TypeName = typeof(RetryNode).FullName!, DisplayName = "失败重试 (Retry)", Category = "00. 核心控制流" });

            foreach (var type in nodeTypes)
            {
                var attr = type.GetCustomAttribute<BtNodeAttribute>();
                ToolboxNodes.Add(new AvailableNodeInfo
                {
                    AssemblyName = type.Assembly.GetName().Name ?? "",
                    TypeName = type.FullName ?? "",
                    DisplayName = attr?.DisplayName ?? type.Name,
                    Category = attr?.Category ?? "默认组件",
                    Description = attr?.Description ?? "无"
                });
            }
        }

        private void InitializeDefaultTree()
        {
            TreeData.Clear();
            var rootInstance = new RootNode("任务起点 (Root)");
            _rootModel = new BtEditorNodeModel(rootInstance);
            TreeData.Add(_rootModel);
        }

        // ==========================================
        // 工具栏事件 (使用 ItemClickEventArgs)
        // ==========================================
        private void BtnLoadXml_Click(object sender, ItemClickEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "XML Files (*.xml)|*.xml" };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    var doc = XDocument.Parse(System.IO.File.ReadAllText(dlg.FileName));
                    var firstNodeXml = doc.Descendants("Node").FirstOrDefault();
                    if (firstNodeXml != null)
                    {
                        TreeData.Clear();
                        _rootModel = RestoreNodeFromXml(firstNodeXml);
                        TreeData.Add(_rootModel);
                        TreeListView.ExpandAllNodes();
                    }
                }
                catch (Exception ex) { MessageBox.Show($"导入失败: {ex.Message}"); }
            }
        }

        private void BtnSaveXml_Click(object sender, ItemClickEventArgs e)
        {
            try
            {
                XElement rootTreeXml = GenerateXmlNode(_rootModel!);
                XDocument xmlDoc = new XDocument(new XElement("root", new XElement("BehaviorTree", new XAttribute("ID", "MainTask"), rootTreeXml)));

                var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "XML Files (*.xml)|*.xml", FileName = "MyTree.xml" };
                if (dlg.ShowDialog() == true)
                {
                    xmlDoc.Save(dlg.FileName);
                    MessageBox.Show("✅ 行为树配方导出成功！");
                }
            }
            catch (Exception ex) { MessageBox.Show($"导出 XML失败: {ex.Message}"); }
        }

        private void BtnExpandAll_Click(object sender, ItemClickEventArgs e) => TreeListView.ExpandAllNodes();
        private void BtnCollapseAll_Click(object sender, ItemClickEventArgs e) => TreeListView.CollapseAllNodes();

        private async void BtnDeploy_Click(object sender, ItemClickEventArgs e)
        {
            if (_rootModel == null) return;
            string targetBrain = EditTargetBrain.EditValue?.ToString() ?? "brain_dispenser";

            try
            {
                XElement rootTreeXml = GenerateXmlNode(_rootModel);
                XDocument xmlDoc = new XDocument(new XElement("root", new XElement("BehaviorTree", new XAttribute("ID", "MainTask"), rootTreeXml)));
                string xmlContent = xmlDoc.ToString();

                var reloadClient = new NatsROS.Core.Communication.RosServiceClient<ReloadTreeReq, ReloadTreeRes>(_nats, $"{targetBrain.Trim()}.bt.reload");
                var response = await reloadClient.CallAsync(new ReloadTreeReq(xmlContent), TimeSpan.FromSeconds(3));

                if (response != null && response.Success) MessageBox.Show("✅ 热重载成功！", "通知", MessageBoxButton.OK, MessageBoxImage.Information);
                else MessageBox.Show($"❌ 下发失败: {response?.Message ?? "大脑无响应"}", "错误");
            }
            catch (Exception ex) { MessageBox.Show($"异常: {ex.Message}"); }
        }

        // ==========================================
        // 核心交互：选中节点并动态注入高级 UI 控件
        // ==========================================
        private void TreeListBehavior_SelectedItemChanged(object sender, DevExpress.Xpf.Grid.SelectedItemChangedEventArgs e)
        {
            // 每次切换节点前，先把上一次动态生成的拦截规则清空
            PropGridNode.PropertyDefinitions.Clear();

            if (e.NewItem is BtEditorNodeModel model)
            {
                var nodeInstance = model.NodeInstance;

                // 【核心魔法】：利用反射，找出这个节点里所有挂了 [PointSelector] 的属性
                var props = nodeInstance.GetType().GetProperties();
                foreach (var prop in props)
                {
                    if (prop.GetCustomAttribute<PointSelectorAttribute>() != null)
                    {
                        // 为这个具体的属性（精确全称），动态创建一个带 [...] 按钮的拦截规则！
                        var def = new DevExpress.Xpf.PropertyGrid.PropertyDefinition { Path = prop.Name };

                        var btnSettings = new DevExpress.Xpf.Editors.Settings.ButtonEditSettings
                        {
                            AllowDefaultButton = true,
                            IsTextEditable = true
                        };

                        btnSettings.DefaultButtonClick += SmartPointButton_Click;
                        def.EditSettings = btnSettings;
                        PropGridNode.PropertyDefinitions.Add(def);
                    }
                }

                // 规则注入完毕后，再绑定真实数据对象
                PropGridNode.SelectedObject = nodeInstance;
            }
            else
            {
                PropGridNode.SelectedObject = null;
            }
        }

        // 拦截按钮点击，呼出聚合点位选择器 Dialog
        private void SmartPointButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is DevExpress.Xpf.Editors.ButtonEdit editor)
            {
                string currentValue = editor.EditValue?.ToString() ?? "";
                var dialog = new PointSelectorDialog(_nats, currentValue) { Owner = Window.GetWindow(this) };
                if (dialog.ShowDialog() == true)
                {
                    editor.EditValue = dialog.SelectedPointName;
                }
            }
        }

        // ==========================================
        // 树操作与序列化
        // ==========================================
        private void GridToolbox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (GridToolbox.SelectedItem is not AvailableNodeInfo selectedInfo) return;
            var parentModel = TreeListBehavior.SelectedItem as BtEditorNodeModel ?? _rootModel;

            if (parentModel!.NodeType == BtNodeType.Action || parentModel.NodeType == BtNodeType.Condition)
            {
                MessageBox.Show($"[{parentModel.Name}] 是动作节点，不支持添加子节点。请选中一个 Sequence 或 Root 节点后再试。", "无效操作", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var targetType = AppDomain.CurrentDomain.GetAssemblies().SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } }).FirstOrDefault(t => t.FullName == selectedInfo.TypeName);
                if (targetType != null)
                {
                    var instance = (BehaviorTreeNode)Activator.CreateInstance(targetType, selectedInfo.DisplayName)!;
                    var props = targetType.GetProperties().Where(p => p.GetCustomAttribute<BtPropAttribute>() != null);
                    foreach (var p in props)
                    {
                        var btAttr = p.GetCustomAttribute<BtPropAttribute>();
                        if (btAttr != null && !string.IsNullOrEmpty(btAttr.DefaultValue))
                        {
                            try { p.SetValue(instance, Convert.ChangeType(btAttr.DefaultValue, p.PropertyType)); } catch { }
                        }
                    }

                    var newModel = new BtEditorNodeModel(instance);
                    parentModel.Children.Add(newModel);
                    TreeListView.ExpandNode(TreeListView.GetNodeByContent(parentModel).RowHandle);
                }
            }
            catch (Exception ex) { MessageBox.Show($"添加节点失败: {ex.Message}"); }
        }

        private void PropGridNode_CellValueChanged(object sender, DevExpress.Xpf.PropertyGrid.CellValueChangedEventArgs e)
        {
            if (TreeListBehavior.SelectedItem is BtEditorNodeModel model) model.UpdateSummary();
        }

        private void BtnDeleteNode_Click(object sender, RoutedEventArgs e)
        {
            if (TreeListBehavior.SelectedItem is BtEditorNodeModel model)
            {
                if (model == _rootModel) { MessageBox.Show("Root 节点是世界之树的根基，不允许删除！"); return; }
                RemoveNodeRecursive(_rootModel!, model);
            }
        }

        private void BtnMoveUp_Click(object sender, RoutedEventArgs e)
        {
            if (TreeListBehavior.SelectedItem is not BtEditorNodeModel targetModel || targetModel == _rootModel) return;
            var parent = FindParent(_rootModel!, targetModel);
            if (parent != null)
            {
                int index = parent.Children.IndexOf(targetModel);
                if (index > 0) parent.Children.Move(index, index - 1);
            }
        }

        private void BtnMoveDown_Click(object sender, RoutedEventArgs e)
        {
            if (TreeListBehavior.SelectedItem is not BtEditorNodeModel targetModel || targetModel == _rootModel) return;
            var parent = FindParent(_rootModel!, targetModel);
            if (parent != null)
            {
                int index = parent.Children.IndexOf(targetModel);
                if (index < parent.Children.Count - 1) parent.Children.Move(index, index + 1);
            }
        }

        private BtEditorNodeModel? FindParent(BtEditorNodeModel currentRoot, BtEditorNodeModel target)
        {
            if (currentRoot.Children.Contains(target)) return currentRoot;
            foreach (var child in currentRoot.Children)
            {
                var found = FindParent(child, target);
                if (found != null) return found;
            }
            return null;
        }

        private bool RemoveNodeRecursive(BtEditorNodeModel parent, BtEditorNodeModel target)
        {
            if (parent.Children.Contains(target)) { parent.Children.Remove(target); return true; }
            foreach (var child in parent.Children) if (RemoveNodeRecursive(child, target)) return true;
            return false;
        }

        private XElement GenerateXmlNode(BtEditorNodeModel model)
        {
            var nodeObj = model.NodeInstance;
            var element = new XElement("Node");
            element.SetAttributeValue("type", nodeObj.GetType().FullName);
            element.SetAttributeValue("Id", nodeObj.Id);
            element.Add(new XElement("Name", nodeObj.Name));

            var props = nodeObj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanWrite && p.Name != "Id" && p.Name != "Name" &&
                (p.GetCustomAttribute<CategoryAttribute>() != null || p.GetCustomAttribute<DefaultValueAttribute>() != null || p.GetCustomAttribute<BtPropAttribute>() != null));

            foreach (var p in props)
            {
                var val = p.GetValue(nodeObj);
                if (val != null) element.Add(new XElement(p.Name, val.ToString()));
            }

            if (model.Children.Count > 0)
            {
                var childrenContainer = new XElement("Children");
                foreach (var child in model.Children) childrenContainer.Add(GenerateXmlNode(child));
                element.Add(childrenContainer);
            }
            return element;
        }

        private BtEditorNodeModel RestoreNodeFromXml(XElement xmlNode)
        {
            string typeFullName = xmlNode.Attribute("type")?.Value ?? "";
            string id = xmlNode.Attribute("Id")?.Value ?? Guid.NewGuid().ToString();
            string nodeName = xmlNode.Element("Name")?.Value ?? typeFullName.Split('.').Last();

            Type? csharpType = AppDomain.CurrentDomain.GetAssemblies().SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } }).FirstOrDefault(t => t.FullName == typeFullName);
            if (csharpType == null) throw new Exception($"找不到类型: {typeFullName}");

            var instance = (BehaviorTreeNode)Activator.CreateInstance(csharpType, nodeName)!;
            instance.Id = id;

            var props = csharpType.GetProperties();
            foreach (var propXml in xmlNode.Elements())
            {
                if (propXml.Name.LocalName == "Children" || propXml.Name.LocalName == "Name") continue;
                var prop = props.FirstOrDefault(p => p.Name.Equals(propXml.Name.LocalName, StringComparison.OrdinalIgnoreCase));
                if (prop != null && prop.CanWrite)
                {
                    try { prop.SetValue(instance, Convert.ChangeType(propXml.Value, prop.PropertyType)); } catch { }
                }
            }

            var model = new BtEditorNodeModel(instance);
            var childrenContainer = xmlNode.Element("Children");
            if (childrenContainer != null)
            {
                foreach (var childXml in childrenContainer.Elements("Node")) model.Children.Add(RestoreNodeFromXml(childXml));
            }
            return model;
        }

        public void Dispose() { }
    }
}