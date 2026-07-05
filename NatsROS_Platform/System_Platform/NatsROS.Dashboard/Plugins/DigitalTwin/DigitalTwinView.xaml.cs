// 【新增】引入 3D 解析库
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
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using MessageBox = System.Windows.MessageBox;
using UserControl = System.Windows.Controls.UserControl;

namespace NatsROS.Dashboard.Plugins.DigitalTwin
{
    public partial class DigitalTwinView : UserControl, IDisposable
    {
        private readonly INatsClient _nats;
        private readonly CancellationTokenSource _cts = new();

        // 操控硬件的客户端
        private RosActionClient<DispenserMoveGoal, DispenserMoveFeedback, DispenserMoveResult> _moveClient;
        private RosActionClient<DispenserTrajectoryGoal, DispenserMoveFeedback, DispenserMoveResult> _trajClient;
        private RosServiceClient<ValveControlReq, ValveControlRes> _valveClient;
        // 在类的顶部追加变量：
        private RosServiceClient<UploadRecordReq, UploadRecordRes> _mesClient;
        private RosServiceClient<StartTreeReq, StartTreeRes> _brainStartClient;

        // 视觉客户端
        private RosServiceClient<FindMarkReq, FindMarkRes> _visionClient;
        private RosParameterClient _visionParamClient;

        private const string NODE_DISPENSER = "simulateddispensernode_1";
        private const string NODE_VISION = "simulatedvisionnode_1"; // 需要您在左侧组件库拉起这个节点！
        private const string NODE_MES = "mockmesnode_1"; // 待会儿在组件库要拉起它

        private Point3D _lastPoint = new Point3D(0, 0, 50);

        public DigitalTwinView(INatsClient nats)
        {
            InitializeComponent();
            _nats = nats;

            GlueLines.Points = new Point3DCollection();
            MoveLines.Points = new Point3DCollection();
            DispenserHeadModel.Transform = new TranslateTransform3D(0, 0, 50);

            _moveClient = new RosActionClient<DispenserMoveGoal, DispenserMoveFeedback, DispenserMoveResult>(_nats, $"{NODE_DISPENSER}.move");
            _trajClient = new RosActionClient<DispenserTrajectoryGoal, DispenserMoveFeedback, DispenserMoveResult>(_nats, $"{NODE_DISPENSER}.trajectory");
            _valveClient = new RosServiceClient<ValveControlReq, ValveControlRes>(_nats, $"{NODE_DISPENSER}.valve");
            _mesClient = new RosServiceClient<UploadRecordReq, UploadRecordRes>(_nats, $"{NODE_MES}.upload");

            _visionClient = new RosServiceClient<FindMarkReq, FindMarkRes>(_nats, $"{NODE_VISION}.find_mark");
            _visionParamClient = new RosParameterClient(_nats, NODE_VISION);
            _brainStartClient = new RosServiceClient<StartTreeReq, StartTreeRes>(_nats, "brain.bt.start"); // 假设您在大盘里拉起的大脑叫 brainnode_1

            _ = ListenToTelemetryAsync(_cts.Token);
        }

        // ==========================================
        // CAD 导入与遥测逻辑 (保持原样)
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

                    ProductModelContainer.Content = modelGroup;
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

        private async Task ListenToTelemetryAsync(CancellationToken ct)
        {
            try
            {
                var feedbackSub = new RosSubscriber<ActionFeedback<DispenserMoveFeedback>>(_nats, $"{NODE_DISPENSER}.move.feedback", RosQosProfile.SensorData);
                var trajFeedbackSub = new RosSubscriber<ActionFeedback<DispenserMoveFeedback>>(_nats, $"{NODE_DISPENSER}.trajectory.feedback", RosQosProfile.SensorData);

                _ = Task.Run(async () => { await foreach (var msg in feedbackSub.SubscribeAsync(ct)) ProcessFeedback(msg.Data); }, ct);
                _ = Task.Run(async () => { await foreach (var msg in trajFeedbackSub.SubscribeAsync(ct)) ProcessFeedback(msg.Data); }, ct);
            }
            catch (OperationCanceledException) { }
        }

