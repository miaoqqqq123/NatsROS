using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf;
using NATS.Client.Core;
using NatsROS.Core.Communication;
using NatsROS.Core.Parameters;
using NatsROS.Core.SystemMessages;
using ScrewMachine.Messages.Hardware;
using ScrewMachine.Messages.Motion;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;
using UserControl = System.Windows.Controls.UserControl;

namespace ScrewMachine.Dashboard.Plugins.DigitalTwin
{
    public partial class DigitalTwinView : UserControl, IDisposable
    {
        private readonly INatsClient _nats;
        private readonly CancellationTokenSource _cts = new();

        // 大脑启停控制客户端
        private RosServiceClient<StartTreeReq, StartTreeRes> _startY1Client, _startY2Client, _startDispenserClient;
        private RosServiceClient<StopTreeReq, StopTreeRes> _stopY1Client, _stopY2Client, _stopDispenserClient;

        private RosParameterClient _visionParamClient;

        private const string NODE_DISPENSER = "simulateddispensernode_1";
        private const string NODE_VISION = "simulatedvisionnode_1";
        private const string NODE_AXIS_Y1 = "axis_y1";
        private const string NODE_AXIS_Y2 = "axis_y2";

        private double _liveX = 0, _liveZ = 50, _liveY1 = 0, _liveY2 = 0;
        private double _lastX = 0, _lastZ = 50, _lastY1 = 0, _lastY2 = 0;

        private Point3D _lastPoint = new Point3D(0, 0, 50);

        // 状态机记录 (防抖显示产品上下料)
        private bool _isY1Running = false;
        private bool _isY2Running = false;

        // 记录产品的偏移角度，供渲染上下料动画时叠加
        private Transform3DGroup _y1SkewTransform = new Transform3DGroup();
        private Transform3DGroup _y2SkewTransform = new Transform3DGroup();

        public DigitalTwinView(INatsClient nats)
        {
            InitializeComponent();
            _nats = nats;

            GlueLinesY1.Points = new Point3DCollection();
            GlueLinesY2.Points = new Point3DCollection();
            MoveLines.Points = new Point3DCollection();

            DispenserHeadModel.Transform = new TranslateTransform3D(0, 0, 50);
            StationY1Container.Transform = new TranslateTransform3D(0, 0, 0);
            StationY2Container.Transform = new TranslateTransform3D(0, 0, 0);

            // 初始时隐藏产品 (等待上料)
            SetProductVisible(ProductY1Container, true, _y1SkewTransform);
            SetProductVisible(ProductY2Container, true, _y2SkewTransform);

            // 初始化三大脑控制客户端
            _startY1Client = new RosServiceClient<StartTreeReq, StartTreeRes>(_nats, "brain_y1.bt.start");
            _stopY1Client = new RosServiceClient<StopTreeReq, StopTreeRes>(_nats, "brain_y1.bt.stop");
            _startY2Client = new RosServiceClient<StartTreeReq, StartTreeRes>(_nats, "brain_y2.bt.start");
            _stopY2Client = new RosServiceClient<StopTreeReq, StopTreeRes>(_nats, "brain_y2.bt.stop");
            _startDispenserClient = new RosServiceClient<StartTreeReq, StartTreeRes>(_nats, "brain_dispenser.bt.start");
            _stopDispenserClient = new RosServiceClient<StopTreeReq, StopTreeRes>(_nats, "brain_dispenser.bt.stop");

            _visionParamClient = new RosParameterClient(_nats, NODE_VISION);

            // 开机自动静默加载 3D 模型
            _ = AutoLoadModelsAsync();

            // 监听底层状态
            _ = ListenToTelemetryAsync(_cts.Token);
        }

