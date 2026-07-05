using DevExpress.Diagram.Core;
using DevExpress.Xpf.Diagram;
using NATS.Client.Core;
using NatsROS.Core.SystemMessages;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;
using UserControl = System.Windows.Controls.UserControl;

namespace NatsROS.Dashboard.Plugins.Introspection
{
    public partial class NodeGraphView : UserControl
    {
        private readonly INatsClient _nats;

        public NodeGraphView(INatsClient nats)
        {
            InitializeComponent();
            _nats = nats;
        }

        private async void BtnGenerateGraph_Click(object sender, RoutedEventArgs e)
        {
            if (_nats == null) return;
            DiagramGraph.Items.Clear();

            var drawnNodes = new Dictionary<string, DiagramShape>();
            var drawnTopics = new Dictionary<string, DiagramShape>();
            var drawnServices = new Dictionary<string, DiagramShape>();

            bool hideRosout = ChkHideRosout.IsChecked ?? false;
            bool hideParams = ChkHideParams.IsChecked ?? false;
            bool hideServices = ChkHideServices.IsChecked ?? false;
            bool hideDiscovery = ChkHideDiscovery.IsChecked ?? false;

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var replies = _nats.Connection.RequestManyAsync<NodeIntrospectionReq, NodeIntrospectionRes>(
                    "natsros.introspection.ping", new NodeIntrospectionReq(Guid.NewGuid().ToString()), cancellationToken: cts.Token);

                await foreach (var reply in replies)
                {
                    if (reply.Data == null) continue;
                    var info = reply.Data;

                    if (hideDiscovery && info.NodeName == "container_manager") continue;

                    if (!drawnNodes.TryGetValue(info.NodeName, out var nodeShape))
                    {
                        nodeShape = new DiagramShape { Content = info.NodeName, Shape = BasicShapes.Rectangle, Width = 160, Height = 45, Background = new SolidColorBrush(Colors.LightBlue), Foreground = new SolidColorBrush(Colors.Black), Stroke = new SolidColorBrush(Colors.SteelBlue), StrokeThickness = 2, FontWeight = FontWeights.Bold };
                        drawnNodes[info.NodeName] = nodeShape;
                        DiagramGraph.Items.Add(nodeShape);
                    }

                    foreach (var pub in info.Publishers) { if (hideRosout && pub == "rosout") continue; if (hideDiscovery && pub.Contains("discovery")) continue; AddConnector(nodeShape, GetOrCreateTopicShape(pub, drawnTopics), Colors.Green); }
                    foreach (var sub in info.Subscribers) { if (hideRosout && sub == "rosout") continue; if (hideDiscovery && sub.Contains("discovery")) continue; AddConnector(GetOrCreateTopicShape(sub, drawnTopics), nodeShape, Colors.DarkOrange); }

                    if (!hideServices)
                    {
                        foreach (var srv in info.ServiceServers) { if (hideParams && srv.Contains(".param.")) continue; if (hideDiscovery && srv.Contains("container.")) continue; AddConnector(GetOrCreateServiceShape(srv, drawnServices), nodeShape, Colors.Purple, true); }
                        foreach (var cli in info.ServiceClients) { if (hideParams && cli.Contains(".param.")) continue; if (hideDiscovery && cli.Contains("container.")) continue; AddConnector(nodeShape, GetOrCreateServiceShape(cli, drawnServices), Colors.MediumPurple, true); }
                    }
                }
                ApplyAutoLayout();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { MessageBox.Show($"渲染拓扑图失败: {ex.Message}"); }
        }

        private void AddConnector(DiagramItem begin, DiagramItem end, Color color, bool isDashed = false)
        {
            var connector = new DiagramConnector { BeginItem = begin, EndItem = end, Type = ConnectorType.Straight, Stroke = new SolidColorBrush(color) };
            if (isDashed) connector.StrokeDashArray = new DoubleCollection(new[] { 4.0, 2.0 });
            DiagramGraph.Items.Add(connector);
        }

        private void BtnAutoLayout_Click(object sender, RoutedEventArgs e) => ApplyAutoLayout();

        private void ApplyAutoLayout()
        {
            if (DiagramGraph.Items.Count > 0)
            {
                DiagramGraph.ApplySugiyamaLayout(Direction.Right);
                DiagramGraph.FitToDrawing();
            }
        }

        private DiagramShape GetOrCreateTopicShape(string name, Dictionary<string, DiagramShape> dict)
        {
            if (!dict.TryGetValue(name, out var shape)) { shape = new DiagramShape { Content = name, Shape = BasicShapes.Ellipse, Width = 160, Height = 45, Background = new SolidColorBrush(Colors.LightGreen), Foreground = new SolidColorBrush(Colors.Black), Stroke = new SolidColorBrush(Colors.DarkGreen), StrokeThickness = 1, FontWeight = FontWeights.Bold }; dict[name] = shape; DiagramGraph.Items.Add(shape); }
            return shape;
        }

        private DiagramShape GetOrCreateServiceShape(string name, Dictionary<string, DiagramShape> dict)
        {
            if (!dict.TryGetValue(name, out var shape)) { shape = new DiagramShape { Content = name, Shape = BasicShapes.Diamond, Width = 220, Height = 55, Background = new SolidColorBrush(Colors.Thistle), Foreground = new SolidColorBrush(Colors.Black), Stroke = new SolidColorBrush(Colors.Purple), StrokeThickness = 1 }; dict[name] = shape; DiagramGraph.Items.Add(shape); }
            return shape;
        }
    }
}
