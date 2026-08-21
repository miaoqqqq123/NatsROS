using HelixToolkit.Wpf;
using NATS.Client.Core;
using NatsROS.Core.Communication;
using NatsROS.Core.Parameters;
using NatsROS.Core.SystemMessages;
using NatsROS.Messages.GeometryMsgs;
using NatsROS.Messages.MES;
using NatsROS.Messages.Motion;
using NatsROS.Messages.SensorMsgs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;
using UserControl = System.Windows.Controls.UserControl;

namespace NatsROS.Dashboard.Plugins.DigitalTwin
{
    public partial class DigitalTwinView : UserControl, IDisposable
    {
        private readonly INatsClient _nats;
        private readonly CancellationTokenSource _cts = new();

        // 硬件与业务客户端
        private RosActionClient<DispenserMoveGoal, DispenserMoveFeedback, DispenserMoveResult> _moveClient;
        private RosActionClient<DispenserTrajectoryGoal, DispenserMoveFeedback, DispenserMoveResult> _trajClient;
        private RosServiceClient<ValveControlReq, ValveControlRes> _valveClient;
        private RosServiceClient<StartTreeReq, StartTreeRes> _brainStartClient;

        private RosServiceClient<FindMarkReq, FindMarkRes> _visionClient;
        private RosParameterClient _visionParamClient;

        // 节点路由名
        private const string NODE_DISPENSER = "simulateddispensernode_1";
        private const string NODE_VISION = "simulatedvisionnode_1";
        private const string NODE_AXIS_Y1 = "axis_y1";
        private const string NODE_AXIS_Y2 = "axis_y2";

        // 实时坐标与画线状态
        private double _liveX = 0, _liveZ = 50;
        private double _liveY1 = 100, _liveY2 = 100;
        private Point3D _lastPoint = new Point3D(0, 0, 50);

        // CAM 特征提取缓存
        private MeshGeometry3D? _selectedMesh;
        private List<Vector3> _generatedPath = new();

        public DigitalTwinView(INatsClient nats)
        {
            InitializeComponent();
            _nats = nats;

            GlueLines.Points = new Point3DCollection();
            MoveLines.Points = new Point3DCollection();
            TeachLines.Points = new Point3DCollection();

            // 初始矩阵位置
            DispenserHeadModel.Transform = new TranslateTransform3D(0, 0, 50);
            StationY1Model.Transform = new TranslateTransform3D(0, 100, 0);
            StationY2Model.Transform = new TranslateTransform3D(0, 100, 0);

            _moveClient = new RosActionClient<DispenserMoveGoal, DispenserMoveFeedback, DispenserMoveResult>(_nats, $"{NODE_DISPENSER}.move");
            _trajClient = new RosActionClient<DispenserTrajectoryGoal, DispenserMoveFeedback, DispenserMoveResult>(_nats, $"{NODE_DISPENSER}.trajectory");
            _valveClient = new RosServiceClient<ValveControlReq, ValveControlRes>(_nats, $"{NODE_DISPENSER}.valve");

            _visionClient = new RosServiceClient<FindMarkReq, FindMarkRes>(_nats, $"{NODE_VISION}.find_mark");
            _visionParamClient = new RosParameterClient(_nats, NODE_VISION);
            _brainStartClient = new RosServiceClient<StartTreeReq, StartTreeRes>(_nats, "brain_dispenser.bt.start");

            Viewport3D.PreviewMouseLeftButtonDown += Viewport3D_PreviewMouseLeftButtonDown;
            _ = ListenToTelemetryAsync(_cts.Token);
        }

        // ==========================================
        // CAD 导入
        // ==========================================
        private void BtnLoadProduct_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "3D Models (*.stl;*.obj)|*.stl;*.obj" };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    Model3DGroup modelGroup = new ModelImporter().Load(dlg.FileName);
                    foreach (var geometryModel in modelGroup.Children)
                        if (geometryModel is GeometryModel3D gm) gm.Material = MaterialHelper.CreateMaterial(Colors.SlateGray);