        // ==========================================
        // 静默加载 3D 模型
        // ==========================================
        private async Task AutoLoadModelsAsync()
        {
            await Task.Delay(500); // 缓冲，防止卡死 UI
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    var importer = new ModelImporter();
                    // 默认从沙盒的 Assets 目录下找
                    string modelsDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "Assets", "3DModels"));
                    if (!Directory.Exists(modelsDir))
                    {
                        Directory.CreateDirectory(modelsDir);
                        return;
                    }

                    string headPath = Path.Combine(modelsDir, "Nozzle.stl");
                    if (File.Exists(headPath))
                    {
                        var headGroup = importer.Load(headPath);
                        foreach (var m in headGroup.Children) if (m is GeometryModel3D gm) gm.Material = MaterialHelper.CreateMaterial(Colors.Orange);
                        DispenserHeadModel.Content = headGroup;
                        if (DefaultHead != null) DefaultHead.Visible = true;
                    }

                    string productPath = Path.Combine(modelsDir, "Product.stl");
                    if (File.Exists(productPath))
                    {
                        var prodGroupY1 = importer.Load(productPath);
                        var prodGroupY2 = importer.Load(productPath);

                        foreach (var m in prodGroupY1.Children) if (m is GeometryModel3D gm) gm.Material = MaterialHelper.CreateMaterial(Colors.Silver);
                        foreach (var m in prodGroupY2.Children) if (m is GeometryModel3D gm) gm.Material = MaterialHelper.CreateMaterial(Colors.Silver);

                        ProductY1Container.Content = prodGroupY1;
                        ProductY2Container.Content = prodGroupY2;
                        if (DefaultProductY1 != null) DefaultProductY1.Visible = true;
                        if (DefaultProductY2 != null) DefaultProductY2.Visible = true;
                    }
                }
                catch { /* 加载失败则静默保留默认方块模型 */ }
            });
        }

        // ==========================================
        // 遥测与坐标处理
        // ==========================================
        private async Task ListenToTelemetryAsync(CancellationToken ct)
        {
            try
            {
                var fbSub = new RosSubscriber<ActionFeedback<DispenserMoveFeedback>>(_nats, $"{NODE_DISPENSER}.move.feedback", RosQosProfile.SensorData);
                var trajSub = new RosSubscriber<ActionFeedback<DispenserMoveFeedback>>(_nats, $"{NODE_DISPENSER}.trajectory.feedback", RosQosProfile.SensorData);
                var y1Sub = new RosSubscriber<ActionFeedback<AxisMoveFeedback>>(_nats, $"{NODE_AXIS_Y1}.move.feedback", RosQosProfile.SensorData);
                var y2Sub = new RosSubscriber<ActionFeedback<AxisMoveFeedback>>(_nats, $"{NODE_AXIS_Y2}.move.feedback", RosQosProfile.SensorData);
                var laserSub = new RosSubscriber<ScannerFlashMsg>(_nats, "hw.scanner.flash", RosQosProfile.SensorData);

                var ioSub = new RosSubscriber<IoStateChangedMsg>(_nats, "motionboard_1.io.state", RosQosProfile.SensorData);

                //var y1BtSub = new RosSubscriber<BtStateMsg>(_nats, "brain_y1.bt.state", RosQosProfile.SensorData);
                //var y2BtSub = new RosSubscriber<BtStateMsg>(_nats, "brain_y2.bt.state", RosQosProfile.SensorData);

                _ = Task.Run(async () => { await foreach (var msg in fbSub.SubscribeAsync(ct)) ProcessDispenser(msg.Data); }, ct);
                _ = Task.Run(async () => { await foreach (var msg in trajSub.SubscribeAsync(ct)) ProcessDispenser(msg.Data); }, ct);
                _ = Task.Run(async () => { await foreach (var msg in y1Sub.SubscribeAsync(ct)) ProcessY1(msg.Data); }, ct);
                _ = Task.Run(async () => { await foreach (var msg in y2Sub.SubscribeAsync(ct)) ProcessY2(msg.Data); }, ct);
                _ = Task.Run(async () => { await foreach (var msg in laserSub.SubscribeAsync(ct)) ProcessLaser(msg); }, ct);


                _ = Task.Run(async () => { await foreach (var msg in ioSub.SubscribeAsync(ct)) ProcessIoState(msg); }, ct);

                //_ = Task.Run(async () => { await foreach (var msg in y1BtSub.SubscribeAsync(ct)) ProcessBrainState("Y1", msg); }, ct);
                //_ = Task.Run(async () => { await foreach (var msg in y2BtSub.SubscribeAsync(ct)) ProcessBrainState("Y2", msg); }, ct);
            }
            catch (OperationCanceledException) { }
        }

        /// <summary>
        /// 纯粹基于物理 IO 驱动的数字孪生渲染
        /// </summary>
        /// <param name="msg"></param>
        private void ProcessIoState(IoStateChangedMsg msg)
        {
            Dispatcher.InvokeAsync(() =>
            {
                // 约定：Pin 0 是 Y1 站的物料光电传感器
                if (msg.Pin == 0)
                {
                    // 如果 Pin 0 为 True，显示物料；为 False，隐藏物料。就这么简单粗暴！
                    if (msg.State) GlueLinesY1.Points.Clear(); // 每次上新料前，顺便把废胶清掉
                    SetProductVisible(ProductY1Container, msg.State, _y1SkewTransform);
                }
                // 约定：Pin 1 是 Y2 站的物料光电传感器
                else if (msg.Pin == 1)
                {
                    if (msg.State) GlueLinesY2.Points.Clear();
                    SetProductVisible(ProductY2Container, msg.State, _y2SkewTransform);
                }
            });
        }

        private void ProcessDispenser(DispenserMoveFeedback fb)
        {
            Dispatcher.InvokeAsync(() =>
            {
                // 1. 机头在世界坐标系下做 X 和 Z 的运动 (机头物理 Y 永远是 0)
                _liveX = fb.CurrentX;
                _liveZ = fb.CurrentZ;

                TxtLiveX.Text = _liveX.ToString("F2"); 
                TxtLiveZ.Text = _liveZ.ToString("F2");
                DispenserHeadModel.Transform = new TranslateTransform3D(_liveX, 0, _liveZ + 10);

                var currentWorldPoint = new Point3D(_liveX, 0, _liveZ);

                // 2. 【核心魔法】：根据底层传来的 Y 轴映射，同步推动下方的物理平台！
                if (fb.ActiveYAxis.Contains("Y1", StringComparison.OrdinalIgnoreCase))
                {
                    _liveY1 = fb.CurrentY;
                    TxtLiveY1.Text = _liveY1.ToString("F2");
                    // 孪生画板里平台的基准是100，所以做差值移动
                    StationY1Container.Transform = new TranslateTransform3D(0, _liveY1, 0);

                    // 绘制胶水轨迹 (绑定在 Y1 平台上)
                    if (fb.IsValveOpen)
                    {
                        // 【核心运动学转换】：
                        // 胶水线画在 StationY1Container 内部（局部坐标系）。
                        // 局部 Y 坐标 = 世界机头 Y(0) - 世界平台 Y(_liveY1) = -_liveY1
                        Point3D p1 = new Point3D(_lastX, -_lastY1, _lastZ);
                        Point3D p2 = new Point3D(_liveX, -_liveY1, _liveZ);

                        // 防止因为精度抖动画出重叠的点
                        if (p1.DistanceTo(p2) > 0.1)
                        {
                            GlueLinesY1.Points.Add(p1);
                            GlueLinesY1.Points.Add(p2);
                        }
                    }
                }
                else if (fb.ActiveYAxis.Contains("Y2", StringComparison.OrdinalIgnoreCase))
                {
                    _liveY2 = fb.CurrentY;
                    TxtLiveY2.Text = _liveY2.ToString("F2");
                    StationY2Container.Transform = new TranslateTransform3D(0, _liveY2, 0);

                    // 绘制胶水轨迹 (绑定在 Y2 平台上)
                    if (fb.IsValveOpen)
                    {
                        Point3D p1 = new Point3D(_lastX, -_lastY2, _lastZ);
                        Point3D p2 = new Point3D(_liveX, -_liveY2, _liveZ);

                        if (p1.DistanceTo(p2) > 0.1)
                        {
                            GlueLinesY2.Points.Add(p1);
                            GlueLinesY2.Points.Add(p2);
                        }
                    }
                }

                // 3. 全局保存当前帧，留给下一帧画线用！
                _lastX = _liveX;
                _lastZ = _liveZ;
                _lastY1 = _liveY1;
                _lastY2 = _liveY2;
            });
        }

        private void ProcessY1(AxisMoveFeedback fb)
        {
            Dispatcher.InvokeAsync(() =>
            {
                _liveY1 = fb.CurrentPosition;
                _lastY1 = _liveY1; // 同步更新最后帧
                TxtLiveY1.Text = _liveY1.ToString("F2");
                StationY1Container.Transform = new TranslateTransform3D(0, _liveY1, 0);
            });
        }

        private void ProcessY2(AxisMoveFeedback fb)
        {
            Dispatcher.InvokeAsync(() =>
            {
                _liveY2 = fb.CurrentPosition;
                _lastY2 = _liveY2; // 同步更新最后帧
                TxtLiveY2.Text = _liveY2.ToString("F2");
                StationY2Container.Transform = new TranslateTransform3D(0, _liveY2, 0);
            });
        }

        private void ProcessLaser(ScannerFlashMsg msg)
        {
            Dispatcher.InvokeAsync(() =>
            {
                var color = msg.IsFlashing ? Color.FromArgb(120, 255, 0, 0) : Color.FromArgb(0, 0, 0, 0);
                if (msg.ScannerName.Contains("y1")) LaserY1.Fill = new SolidColorBrush(color);
                if (msg.ScannerName.Contains("y2")) LaserY2.Fill = new SolidColorBrush(color);
            });
        }

        // ==========================================
        // 智能上下料动画控制
        // ==========================================
        private void ProcessBrainState(string station, BtStateMsg msg)
        {
            Dispatcher.InvokeAsync(() =>
            {
                bool isRunning = msg.NodeStates.Values.Any(s => s == BtNodeStatus.Running);

                if (station == "Y1")
                {
                    if (isRunning && !_isY1Running)
                    {
                        GlueLinesY1.Points.Clear();
                        SetProductVisible(ProductY1Container, true, _y1SkewTransform);
                    }
                    else if (!isRunning && _isY1Running)
                    {
                        Task.Delay(800).ContinueWith(_ => Dispatcher.Invoke(() => SetProductVisible(ProductY1Container, false, _y1SkewTransform)));
                    }
                    _isY1Running = isRunning;
                }
                else if (station == "Y2")
                {
                    if (isRunning && !_isY2Running)
                    {
                        GlueLinesY2.Points.Clear();
                        SetProductVisible(ProductY2Container, true, _y2SkewTransform);
                    }
                    else if (!isRunning && _isY2Running)
                    {
                        Task.Delay(800).ContinueWith(_ => Dispatcher.Invoke(() => SetProductVisible(ProductY2Container, false, _y2SkewTransform)));
                    }
                    _isY2Running = isRunning;
                }
            });
        }

        // 利用 Z 轴缩放为 0 的黑魔法隐藏产品，避免从内存删除
        private void SetProductVisible(ModelVisual3D container, bool visible, Transform3DGroup skewGroup)
        {
            var transform = new Transform3DGroup();
            transform.Children.Add(skewGroup); // 保留它被放歪的角度

            // 如果要隐藏，就把它缩小到肉眼看不见 (不用改颜色)
            if (!visible)
                transform.Children.Add(new ScaleTransform3D(0, 0, 0));
            else
                transform.Children.Add(new ScaleTransform3D(1, 1, 1));

            container.Transform = transform;
        }

        // ==========================================
        // 全局控制与模拟按钮
        // ==========================================
        private async void BtnStartAll_Click(object sender, RoutedEventArgs e)
        {
            BtnStartAll.IsEnabled = false;
            try
            {
                bool isLoop = ChkLoopMode.IsChecked ?? false;

                // 一键启动三个大脑！
                await _startDispenserClient.CallAsync(new StartTreeReq(ContextData: null, IsLoop: isLoop));
                await Task.Delay(100); // 稍微缓冲防并发卡顿
                await _startY1Client.CallAsync(new StartTreeReq(ContextData: null, IsLoop: isLoop));
                await _startY2Client.CallAsync(new StartTreeReq(ContextData: null, IsLoop: isLoop));

                string modeText = isLoop ? "连续循环" : "单次单动";
                MessageBox.Show($"全线大脑已激活！当前模式：【{modeText}】\n正在等待 IO 触发上料...");
            }
            catch (Exception ex)
            {
                MessageBox.Show("启动失败: " + ex.Message);
            }
            finally
            {
                BtnStartAll.IsEnabled = true;
            }
        }

        private async void BtnStopAll_Click(object sender, RoutedEventArgs e)
        {
            BtnStopAll.IsEnabled = false;
            try
            {
                await _stopDispenserClient.CallAsync(new StopTreeReq());
                await _stopY1Client.CallAsync(new StopTreeReq());
                await _stopY2Client.CallAsync(new StopTreeReq());
            }
            finally
            {
                BtnStopAll.IsEnabled = true;
            }
        }

        private async void BtnSimulateSkewY1_Click(object sender, RoutedEventArgs e)
        {
            this.BtnSimulateSkewY1.IsEnabled = false;
            try
            {
                var ret = await SimulateSkew("Y1", ProductY1Container);
                _y1SkewTransform = ret;
            }
            catch (Exception exception)
            {
                Debug.WriteLine(exception);
            }
            finally
            {
                this.BtnSimulateSkewY1.IsEnabled = true;
            }

        }

        private async void BtnSimulateSkewY2_Click(object sender, RoutedEventArgs e)
        {
            this.BtnSimulateSkewY2.IsEnabled = false;
            try
            {
                var ret = await SimulateSkew("Y2", ProductY2Container);
                _y2SkewTransform = ret;
            }
            catch (Exception exception)
            {
                Debug.WriteLine(exception);
            }
            finally
            {
                this.BtnSimulateSkewY2.IsEnabled = true;
            }
        }

        private async Task<Transform3DGroup> SimulateSkew(string station, ModelVisual3D container)
        {

            var rand = new Random();
            double randOffsetX = rand.NextDouble() * 30 - 15;
            double randOffsetY = rand.NextDouble() * 30 - 15;
            double randAngle = rand.NextDouble() * 40 - 20;

            await _visionParamClient.SetAsync("TruthOffsetX", randOffsetX.ToString());
            await _visionParamClient.SetAsync("TruthOffsetY", randOffsetY.ToString());
            await _visionParamClient.SetAsync("TruthAngle", randAngle.ToString());

            var group = new Transform3DGroup();
            group.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 0, 1), randAngle)));
            group.Children.Add(new TranslateTransform3D(randOffsetX, randOffsetY, 0));

            if (container.Transform.Value.M11 > 0) SetProductVisible(container, true, group);

            MessageBox.Show($"[{station} 站] 工人已将物料放歪！\nX:{randOffsetX:F1}, Y:{randOffsetY:F1}, 旋转:{randAngle:F1}°");

            return group; // 返回结果
        }

        private void BtnClearLines_Click(object sender, RoutedEventArgs e)
        {
            GlueLinesY1.Points.Clear();
            GlueLinesY2.Points.Clear();
            MoveLines.Points.Clear();
        }

        public void Dispose() => _cts.Cancel();
    }
}