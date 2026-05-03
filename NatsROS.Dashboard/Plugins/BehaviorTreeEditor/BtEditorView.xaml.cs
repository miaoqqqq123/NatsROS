using DevExpress.Diagram.Core;
using DevExpress.Diagram.Core.Shapes;
using DevExpress.Xpf.Diagram;
using Hexiv.BehaviorTree.Attributes;
using Hexiv.BehaviorTree.Core;
using NATS.Client.Core;
using NatsROS.Core.Attributes;
using NatsROS.Dashboard.Plugins.BehaviorTree;
using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.TrayNotify;
using Color = System.Windows.Media.Color; // 使用我们之前定义的标签
using MessageBox = System.Windows.MessageBox;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using UserControl = System.Windows.Controls.UserControl;

namespace NatsROS.Dashboard.Plugins.BehaviorTreeEditor
{
    public partial class BtEditorView : UserControl, IDisposable
    {
        private readonly INatsClient _nats;
        private const string STENCIL_ID = "HexivBTStencil";

        // 锁定 Root 节点，不让用户删除
        private DiagramShape? _rootShape;
        private bool _isImporting = false; // 【新增】导入状态锁

        public BtEditorView(INatsClient nats)
        {
            InitializeComponent();
            _nats = nats;

            // 初始化画板工具箱
            RegisterBehaviorTreeStencil();
            InitializeSmartCanvas();

            var itemsCollection = (INotifyCollectionChanged)DiagramCanvas.Items;
            itemsCollection.CollectionChanged += DiagramCanvas_Items_CollectionChanged;

            // 【新增魔法】：监听右侧属性网格的修改！
            PropGridNode.CellValueChanged += PropGridNode_CellValueChanged;
        }

        private void PropGridNode_CellValueChanged(object sender, DevExpress.Xpf.PropertyGrid.CellValueChangedEventArgs e)
        {
            // 如果用户改了 "Name" 属性，立即同步到画板上的图形！
            if (e.Row.FullPath == "Name" && DiagramCanvas.PrimarySelection is DiagramShape shape && shape.Tag is BehaviorTreeNode node)
            {
                shape.Content = node.Name;
                RefreshTreeLayoutAndIndices(); // 重算[1] [2] 序号
            }
        }

        // ==========================================
        // 智能画布核心引擎：自动吸附与触发排版
        // ==========================================
        private void DiagramCanvas_Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            // 【新增拦截】：如果是代码在批量导入 XML，绝对不要触发自动连线！
            if (_isImporting) return;

            // 1. 如果是新增了图形 (用户从左侧拖出来的)
            if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
            {
                foreach (var newItem in e.NewItems.OfType<DiagramShape>())
                {
                    HandleNewShapeAdded(newItem);
                }
            }

            // 2. 只要画布上有任何东西增删，立即触发全盘序号重算
            DebounceRefreshTree();
        }

        private void HandleNewShapeAdded(DiagramShape newShape)
        {
            if (newShape == _rootShape) return;

            // 寻找画布上离它最近、且在它“上方”的节点作为潜在父亲
            var potentialParent = DiagramCanvas.Items.OfType<DiagramShape>()
                .Where(s => s != newShape && s.Position.Y < newShape.Position.Y)
                .OrderBy(s => Math.Pow(s.Position.X - newShape.Position.X, 2) + Math.Pow(s.Position.Y - newShape.Position.Y, 2))
                .FirstOrDefault();

            if (potentialParent != null)
            {
                double distance = Math.Sqrt(Math.Pow(potentialParent.Position.X - newShape.Position.X, 2) + Math.Pow(potentialParent.Position.Y - newShape.Position.Y, 2));
                if (distance < 400) // 吸附距离设为 400 像素
                {
                    // 【极其关键的防灾代码】：Dispatcher.InvokeAsync
                    // 因为我们正在响应集合变化的事件，WPF 严禁在这个事件内部直接再次修改集合！
                    // 所以我们必须把“画线”这个动作，塞到 UI 消息队列的末尾去异步执行！
                    Dispatcher.InvokeAsync(() =>
                    {
                        DiagramCanvas.Items.Add(new DiagramConnector
                        {
                            BeginItem = potentialParent,
                            BeginItemPointIndex = 2, // 【核心魔法1】：强行锚定父节点的底部 (Bottom = 2)

                            EndItem = newShape,
                            EndItemPointIndex = 0,   // 【核心魔法2】：强行锚定子节点的顶部 (Top = 0)

                            Type = ConnectorType.RightAngle, // 【核心优化】：改为高级平滑曲线
                            Stroke = new SolidColorBrush(Color.FromRgb(130, 140, 150)),
                            StrokeThickness = 2,
                            EndArrow = ArrowDescriptions.Filled90,
                            EndArrowSize = new Size(15, 15)
                        });
                    });
                }
            }
        }