                    // 【核心】：挂载到 Y1 平台内部，产品会跟着 Y1 平台移动！
                    ProductY1Container.Content = modelGroup;
                    Viewport3D.ZoomExtents();
                }
                catch (Exception ex) { MessageBox.Show($"加载失败: {ex.Message}"); }
            }
        }

        private void BtnLoadMachine_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "3D Models (*.stl;*.obj)|*.stl;*.obj" };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    Model3DGroup modelGroup = new ModelImporter().Load(dlg.FileName);
                    foreach (var geometryModel in modelGroup.Children)
                        if (geometryModel is GeometryModel3D gm) gm.Material = MaterialHelper.CreateMaterial(Colors.Orange);

                    DispenserHeadModel.Content = modelGroup;
                    if (DefaultHead != null) DefaultHead.Visible = false;
                    Viewport3D.ZoomExtents();
                }
                catch (Exception ex) { MessageBox.Show($"加载失败: {ex.Message}"); }
            }
        }

        // ==========================================
        // 双工位遥测与相对坐标计算
        // ==========================================
        private async Task ListenToTelemetryAsync(CancellationToken ct)
        {
            try
            {
                var feedbackSub = new RosSubscriber<ActionFeedback<DispenserMoveFeedback>>(_nats, $"{NODE_DISPENSER}.move.feedback", RosQosProfile.SensorData);
                var trajFeedbackSub = new RosSubscriber<ActionFeedback<DispenserMoveFeedback>>(_nats, $"{NODE_DISPENSER}.trajectory.feedback", RosQosProfile.SensorData);
                var axisY1Sub = new RosSubscriber<ActionFeedback<AxisMoveFeedback>>(_nats, $"{NODE_AXIS_Y1}.move.feedback", RosQosProfile.SensorData);
                var axisY2Sub = new RosSubscriber<ActionFeedback<AxisMoveFeedback>>(_nats, $"{NODE_AXIS_Y2}.move.feedback", RosQosProfile.SensorData);

                _ = Task.Run(async () => { await foreach (var msg in feedbackSub.SubscribeAsync(ct)) ProcessDispenserFeedback(msg.Data); }, ct);
                _ = Task.Run(async () => { await foreach (var msg in trajFeedbackSub.SubscribeAsync(ct)) ProcessDispenserFeedback(msg.Data); }, ct);
                _ = Task.Run(async () => { await foreach (var msg in axisY1Sub.SubscribeAsync(ct)) ProcessY1Feedback(msg.Data); }, ct);
                _ = Task.Run(async () => { await foreach (var msg in axisY2Sub.SubscribeAsync(ct)) ProcessY2Feedback(msg.Data); }, ct);
            }
            catch (OperationCanceledException) { }
        }

        private void ProcessDispenserFeedback(DispenserMoveFeedback fb)
        {
            Dispatcher.InvokeAsync(() =>
            {
                _liveX = fb.CurrentX; _liveZ = fb.CurrentZ;
                TxtLiveX.Text = _liveX.ToString("F2");
                TxtLiveZ.Text = _liveZ.ToString("F2");

                // 机头只管 X 和 Z，Y 相对世界为 0
                DispenserHeadModel.Transform = new TranslateTransform3D(_liveX, 0, _liveZ + 10);

                DrawTraceLine(fb);
            });
        }

        private void ProcessY1Feedback(AxisMoveFeedback fb)
        {
            Dispatcher.InvokeAsync(() =>
            {
                _liveY1 = fb.CurrentPosition;
                TxtLiveY1.Text = _liveY1.ToString("F2");
                StationY1Model.Transform = new TranslateTransform3D(0, _liveY1 - 100, 0);
            });
        }

        private void ProcessY2Feedback(AxisMoveFeedback fb)
        {
            Dispatcher.InvokeAsync(() =>
            {
                _liveY2 = fb.CurrentPosition;
                TxtLiveY2.Text = _liveY2.ToString("F2");
                StationY2Model.Transform = new TranslateTransform3D(0, _liveY2 - 100, 0);
            });
        }

        private void DrawTraceLine(DispenserMoveFeedback fb)
        {
            // 谁靠近机头(Y=0)，线就画在谁身上。相对坐标合成！
            double activeY = 0;
            if (Math.Abs(_liveY1) < 50) activeY = _liveY1;
            else if (Math.Abs(_liveY2) < 50) activeY = _liveY2;

            var currentPoint = new Point3D(_liveX, -activeY, _liveZ);

            if (_lastPoint.DistanceTo(currentPoint) > 0.1)
            {
                if (fb.IsValveOpen) { GlueLines.Points.Add(_lastPoint); GlueLines.Points.Add(currentPoint); }
                else { MoveLines.Points.Add(_lastPoint); MoveLines.Points.Add(currentPoint); }
            }
            _lastPoint = currentPoint;
        }

        // ==========================================
        // 视觉模拟与大脑呼叫
        // ==========================================
        private async void BtnSimulateSkew_Click(object sender, RoutedEventArgs e)
        {
            BtnSimulateSkew.IsEnabled = false;
            try
            {
                var rand = new Random();
                double randOffsetX = rand.NextDouble() * 30 - 15;
                double randOffsetY = rand.NextDouble() * 30 - 15;
                double randAngle = rand.NextDouble() * 40 - 20;

                // 写入视觉节点骗大脑
                await _visionParamClient.SetAsync("TruthOffsetX", randOffsetX.ToString());
                await _visionParamClient.SetAsync("TruthOffsetY", randOffsetY.ToString());
                await _visionParamClient.SetAsync("TruthAngle", randAngle.ToString());

                // 转动 Y1 容器里的产品
                var transformGroup = new Transform3DGroup();
                transformGroup.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 0, 1), randAngle)));
                transformGroup.Children.Add(new TranslateTransform3D(randOffsetX, randOffsetY, 0));
                ProductY1Container.Transform = transformGroup;

                MessageBox.Show($"产品已被放歪！\n偏差X: {randOffsetX:F2} \n偏差Y: {randOffsetY:F2} \n旋转: {randAngle:F2}°");
            }
            finally { BtnSimulateSkew.IsEnabled = true; }
        }

        private async void BtnDrawRect_Click(object sender, RoutedEventArgs e)
        {
            BtnDrawRect.IsEnabled = false;
            try
            {
                // UI 不再负责算矩阵和控制马达，一切交给大脑！
                var res = await _brainStartClient.CallAsync(new StartTreeReq(), TimeSpan.FromSeconds(2));
                if (res != null && !res.Success) MessageBox.Show(res.Message, "启动被大脑拒绝");
            }
            catch (Exception ex) { MessageBox.Show($"呼叫大脑失败: {ex.Message}"); }
            finally { BtnDrawRect.IsEnabled = true; }
        }

        // ==========================================
        // CAM 智能特征面拾取与轨迹生成
        // ==========================================
        private void TogFaceSelect_Click(object sender, RoutedEventArgs e)
        {
            TogFaceSelect.Background = TogFaceSelect.IsChecked == true ? new SolidColorBrush(Colors.Orange) : new SolidColorBrush(Color.FromRgb(55, 71, 79));
            if (TogFaceSelect.IsChecked != true) SelectedFaceModel.Content = null;
        }

        private void Viewport3D_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (TogFaceSelect.IsChecked != true) return;

            var hits = Viewport3D.Viewport.FindHits(e.GetPosition(Viewport3D));
            var hit = hits.FirstOrDefault(h => h.Model != DispenserHeadModel.Content);

            if (hit != null && hit.Visual is ModelVisual3D && hit.Model is GeometryModel3D gm && gm.Geometry is MeshGeometry3D sourceMesh)
            {
                var hitMesh = hit.Mesh;
                if (hitMesh == null) return;

                Vector3D targetNormal = hit.Normal;
                Point3D targetPoint = hit.Position;

                var newMesh = new MeshGeometry3D();
                var positions = hitMesh.Positions;
                var indices = hitMesh.TriangleIndices;

                for (int i = 0; i < indices.Count; i += 3)
                {
                    var p1 = positions[indices[i]]; var p2 = positions[indices[i + 1]]; var p3 = positions[indices[i + 2]];
                    var normal = Vector3D.CrossProduct(p2 - p1, p3 - p1); normal.Normalize();

                    if (Math.Abs(Vector3D.DotProduct(normal, targetNormal)) > 0.99)
                    {
                        if (Math.Abs(Vector3D.DotProduct(p1 - targetPoint, targetNormal)) < 0.1)
                        {
                            int baseIndex = newMesh.Positions.Count;
                            newMesh.Positions.Add(p1); newMesh.Positions.Add(p2); newMesh.Positions.Add(p3);
                            newMesh.TriangleIndices.Add(baseIndex); newMesh.TriangleIndices.Add(baseIndex + 1); newMesh.TriangleIndices.Add(baseIndex + 2);
                        }
                    }
                }

                _selectedMesh = newMesh;
                SelectedFaceModel.Content = new GeometryModel3D(_selectedMesh, MaterialHelper.CreateMaterial(Color.FromArgb(150, 0, 191, 255)));
            }
        }

        private void BtnExtractPath_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedMesh == null) { MessageBox.Show("请先点击选取一个加工面！"); return; }

            var edgeDict = new Dictionary<Tuple<Point3D, Point3D>, int>();
            Action<Point3D, Point3D> addEdge = (p1, p2) =>
            {
                var edge = p1.X < p2.X || (p1.X == p2.X && p1.Y < p2.Y) || (p1.X == p2.X && p1.Y == p2.Y && p1.Z < p2.Z) ? Tuple.Create(p1, p2) : Tuple.Create(p2, p1);
                if (edgeDict.ContainsKey(edge)) edgeDict[edge]++; else edgeDict[edge] = 1;
            };

            for (int i = 0; i < _selectedMesh.TriangleIndices.Count; i += 3)
            {
                var p1 = _selectedMesh.Positions[_selectedMesh.TriangleIndices[i]];
                var p2 = _selectedMesh.Positions[_selectedMesh.TriangleIndices[i + 1]];
                var p3 = _selectedMesh.Positions[_selectedMesh.TriangleIndices[i + 2]];
                addEdge(p1, p2); addEdge(p2, p3); addEdge(p3, p1);
            }

            var boundaryEdges = edgeDict.Where(kv => kv.Value == 1).Select(kv => kv.Key).ToList();
            if (boundaryEdges.Count == 0) return;

            _generatedPath.Clear(); TeachLines.Points.Clear();
            var currentEdge = boundaryEdges[0];
            var currentPt = currentEdge.Item1; var nextPt = currentEdge.Item2;

            _generatedPath.Add(new Vector3(currentPt.X, currentPt.Y, currentPt.Z));
            boundaryEdges.RemoveAt(0);

            while (boundaryEdges.Count > 0)
            {
                _generatedPath.Add(new Vector3(nextPt.X, nextPt.Y, nextPt.Z));
                TeachLines.Points.Add(currentPt); TeachLines.Points.Add(nextPt);

                var matchIndex = boundaryEdges.FindIndex(e => e.Item1.DistanceTo(nextPt) < 0.01 || e.Item2.DistanceTo(nextPt) < 0.01);
                if (matchIndex == -1) break;

                var matchEdge = boundaryEdges[matchIndex];
                boundaryEdges.RemoveAt(matchIndex);

                currentPt = nextPt;
                nextPt = matchEdge.Item1.DistanceTo(nextPt) < 0.01 ? matchEdge.Item2 : matchEdge.Item1;
            }

            _generatedPath.Add(_generatedPath[0]);
            TeachLines.Points.Add(currentPt); TeachLines.Points.Add(new Point3D(_generatedPath[0].X, _generatedPath[0].Y, _generatedPath[0].Z));

            TxtTeachCount.Text = $"已生成 {_generatedPath.Count} 个轨迹点";
            TogFaceSelect.IsChecked = false; TogFaceSelect_Click(null, null);
        }

        private async void BtnDeployTeach_Click(object sender, RoutedEventArgs e)
        {
            if (_generatedPath.Count < 2) { MessageBox.Show("轨迹为空！"); return; }
            BtnDeployTeach.IsEnabled = false;
            try
            {
                string pathJson = System.Text.Json.JsonSerializer.Serialize(_generatedPath);
                var context = new Dictionary<string, string>
                {
                    { "TeachPathJson", pathJson },
                    { "SafeZ", SpinSafeZ.Value.ToString() }
                };

                var res = await _brainStartClient.CallAsync(new StartTreeReq(context), TimeSpan.FromSeconds(2));
                if (res != null && !res.Success) MessageBox.Show(res.Message, "大脑拒绝");
            }
            catch (Exception ex) { MessageBox.Show($"调用异常: {ex.Message}"); }
            finally { BtnDeployTeach.IsEnabled = true; }
        }

        private void BtnClearLines_Click(object sender, RoutedEventArgs e)
        {
            GlueLines.Points.Clear(); MoveLines.Points.Clear(); TeachLines.Points.Clear();
            _generatedPath.Clear(); TxtTeachCount.Text = "已生成 0 个轨迹点";
        }

        public void Dispose() => _cts.Cancel();
    }
}