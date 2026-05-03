using DevExpress.Diagram.Core;
using DevExpress.Xpf.Diagram;
using NATS.Client.Core;
using NatsROS.Core.SystemMessages;
using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Size = System.Windows.Size;
using UserControl = System.Windows.Controls.UserControl;

namespace NatsROS.Dashboard.Plugins.BehaviorTree
{
    /// <summary>
    /// BehaviorTreeView.xaml 的交互逻辑
    /// </summary>
    public partial class BehaviorTreeView : UserControl, IDisposable
    {
        private readonly INatsClient _nats;
        private readonly CancellationTokenSource _cts = new();
        private readonly Dictionary<string, DiagramShape> _shapeDict = new();
        private bool _topologyDrawn = false;

        public BehaviorTreeView(INatsClient nats)
        {
            InitializeComponent();
            _nats = nats;
            _ = ListenToBrainAsync(_cts.Token);
        }

        private async Task ListenToBrainAsync(CancellationToken ct)
        {
            try
            {
                // 1. 【核心修复】：主动出击！向大脑索要拓扑图！
                var topClient = new Core.Communication.RosServiceClient<BtTopologyReq, BtTopologyMsg>(_nats, "brain.bt.topology.request");

                // 写一个极其稳健的重试循环：如果大脑还没启动，UI 就每秒问一次，直到大脑上线并交出图谱！
                while (!_topologyDrawn && !ct.IsCancellationRequested)
                {
                    try
                    {
                        var topology = await topClient.CallAsync(new BtTopologyReq(), TimeSpan.FromSeconds(2), ct);
                        if (topology != null)
                        {
                            // 拿到图谱，切回主线程画树！
                            Dispatcher.Invoke(() => DrawTree(topology));
                        }
                    }
                    catch
                    {
                        // 大脑还没上线，静默等待 1 秒后继续问
                        await Task.Delay(1000, ct);
                    }
                }

                // 2. 监听状态波 (染色)
                var stateSub = _nats.SubscribeAsync<BtStateMsg>("brain.bt.state", cancellationToken: ct);
                await foreach (var msg in stateSub)
                {
                    if (msg.Data != null && _topologyDrawn)
                        Dispatcher.Invoke(() => UpdateColors(msg.Data));
                }
            }
            catch (OperationCanceledException) { }
        }

        private void DrawTree(BtTopologyMsg topology)
        {
            DiagramTree.Items.Clear();
            _shapeDict.Clear();

            // 生成节点
            foreach (var node in topology.Nodes)
            {
                var (shapeType, bgColor, fgColor, strokeColor, thickness) = GetVisualStyle(node.Type);

                var shape = new DiagramShape
                {
                    Content = node.Name,
                    Shape = shapeType,
                    Width = 160,
                    Height = 50, // 与 Editor 保持一致的大小
                    Background = new SolidColorBrush(bgColor),
                    Foreground = new SolidColorBrush(fgColor),
                    Stroke = new SolidColorBrush(strokeColor),
                    StrokeThickness = thickness,
                    FontWeight = FontWeights.Bold,
                    Tag = node.Type // 【魔法】：把原始类型藏在 Tag 里，方便 Idle 时恢复颜色！
                };

                _shapeDict[node.Id] = shape;
                DiagramTree.Items.Add(shape);
            }

            // 生成连线
            foreach (var node in topology.Nodes)
            {
                if (node.ChildrenIds == null) continue;
                foreach (var childId in node.ChildrenIds)
                {
                    if (_shapeDict.TryGetValue(node.Id, out var parentShape) && _shapeDict.TryGetValue(childId, out var childShape))
                    {
                        //DiagramTree.Items.Add(new DiagramConnector 
                        //{ 
                        //    BeginItem = parentShape, 
                        //    EndItem = childShape, 
                        //    Type = ConnectorType.Straight, 
                        //    Stroke = new SolidColorBrush(Colors.Gray) });

                        DiagramTree.Items.Add(new DiagramConnector
                        {
                            BeginItem = parentShape,
                            BeginItemPointIndex = 2, // 锚定底部
                            EndItem = childShape,
                            EndItemPointIndex = 0,   // 锚定顶部
                            Type = ConnectorType.Straight, // 平滑曲线
                            Stroke = new SolidColorBrush(Color.FromRgb(130, 140, 150)),
                            StrokeThickness = 2,
                            EndArrow = ArrowDescriptions.Filled90, // 增加末端箭头
                            EndArrowSize = new Size(15, 15)
                        });
                    }
                }
            }

            // 【自上而下的树状排版！】
            DiagramTree.ApplyTreeLayout(Direction.Down);
            DiagramTree.FitToDrawing();
            _topologyDrawn = true;
        }

        private void UpdateColors(BtStateMsg stateMsg)
        {
            foreach (var kvp in stateMsg.NodeStates)
            {
                if (_shapeDict.TryGetValue(kvp.Key, out var shape))
                {
                    if (shape.Tag is BtNodeType originalType)
                    {
                        // 核心魔法：如果是 Idle，恢复它的“基因原色”；否则亮起状态灯！
                        Color newColor = kvp.Value switch
                        {
                            BtNodeStatus.Idle => GetVisualStyle(originalType).Bg,
                            BtNodeStatus.Running => Colors.Yellow,   // 正在干活，亮黄色
                            BtNodeStatus.Success => Colors.LimeGreen,// 成功，亮绿色
                            BtNodeStatus.Failure => Colors.Red,      // 失败，亮红色
                            _ => Colors.LightGray
                        };

                        shape.Background = new SolidColorBrush(newColor);

                        // 当变成黄绿红时，把字变成黑色以保证高对比度看清内容；恢复 Idle 时变回白色/黄色
                        shape.Foreground = kvp.Value == BtNodeStatus.Idle
                            ? new SolidColorBrush(GetVisualStyle(originalType).Fg)
                            : new SolidColorBrush(Colors.Black);
                    }
                }
            }
        }

        private (ShapeDescription Shape, Color Bg, Color Fg, Color Stroke, double StrokeThickness) GetVisualStyle(BtNodeType type)
        {
            return type switch
            {
                // 对标我们的 Editor 配色方案
                // (注意枚举里我们为了兼容，之前把 Root 映射成了 Sequence 或者直接通过名字判定，这里做兜底)
                BtNodeType.Sequence => (BasicShapes.Rectangle, Color.FromRgb(0, 122, 204), Colors.White, Color.FromRgb(30, 30, 30), 2),
                BtNodeType.Selector => (BasicShapes.Diamond, Color.FromRgb(156, 39, 176), Colors.White, Color.FromRgb(30, 30, 30), 2),
                BtNodeType.Condition => (BasicShapes.Diamond, Colors.Orange, Colors.White, Color.FromRgb(30, 30, 30), 2),
                BtNodeType.Action => (BasicShapes.RoundedRectangle, Colors.Gray, Colors.White, Color.FromRgb(30, 30, 30), 2),
                _ => (BasicShapes.Rectangle, Color.FromRgb(45, 45, 48), Color.FromRgb(250, 173, 20), Color.FromRgb(250, 173, 20), 3) // 默认为 Root 样式
            };
        }


        public void Dispose() => _cts.Cancel();
    }
}