        private void ProcessFeedback(DispenserMoveFeedback fb)
        {
            Dispatcher.InvokeAsync(() =>
            {
                TxtLiveX.Text = fb.CurrentX.ToString("F2");
                TxtLiveY.Text = fb.CurrentY.ToString("F2");
                TxtLiveZ.Text = fb.CurrentZ.ToString("F2");

                DispenserHeadModel.Transform = new TranslateTransform3D(fb.CurrentX, fb.CurrentY, fb.CurrentZ + 10);
                var currentPoint = new Point3D(fb.CurrentX, fb.CurrentY, fb.CurrentZ);

                if (_lastPoint.DistanceTo(currentPoint) > 0.1)
                {
                    if (fb.IsValveOpen) { GlueLines.Points.Add(_lastPoint); GlueLines.Points.Add(currentPoint); }
                    else { MoveLines.Points.Add(_lastPoint); MoveLines.Points.Add(currentPoint); }
                }
                _lastPoint = currentPoint;
            });
        }

        // ==========================================
        // 核心：视觉仿射变换算法！
        // ==========================================
        private Vector3 ApplyAffineTransform(Vector3 original, double offsetX, double offsetY, double angleDeg)
        {
            // 假设旋转中心就是产品的中心 (0,0)
            double rad = angleDeg * Math.PI / 180.0;
            double cosA = Math.Cos(rad);
            double sinA = Math.Sin(rad);

            // 矩阵乘法：先旋转，再平移
            double newX = original.X * cosA - original.Y * sinA + offsetX;
            double newY = original.X * sinA + original.Y * cosA + offsetY;

            return new Vector3(newX, newY, original.Z);
        }

        // ==========================================
        // UI 按钮控制逻辑
        // ==========================================

