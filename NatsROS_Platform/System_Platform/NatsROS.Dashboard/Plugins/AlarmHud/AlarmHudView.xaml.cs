using DevExpress.Xpf.Core.Native;
using NATS.Client.Core;
using NatsROS.Core.Communication;
using NatsROS.Messages.AEM;
using NatsROS.Messages.StdMsgs; // 引入字符串消息
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;
using UserControl = System.Windows.Controls.UserControl;

namespace NatsROS.Dashboard.Plugins.AlarmHud
{
    public partial class AlarmHudView : UserControl, IDisposable
    {
        private readonly INatsClient _nats;
        private readonly RosServiceClient<AckAlarmReq, AckAlarmRes> _ackClient;
        private readonly CancellationTokenSource _cts = new();

        private string _highestAlarmCode = "";

        // 记录当前的静音状态
        private bool _isMuted = false;

        public AlarmHudView(INatsClient nats)
        {
            InitializeComponent();
            _nats = nats;
            _ackClient = new RosServiceClient<AckAlarmReq, AckAlarmRes>(nats, "aem.ack");

            Loaded += async (s, e) => await StartMonitoringAsync();
        }

        private async Task StartMonitoringAsync()
        {
            try
            {
                var syncRes = await new RosServiceClient<SyncAlarmsReq, SyncAlarmsRes>(_nats, "aem.sync").CallAsync(new SyncAlarmsReq(), TimeSpan.FromSeconds(2));
                if (syncRes?.ActiveAlarms != null) UpdateHud(syncRes.ActiveAlarms);
            }
            catch { }

            _ = Task.Run(async () =>
            {
                try
                {
                    var alarmSub = new RosSubscriber<AlarmsChangedEvent>(_nats, "aem.changed", RosQosProfile.SensorData);
                    await foreach (var msg in alarmSub.SubscribeAsync(_cts.Token))
                    {
                        if (msg != null) Dispatcher.Invoke(() => UpdateHud(msg.ActiveAlarms));
                    }
                }
                catch (OperationCanceledException) { }
            }, _cts.Token);
        }

        private void UpdateHud(System.Collections.Generic.List<ActiveAlarmState> activeAlarms)
        {
            if (activeAlarms.Count == 0)
            {
                // ==========================================
                // 状态 1：系统健康 (深蓝灰底色 + 绿对勾)
                // ==========================================
                HudBorder.Background = new SolidColorBrush(Color.FromRgb(38, 50, 56));
                HudBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(55, 71, 79));
                IconStatus.Source = WpfSvgRenderer.CreateImageSource(new Uri("pack://application:,,,/DevExpress.Images.v24.2;component/SvgImages/Icon Builder/Actions_CheckCircled.svg"));
                // 【新增】：将光晕设为亮绿色
                GlowEffect.Color = Color.FromRgb(76, 175, 80);

                TxtTitle.Text = "✅ 系统运行正常";
                TxtMessage.Text = "目前没有任何活动报警，设备运转良好。";

                BtnAck.IsEnabled = false;
                _highestAlarmCode = "";
            }
            else
            {
                var highest = activeAlarms.OrderByDescending(a => a.Level).ThenByDescending(a => a.LastRaisedTime).First();
                _highestAlarmCode = highest.Code;

                if (highest.Status == AlarmStatus.Cleared)
                {
                    // ==========================================
                    // 状态 2：物理已恢复 (橙色警告 + 感叹号)
                    // ==========================================
                    HudBorder.Background = new SolidColorBrush(Color.FromRgb(230, 81, 0)); // 深橙色
                    HudBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(255, 152, 0));
                    IconStatus.Source = WpfSvgRenderer.CreateImageSource(new Uri("pack://application:,,,/DevExpress.Images.v24.2;component/SvgImages/Icon Builder/Security_Warning.svg"));
                    // 【新增】：将光晕设为亮橙色
                    GlowEffect.Color = Color.FromRgb(255, 152, 0);

                    TxtTitle.Text = $"⚠️ {highest.Code} (已恢复，请确认)";
                }
                else
                {
                    // ==========================================
                    // 状态 3：致命报警发生中 (猩红色 + 大叉/盾牌)
                    // ==========================================
                    HudBorder.Background = new SolidColorBrush(Color.FromRgb(183, 28, 28)); // 深红色
                    HudBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(211, 47, 47));
                    IconStatus.Source = WpfSvgRenderer.CreateImageSource(new Uri("pack://application:,,,/DevExpress.Images.v24.2;component/SvgImages/Icon Builder/Security_WarningCircled1.svg"));
                    // 【新增】：将光晕设为刺眼的红色
                    GlowEffect.Color = Color.FromRgb(244, 67, 54);

                    TxtTitle.Text = $"🚨 {highest.Code} (正在发生)";
                }

                TxtMessage.Text = highest.FormattedMessage;

                // 只有当存在未确认的报警时，才允许按 ACK
                BtnAck.IsEnabled = activeAlarms.Any(a => a.Status != AlarmStatus.Acknowledged);
            }
        }

        // ==========================================
        // 核心交互：确认当前显示的头条报警
        // ==========================================
        private async void BtnAck_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_highestAlarmCode)) return;
            BtnAck.IsEnabled = false;
            try
            {
                string op = NatsROS.Dashboard.Security.GlobalSecurityContext.CurrentUser?.DisplayName ?? "操作员";
                await _ackClient.CallAsync(new AckAlarmReq(_highestAlarmCode, op), TimeSpan.FromSeconds(2));
            }
            catch { BtnAck.IsEnabled = true; }
        }

        // ==========================================
        // 核心交互：静音拨动开关 (Toggle Switch)
        // ==========================================
        private async void BtnMuteToggle_Click(object sender, RoutedEventArgs e)
        {
            // 翻转本地状态
            _isMuted = !_isMuted;

            try
            {
                var headers = new NATS.Client.Core.NatsHeaders { { "ros-type", "NatsROS.Messages.StdMsgs.StringMsg, NatsROS.Messages" } };
                var rawSer = NATS.Client.Core.NatsDefaultSerializerRegistry.Default.GetSerializer<byte[]>();

                if (_isMuted)
                {
                    // 当前想静音：发静音指令，按钮变红，图标显示禁音
                    BtnMuteToggle.Content = "🔕 取消静音";
                    BtnMuteToggle.Background = new SolidColorBrush(Color.FromRgb(198, 40, 40)); // 红底警示用户喇叭已被掐断
                    var bytes = MessagePack.MessagePackSerializer.Serialize(new StringMsg("MUTE_ON"));
                    await _nats.PublishAsync("hw.towerlight.command", data: bytes, headers: headers, serializer: rawSer);
                }
                else
                {
                    // 当前想恢复声音：发恢复指令，按钮变回蓝灰色，图标显示喇叭
                    BtnMuteToggle.Content = "🔔 开启静音";
                    BtnMuteToggle.Background = new SolidColorBrush(Color.FromRgb(84, 110, 122));
                    var bytes = MessagePack.MessagePackSerializer.Serialize(new StringMsg("MUTE_OFF"));
                    await _nats.PublishAsync("hw.towerlight.command", data: bytes, headers: headers, serializer: rawSer);
                }
            }
            catch (Exception ex)
            {
                // 失败了就把状态回滚
                _isMuted = !_isMuted;
                MessageBox.Show($"发送静音指令失败: {ex.Message}");
            }
        }

        public void Dispose() => _cts.Cancel();
    }
}