        // ==========================================
        // 0. 画布初始化：种下“世界之树”的种子 (Root Node)
        // ==========================================
        private void InitializeSmartCanvas()
        {
            // 将 Tag 替换为真实的 RootNode，并赋值给一个固定的 ID
            var rootInstance = new RootNode("任务起点 (Root)");

            // 创建一个无法被删除、极其炫酷的 Root 节点
            _rootShape = new DiagramShape
            {
                Shape = BasicShapes.Rectangle,
                Content = "[0] 🚩 任务起点 (Root)",
                Width = 180,
                Height = 50,
                Position = new Point(400, 50),
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 48)),   // 深空灰
                Foreground = new SolidColorBrush(Color.FromRgb(250, 173, 20)), // 警告黄
                Stroke = new SolidColorBrush(Color.FromRgb(250, 173, 20)),
                StrokeThickness = 3,
                FontWeight = FontWeights.Bold,
                CanDelete = false, // 【防呆】：绝不允许删除根节点！
                CanCopy = false,
                Tag = rootInstance // 伪装成一个 Sequence 以便提取 XML
            };
            DiagramCanvas.Items.Add(_rootShape);
        }

        // ==========================================
        // 1. 扫描 DLL，注册自定义工具箱图元
        // ==========================================
        private void RegisterBehaviorTreeStencil()
        {
            // 创建一个专属工具箱分类
            var stencil = new DiagramStencil(STENCIL_ID, "⚙️ 工业行为树组件库");

            // 注册连线工具
            stencil.RegisterTool(new FactoryItemTool("Connector", () => "🔗 手动连线", diagram => new DiagramConnector()));

            // 注册核心控制流 (使用深色高级 UI 风格)
            RegisterNodeToStencil(stencil, "Sequence", "顺序执行 (Sequence)", BasicShapes.Rectangle, Color.FromRgb(0, 122, 204), typeof(SequenceNode));
            RegisterNodeToStencil(stencil, "Fallback", "失败挽救 (Fallback)", BasicShapes.Diamond, Color.FromRgb(156, 39, 176), typeof(FallbackNode));

            // 动态扫描业务节点
            var nodeTypes = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                .Where(t => typeof(BehaviorTreeNode).IsAssignableFrom(t) && !t.IsAbstract && t.GetCustomAttribute<Hexiv.BehaviorTree.Attributes.BtNodeAttribute>() != null);

            foreach (var type in nodeTypes)
            {
                var attr = type.GetCustomAttribute<RosNodeAttribute>();
                string displayName = attr?.DisplayName ?? type.Name;
                // 业务节点统一用深绿色圆角矩形
                RegisterNodeToStencil(stencil, type.Name, displayName, BasicShapes.RoundedRectangle, Color.FromRgb(46, 125, 50), type);
            }

            // 把我们的工具箱注册进全局 Diagram 引擎
            DiagramToolboxRegistrator.RegisterStencil(stencil);

            // 命令画板只显示我们的工具箱
            DiagramCanvas.SelectedStencils = new StencilCollection { STENCIL_ID };
        }

        private void RegisterNodeToStencil(DiagramStencil stencil, string id, string name, ShapeDescription shape, Color color, Type csharpType)
        {
            // 定义拖拽生成规则：当用户拖出一个图形时，在它的 Tag 里面塞入一个真实的 C# 对象实例！
            var itemTool = new FactoryItemTool(id, () => name, diagram =>
            {
                var diagramShape = new DiagramShape
                {
                    Shape = shape,
                    Content = name,
                    Width = 160,
                    Height = 50,
                    Background = new SolidColorBrush(color),
                    Foreground = new SolidColorBrush(Colors.White),
                    Stroke = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                    StrokeThickness = 2,
                    FontWeight = FontWeights.Bold
                };

                // 【核心魔法】：反射实例化 C# 业务对象，并藏在图形影子里！
                try
                {
                    var instance = Activator.CreateInstance(csharpType, name);
                    diagramShape.Tag = instance; // Tag 就是它的灵魂
                }
                catch { }

                return diagramShape;
            });

            stencil.RegisterTool(itemTool);
        }

        // ==========================================
        // 2. 点击画板上的图形，右侧瞬间展现其参数属性
        // ==========================================
        private void DiagramCanvas_SelectionChanged(object sender, DiagramSelectionChangedEventArgs e)
        {
            if (DiagramCanvas.PrimarySelection is DiagramShape shape && shape.Tag != null && shape != _rootShape)
                PropGridNode.SelectedObject = shape.Tag;
            else
                PropGridNode.SelectedObject = null;
        }

        // ==========================================
        // 3. 辅助功能按钮
        // ==========================================
        private void BtnAutoLayout_Click(object sender, RoutedEventArgs e)
        {
            RefreshTreeLayoutAndIndices();
        }

        private void BtnLoadXml_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "XML Files (*.xml)|*.xml" };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    _isImporting = true; // 开启保护锁

                    DiagramCanvas.Items.Clear(); // 清空旧画布
                    _rootShape = null;

                    var xmlStr = System.IO.File.ReadAllText(dlg.FileName);
                    var doc = XDocument.Parse(xmlStr);
                    var btElement = doc.Descendants("BehaviorTree").FirstOrDefault() ?? doc.Root;

                    // 寻找顶层的 RootNode
                    var firstNodeXml = btElement?.Elements("Node").FirstOrDefault();

                    if (firstNodeXml != null)
                    {
                        // 从真正的 Root 开始递归还原，不传 parentShape
                        RestoreShapeFromXml(firstNodeXml, null);
                    }

                    _isImporting = false; // 关闭保护锁

                    // 强行触发一次排版和序号重算
                    RefreshTreeLayoutAndIndices();

                    MessageBox.Show("✅ 行为树配方导入成功！", "导入成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    _isImporting = false;
                    MessageBox.Show($"导入 XML 失败: {ex.Message}", "语法错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BtnSaveXml_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var connectors = DiagramCanvas.Items.OfType<DiagramConnector>().ToList();
                var parentToChildren = new Dictionary<DiagramShape, List<DiagramShape>>();
                foreach (var conn in connectors)
                {
                    if (conn.BeginItem is DiagramShape p && conn.EndItem is DiagramShape c)
                    {
                        if (!parentToChildren.ContainsKey(p)) parentToChildren[p] = new List<DiagramShape>();
                        parentToChildren[p].Add(c);
                    }
                }

                foreach (var parent in parentToChildren.Keys.ToList())
                {
                    parentToChildren[parent] = parentToChildren[parent].OrderBy(c => c.Position.X).ToList();
                }

                XElement rootTreeXml = GenerateXmlNode(_rootShape!, parentToChildren);
                XDocument xmlDoc = new XDocument(new XElement("root", new XElement("BehaviorTree", new XAttribute("ID", "MainTask"), rootTreeXml)));

                var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "XML Files (*.xml)|*.xml", FileName = "MyBehaviorTree.xml" };
                if (dlg.ShowDialog() == true)
                {
                    xmlDoc.Save(dlg.FileName);
                    MessageBox.Show("✅ 行为树配方导出成功！", "导出成功");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导出 XML 失败: {ex.Message}", "错误");
            }
        }


        private void BtnDeploy_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("将 XML 通过 NATS RPC 发送给 L1_MissionControl 大脑节点热重载！（功能待开发）");
        }

        public void Dispose() { }


        /// <summary>
        /// 递归生成 XML 节点与动态属性提取
        /// </summary>
        /// <param name="shape"></param>
        /// <param name="parentToChildren"></param>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        private XElement GenerateXmlNode(DiagramShape shape, Dictionary<DiagramShape, List<DiagramShape>> parentToChildren)
        {
            var nodeObj = shape.Tag as BehaviorTreeNode;
            if (nodeObj == null) throw new Exception($"图形 '{shape.Content}' 无效");

            // 极其干净的标准 OpenTAP 格式
            var element = new XElement("Node");

            var typeName = nodeObj.GetType().FullName;
            element.SetAttributeValue("type", typeName);
            element.SetAttributeValue("Id", nodeObj.Id);
            element.Add(new XElement("Name", nodeObj.Name));

            // 将所有带有 Category 或 DefaultValue 的属性，作为 Element 写入！
            var props = nodeObj.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Where(p => p.CanWrite && (p.GetCustomAttribute<System.ComponentModel.CategoryAttribute>() != null || p.GetCustomAttribute<System.ComponentModel.DefaultValueAttribute>() != null));

            foreach (var p in props)
            {
                if (p.Name == "Id" || p.Name == "Name") continue;
                var val = p.GetValue(nodeObj);
                if (val != null) element.Add(new XElement(p.Name, val.ToString()));
            }

            // 递归处理子节点
            if (parentToChildren.TryGetValue(shape, out var children) && children.Count > 0)
            {
                var childrenContainer = new XElement("Children");
                foreach (var child in children)
                {
                    childrenContainer.Add(GenerateXmlNode(child, parentToChildren));
                }
                element.Add(childrenContainer);
            }

            return element;
        }


        /// <summary>
        /// 递归逆向还原XML 节点与动态属性提取
        /// </summary>
        /// <param name="xmlNode"></param>
        /// <param name="parentShape"></param>
        /// <returns></returns>
        private DiagramShape RestoreShapeFromXml(XElement xmlNode, DiagramShape? parentShape)
        {
            string typeFullName = xmlNode.Attribute("type")?.Value ?? throw new Exception("缺少 type 属性");
            string id = xmlNode.Attribute("Id")?.Value ?? Guid.NewGuid().ToString();
            string nodeName = xmlNode.Element("Name")?.Value ?? typeFullName.Split('.').Last();

            // 全网反射查找类 (使用 FullName 精确匹配！)
            Type? csharpType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                .FirstOrDefault(t => t.FullName == typeFullName);

            if (csharpType == null)
                throw new Exception($"未能在内存中找到类型: {typeFullName}。请确保对应的插件 DLL 已放置在运行目录下！");

            // 1. 优先实例化 C# 对象，让对象自己告诉我们它是什么类型！
            var instance = (BehaviorTreeNode)Activator.CreateInstance(csharpType, nodeName)!;

            // 强行恢复灵魂 ID
            instance.Id = id;

            // 2. 动态把 XML 里的参数，塞回 C# 对象的属性里
            var props = csharpType.GetProperties();
            foreach (var propXml in xmlNode.Elements())
            {
                if (propXml.Name.LocalName == "Children" || propXml.Name.LocalName == "Name") continue;
                var prop = props.FirstOrDefault(p => p.Name.Equals(propXml.Name.LocalName, StringComparison.OrdinalIgnoreCase));
                if (prop != null && prop.CanWrite)
                {
                    try 
                    { 
                        prop.SetValue(instance, Convert.ChangeType(propXml.Value, prop.PropertyType)); 
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"警告：节点 '{nodeName}' 无法正确实例化！\n错误信息: {ex.Message}", "属性恢复失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }

            // 3. 【核心升级 4】：基于对象的 NodeType 枚举，决定 UI 形状和颜色！彻底消灭魔法字符串！
            ShapeDescription shapeDesc = BasicShapes.Rectangle;
            Color foreColor = Colors.White;
            Color backColor = Color.FromRgb(46, 125, 50);
            Color strokeColor = Colors.Black;

            //默认节点样式
            DiagramShape shape = new DiagramShape
            {
                Shape = BasicShapes.RoundedRectangle,
                Content = nodeName,
                Width = 160,
                Height = 50,
                Background = new SolidColorBrush(backColor),
                Foreground = new SolidColorBrush(foreColor),
                Stroke = new SolidColorBrush(strokeColor),
                StrokeThickness = 2,
                FontWeight = FontWeights.Bold,
                Tag = instance
            };

            switch (instance.NodeType)
            {
                case BTNodeType.Root:
                    shape.Shape = BasicShapes.Rectangle;
                    shape.Foreground = new SolidColorBrush(Color.FromRgb(250, 173, 20)); // 警告黄
                    shape.Background = new SolidColorBrush(Color.FromRgb(45, 45, 48));   // 深空灰
                    shape.Stroke = new SolidColorBrush(Color.FromRgb(250, 173, 20));
                    shape.Width = 180;
                    shape.Height = 50;
                    shape.Position = new Point(400, 50);
                    shape.FontWeight = FontWeights.Bold;
                    shape.CanDelete = false; // 【防呆】：绝不允许删除根节点！
                    shape.CacheMode = new BitmapCache(); // 【性能优化】：Root 节点永远不变，开启位图缓存提升性能！
                    shape.StrokeThickness = 3;
                    shape.CanCopy = false;
                    break;
                case BTNodeType.Sequence:
                    shape.Shape = BasicShapes.Rectangle;
                    shape.Background = new SolidColorBrush(Color.FromRgb(0, 122, 204)); // 深蓝
                    break;
                case BTNodeType.Selector:
                    shape.Shape = BasicShapes.Diamond;
                    shape.Background = new SolidColorBrush(Color.FromRgb(156, 39, 176)); // 紫色
                    break;
                case BTNodeType.Decorator:
                    shape.Shape = BasicShapes.Diamond;
                    shape.Background = new SolidColorBrush(Color.FromRgb(255, 165, 0)); // 橙色
                    break;
                case BTNodeType.Action:
                case BTNodeType.Condition:
                default:
                    shape.Shape = BasicShapes.RoundedRectangle;
                    shape.Background = new SolidColorBrush(Color.FromRgb(46, 125, 50)); // 深绿
                    break;
            }

            // 如果反序列化的是根节点，认祖归宗！
            if (instance.NodeType == BTNodeType.Root && parentShape == null)
            {
                _rootShape = shape;
            }

            DiagramCanvas.Items.Add(shape);

            // 5. 连线 (子连父)
            if (parentShape != null)
            {
                DiagramCanvas.Items.Add(new DiagramConnector
                {
                    BeginItem = parentShape,
                    BeginItemPointIndex = 2,
                    EndItem = shape,
                    EndItemPointIndex = 0,
                    Type = ConnectorType.RightAngle,
                    Stroke = new SolidColorBrush(Color.FromRgb(130, 140, 150)),
                    StrokeThickness = 2,
                    EndArrow = ArrowDescriptions.Filled90,
                    EndArrowSize = new Size(15, 15)
                });
            }

            // 6. 递归处理所有子节点
            var childrenContainer = xmlNode.Element("Children");
            if (childrenContainer != null)
            {
                foreach (var childXml in childrenContainer.Elements("Node"))
                {
                    RestoreShapeFromXml(childXml, shape);
                }
            }

            return shape;
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {

        }

        private void DiagramCanvas_ConnectionChanged(object sender, DiagramConnectionChangedEventArgs e)
        {
            DebounceRefreshTree();
        }


        private bool _isRefreshing = false;
        private async void DebounceRefreshTree()
        {
            if (_isRefreshing) return;
            _isRefreshing = true;
            await Task.Delay(100); // 防抖，避免一瞬间拖拽触发几十次

            RefreshTreeLayoutAndIndices();
            _isRefreshing = false;
        }

        private void RefreshTreeLayoutAndIndices()
        {
            if (_rootShape == null) return;

            var connectors = DiagramCanvas.Items.OfType<DiagramConnector>().ToList();
            var parentToChildren = new Dictionary<DiagramShape, List<DiagramShape>>();

            foreach (var conn in connectors)
            {
                if (conn.BeginItem is DiagramShape parent && conn.EndItem is DiagramShape child)
                {
                    if (!parentToChildren.ContainsKey(parent)) parentToChildren[parent] = new List<DiagramShape>();
                    parentToChildren[parent].Add(child);
                }
            }

            // 按照 X 坐标（从左到右）严格排序子节点
            foreach (var parent in parentToChildren.Keys.ToList())
            {
                parentToChildren[parent] = parentToChildren[parent].OrderBy(c => c.Position.X).ToList();
            }

            // 深度优先搜索 (DFS)，打上执行序号角标！
            int executionOrder = 0;
            void DFS(DiagramShape currentShape)
            {
                var nodeObj = currentShape.Tag as BehaviorTreeNode;
                if (nodeObj != null)
                {
                    // 把原来的名字提炼出来，加上 [1], [2] 这样的前缀
                    string originalName = nodeObj.Name;
                    currentShape.Content = $"[{executionOrder++}] {originalName}";
                }

                if (parentToChildren.TryGetValue(currentShape, out var children))
                {
                    foreach (var child in children) DFS(child);
                }
            }

            DFS(_rootShape);

            // 重新极其优美地排版！
            DiagramCanvas.ApplySugiyamaLayout(Direction.Down);
            DiagramCanvas.FitToDrawing();
        }
    }
}