        private async void BtnSimulateSkew_Click(object sender, RoutedEventArgs e)
        {
            BtnSimulateSkew.IsEnabled = false;
            try
            {
                // 1. 生成随机的放置偏差
                var rand = new Random();
                double randOffsetX = rand.NextDouble() * 30 - 15; // -15 到 15 mm
                double randOffsetY = rand.NextDouble() * 30 - 15;
                double randAngle = rand.NextDouble() * 40 - 20;   // -20 到 20 度

                // 2. 将真实的偏差写入视觉节点的参数服务器 (模拟它能拍出来)
                await _visionParamClient.SetAsync("TruthOffsetX", randOffsetX.ToString());
                await _visionParamClient.SetAsync("TruthOffsetY", randOffsetY.ToString());
                await _visionParamClient.SetAsync("TruthAngle", randAngle.ToString());

                // 3. 在 3D 界面里，真正地把底部的产品模型给转过去！
                var transformGroup = new Transform3DGroup();
                // 必须先旋转，再平移，这叫 TRS 矩阵顺序
                transformGroup.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 0, 1), randAngle)));
                transformGroup.Children.Add(new TranslateTransform3D(randOffsetX, randOffsetY, 0));

                ProductModelContainer.Transform = transformGroup;

                MessageBox.Show($"产品已被放歪！\n偏差X: {randOffsetX:F2} \n偏差Y: {randOffsetY:F2} \n旋转: {randAngle:F2}°\n请点击下方按钮测试视觉纠偏。", "提示");
            }
            finally { BtnSimulateSkew.IsEnabled = true; }
        }

        private async void BtnDrawRect_Click(object sender, RoutedEventArgs e)
        {
            //BtnDrawRect.IsEnabled = false;
            //var sw = System.Diagnostics.Stopwatch.StartNew(); // 【新增】：开始计时！
            //double ox = 0, oy = 0, ang = 0;

            //try
            //{
            //    // ========================================
            //    // 1. 呼叫视觉相机进行拍照与定位！
            //    // ========================================
            //    var visionRes = await _visionClient.CallAsync(new FindMarkReq(), TimeSpan.FromSeconds(5));
            //    if (visionRes == null || !visionRes.Success) throw new Exception("视觉定位失败或超时！");

            //    ox = visionRes.OffsetX;
            //    oy = visionRes.OffsetY;
            //    ang = visionRes.AngleDegree;

            //    // ========================================
            //    // 2. 定义理论胶路 (完美的正方形)
            //    // ========================================
            //    var standardPath = new System.Collections.Generic.List<Vector3>();
            //    standardPath.Add(new Vector3(-30, 30, 0));
            //    standardPath.Add(new Vector3(30, 30, 0));
            //    standardPath.Add(new Vector3(30, -30, 0));
            //    standardPath.Add(new Vector3(-30, -30, 0));
            //    standardPath.Add(new Vector3(-30, 30, 0));

            //    // ========================================
            //    // 3. 大脑运算：使用仿射变换修正全部轨迹点！
            //    // ========================================
            //    var correctedPath = new System.Collections.Generic.List<Vector3>();
            //    foreach (var pt in standardPath)
            //    {
            //        correctedPath.Add(ApplyAffineTransform(pt, ox, oy, ang));
            //    }

            //    // 取纠偏后的第一个点作为下针点
            //    var startPt = correctedPath[0];

            //    // ========================================
            //    // 4. 执行修正后的点胶工序
            //    // ========================================
            //    await _moveClient.SendGoalAsync(new DispenserMoveGoal(startPt.X, startPt.Y, 20, 50));
            //    await _moveClient.SendGoalAsync(new DispenserMoveGoal(startPt.X, startPt.Y, 0, 20));

            //    await _valveClient.CallAsync(new ValveControlReq(true));
            //    await _trajClient.SendGoalAsync(new DispenserTrajectoryGoal(correctedPath.ToArray(), 30));
            //    await _valveClient.CallAsync(new ValveControlReq(false));

            //    await _moveClient.SendGoalAsync(new DispenserMoveGoal(startPt.X, startPt.Y, 20, 50));
            //    await _moveClient.SendGoalAsync(new DispenserMoveGoal(0, 0, 50, 50));

            //    // 【新增】：点胶流程完美结束，组装 MES 数据并上传！
            //    sw.Stop();
            //    var record = new ProductRecord
            //    (
            //        Barcode: $"SN-{DateTime.Now:yyyyMMddHHmmss}", // 自动生成条码
            //        Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            //        IsPass: true, // 如果没抛异常就是良品
            //        CycleTimeSec: sw.Elapsed.TotalSeconds,
            //        OffsetX: ox,
            //        OffsetY: oy,
            //        Angle: ang
            //    );

            //    await _mesClient.CallAsync(new UploadRecordReq(record), TimeSpan.FromSeconds(2));
            //}
            //catch (Exception ex)
            //{
            //    sw.Stop();
            //    // 失败也上传一条记录
            //    await _mesClient.CallAsync(new UploadRecordReq
            //    (
            //        new ProductRecord($"SN-{DateTime.Now:yyyyMMddHHmmss}", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), false, sw.Elapsed.TotalSeconds, 0, 0, 0)
            //    ));

            //    MessageBox.Show($"点胶工序异常: {ex.Message}");
            //}
            //finally
            //{
            //    BtnDrawRect.IsEnabled = true;
            //}

            BtnDrawRect.IsEnabled = false;
            try
            {
                // UI 不再负责算矩阵和控制马达，UI 仅仅是“上帝”按下了一个启动按钮！
                // 真正干活的，是 L1 层的 BrainNode，它会去读您画的那棵行为树！
                var res = await _brainStartClient.CallAsync(new StartTreeReq(), TimeSpan.FromSeconds(2));

                if (res != null && !res.Success)
                {
                    MessageBox.Show(res.Message, "启动被大脑拒绝");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"呼叫大脑失败，请确保 BrainNode 节点已在大盘中拉起。错误: {ex.Message}");
            }
            finally
            {
                BtnDrawRect.IsEnabled = true;
            }
        }

        private async void BtnSendGoal_Click(object sender, RoutedEventArgs e)
        {
            BtnSendGoal.IsEnabled = false;
            await _moveClient.SendGoalAsync(new DispenserMoveGoal(
                Convert.ToDouble(SpinTargetX.Value), Convert.ToDouble(SpinTargetY.Value), Convert.ToDouble(SpinTargetZ.Value), Convert.ToDouble(SpinVel.Value)), null, _cts.Token);
            BtnSendGoal.IsEnabled = true;
        }

        private void BtnClearLines_Click(object sender, RoutedEventArgs e)
        {
            GlueLines.Points.Clear();
            MoveLines.Points.Clear();
        }

        public void Dispose() => _cts.Cancel();
    }
}