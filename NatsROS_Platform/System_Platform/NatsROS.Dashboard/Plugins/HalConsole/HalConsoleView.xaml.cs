using NATS.Client.Core;
using NatsROS.Core.Communication;
using NatsROS.Messages.Hardware;
using NatsROS.Messages.Motion;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;
using UserControl = System.Windows.Controls.UserControl;

namespace NatsROS.Dashboard.Plugins.HalConsole
{
    // 用于绑定 UI 32 个引脚的模型
    public class IoPinModel : INotifyPropertyChanged
    {
        private bool _isHigh;
        public int PinId { get; set; }
        public string PinName => $"OUT {PinId:D2}";

        public bool IsHigh
        {
            get => _isHigh;
            set
            {
                _isHigh = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PinColor));
            }
        }

        // 绿色代表通电，深灰代表断电
        public SolidColorBrush PinColor => _isHigh ? new SolidColorBrush(Color.FromRgb(76, 175, 80)) : new SolidColorBrush(Color.FromRgb(55, 71, 79));

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public partial class HalConsoleView : UserControl, IDisposable
    {
        private readonly INatsClient _nats;
        private readonly CancellationTokenSource _cts = new();

        public ObservableCollection<IoPinModel> IoPins { get; set; } = new();

        // 硬件寻址常量 (与 launch.json 保持一致)
        private const string NODE_BOARD = "motionboard_1";
        private const string NODE_LIGHT = "towerlight_1";
        private const string NODE_VALVE = "valve_1";
        private const string NODE_SCANNER = "scanner_1";

        public HalConsoleView(INatsClient nats)
        {
            InitializeComponent();
            _nats = nats;

            // 初始化 32 个 IO 引脚
            for (int i = 0; i < 32; i++) IoPins.Add(new IoPinModel { PinId = i, IsHigh = false });
            IoItemsControl.ItemsSource = IoPins;

            // 后台监听底层板卡的 IO 状态变化广播
            _ = ListenToIoEventsAsync(_cts.Token);
        }

        private async Task ListenToIoEventsAsync(CancellationToken ct)
        {
            try
            {
                var ioSub = new RosSubscriber<IoStateChangedMsg>(_nats, $"{NODE_BOARD}.io.state", RosQosProfile.SensorData);
                await foreach (var msg in ioSub.SubscribeAsync(ct))
                {
                    if (msg != null)
                    {
                        var pinData = msg;
                        Dispatcher.InvokeAsync(() =>
                        {
                            // 瞬间点亮或熄灭对应的按钮！
                            if (pinData.Pin >= 0 && pinData.Pin < 32)
                            {
                                IoPins[pinData.Pin].IsHigh = pinData.State;
                            }
                        });
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        // ==========================================
        // 1. IO 强制置位
        // ==========================================
        private async void BtnIoPin_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int pinId)
            {
                var pinModel = IoPins[pinId];
                bool targetState = !pinModel.IsHigh; // 反转状态

                try
                {
                    var ioClient = new RosServiceClient<SetIoReq, SetIoRes>(_nats, $"{NODE_BOARD}.io.set");
                    var res = await ioClient.CallAsync(new SetIoReq(pinId, targetState), TimeSpan.FromSeconds(2));

                    if (res == null || !res.Success) MessageBox.Show($"写入引脚 {pinId} 失败！请检查板卡节点是否在线。");
                }
                catch (Exception ex) { MessageBox.Show($"通讯异常: {ex.Message}"); }
            }
        }

        // ==========================================
        // 2. 塔灯强制下发
        // ==========================================
        private async void BtnSetLight_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var lightClient = new RosServiceClient<SetTowerLightReq, SetTowerLightRes>(_nats, $"{NODE_LIGHT}.set");
                var req = new SetTowerLightReq(
                    (LightState)CboRed.SelectedIndex,
                    (LightState)CboYellow.SelectedIndex,
                    (LightState)CboGreen.SelectedIndex,
                    false);

                await lightClient.CallAsync(req, TimeSpan.FromSeconds(2));
            }
            catch (Exception ex) { MessageBox.Show($"塔灯控制失败: {ex.Message}"); }
        }

        // ==========================================
        // 3. 点胶阀强行开关
        // ==========================================
        private async void BtnValveOpen_Click(object sender, RoutedEventArgs e) => await CallValve(true);
        private async void BtnValveClose_Click(object sender, RoutedEventArgs e) => await CallValve(false);

        private async Task CallValve(bool open)
        {
            try
            {
                var valveClient = new RosServiceClient<ValveControlReq, ValveControlRes>(_nats, $"{NODE_VALVE}.valve");
                await valveClient.CallAsync(new ValveControlReq(open), TimeSpan.FromSeconds(2));
            }
            catch (Exception ex) { MessageBox.Show($"阀门控制失败: {ex.Message}"); }
        }

        // ==========================================
        // 4. 扫码枪软触发
        // ==========================================
        private async void BtnTriggerScan_Click(object sender, RoutedEventArgs e)
        {
            BtnTriggerScan.IsEnabled = false;
            TxtScanResult.Text = "⏳ 扫描中...";
            TxtScanResult.Foreground = new SolidColorBrush(Colors.Gray);

            try
            {
                var scannerClient = new RosServiceClient<TriggerScanReq, TriggerScanRes>(_nats, $"{NODE_SCANNER}.trigger");
                var res = await scannerClient.CallAsync(new TriggerScanReq(3000), TimeSpan.FromSeconds(3));

                if (res != null && res.Success)
                {
                    TxtScanResult.Text = res.Barcode;
                    TxtScanResult.Foreground = new SolidColorBrush(Colors.LimeGreen);
                }
                else
                {
                    TxtScanResult.Text = res?.Message ?? "超时";
                    TxtScanResult.Foreground = new SolidColorBrush(Colors.Red);
                }
            }
            catch (Exception ex)
            {
                TxtScanResult.Text = "通信异常";
                TxtScanResult.Foreground = new SolidColorBrush(Colors.Red);
            }
            finally
            {
                BtnTriggerScan.IsEnabled = true;
            }
        }

        public void Dispose() => _cts.Cancel();
    }
}