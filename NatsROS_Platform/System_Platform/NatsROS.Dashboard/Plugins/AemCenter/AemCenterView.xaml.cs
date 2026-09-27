using MessagePack;
using NATS.Client.Core;
using NatsROS.Core.Communication;
using NatsROS.Core.Security;
using NatsROS.Messages.AEM;
using NatsROS.Messages.StdMsgs;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;
using UserControl = System.Windows.Controls.UserControl;

namespace NatsROS.Dashboard.Plugins.AemCenter
{
    public class AlarmUiModel
    {
        public string FormattedTime { get; set; } = "";
        public string Code { get; set; } = "";
        public string Level { get; set; } = "";
        public string FormattedMessage { get; set; } = "";
        public int Occurrences { get; set; }
        public string Status { get; set; } = "";
        public string Solution { get; set; } = "";
        public bool AllowBypass { get; set; }
        public string AllowBypassStr => AllowBypass ? "✔ 是" : "⛔ 否";

        // 【新增】：根据状态计算按钮是否可用。只有不是 Acknowledged 的，才允许按 ACK
        public bool CanAck => Status != "Acknowledged";
    }

    public class HistoryUiModel
    {
        public string FormattedTime { get; set; } = "";
        public string Code { get; set; } = "";
        public string Action { get; set; } = "";
        public string Operator { get; set; } = "";
        public string Details { get; set; } = "";
    }

    public partial class AemCenterView : UserControl, IDisposable
    {
        private readonly INatsClient _nats;
        private readonly RosServiceClient<AckAlarmReq, AckAlarmRes> _ackClient;
        private readonly RosServiceClient<GetAlarmHistoryReq, GetAlarmHistoryRes> _historyClient;
        private readonly RosServiceClient<BypassAlarmReq, BypassAlarmRes> _bypassClient;

        private readonly CancellationTokenSource _cts = new();

        public ObservableCollection<AlarmUiModel> AlarmList { get; set; } = new();
        public ObservableCollection<HistoryUiModel> HistoryList { get; set; } = new();

        public AemCenterView(INatsClient nats)
        {
            InitializeComponent();
            _nats = nats;
            _ackClient = new RosServiceClient<AckAlarmReq, AckAlarmRes>(nats, "aem.ack");
            _historyClient = new RosServiceClient<GetAlarmHistoryReq, GetAlarmHistoryRes>(nats, "aem.history");
            _bypassClient = new RosServiceClient<BypassAlarmReq, BypassAlarmRes>(nats, "aem.bypass");

            GridAlarms.ItemsSource = AlarmList;
            GridHistory.ItemsSource = HistoryList;

            Loaded += async (s, e) => await StartMonitoringAsync();
        }

        private async Task StartMonitoringAsync()
        {
            try
            {
                var syncRes = await new RosServiceClient<SyncAlarmsReq, SyncAlarmsRes>(_nats, "aem.sync").CallAsync(new SyncAlarmsReq(), TimeSpan.FromSeconds(2));
                if (syncRes?.ActiveAlarms != null) UpdateUi(syncRes.ActiveAlarms);
            }
            catch { }

            _ = Task.Run(async () =>
            {
                try
                {
                    var alarmSub = new RosSubscriber<AlarmsChangedEvent>(_nats, "aem.changed", RosQosProfile.SensorData);
                    await foreach (var msg in alarmSub.SubscribeAsync(_cts.Token))
                    {
                        if (msg != null) Dispatcher.Invoke(() => UpdateUi(msg.ActiveAlarms));
                    }
                }
                catch (OperationCanceledException) { }
            }, _cts.Token);
        }

        private void UpdateUi(List<ActiveAlarmState> activeAlarms)
        {
            AlarmList.Clear();
            foreach (var a in activeAlarms.OrderByDescending(x => x.LastRaisedTime))
            {
                AlarmList.Add(new AlarmUiModel
                {
                    FormattedTime = DateTimeOffset.FromUnixTimeMilliseconds(a.LastRaisedTime).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    Code = a.Code,
                    Level = a.Level.ToString(),
                    FormattedMessage = a.FormattedMessage,
                    Occurrences = a.Occurrences,
                    Status = a.Status.ToString(),
                    AllowBypass = a.AllowBypass,
                    Solution = GetSopForAlarm(a.Code)
                });
            }

            if (activeAlarms.Count == 0)
            {
                AlertCard.Background = new SolidColorBrush(Color.FromRgb(55, 71, 79));
                TxtAlertTitle.Text = "✅ 当前系统无报警";
                TxtAlertMessage.Text = "所有设备运行正常";
                BtnAckAll.IsEnabled = false;
            }
            else
            {
                var highest = activeAlarms.OrderByDescending(a => a.Level).ThenByDescending(a => a.LastRaisedTime).First();

                if (highest.Status == AlarmStatus.Cleared)
                {
                    AlertCard.Background = new SolidColorBrush(Color.FromRgb(255, 152, 0));
                    TxtAlertTitle.Text = $"⚠️ {highest.Code} (物理已恢复，等待确认)";
                }
                else
                {
                    AlertCard.Background = new SolidColorBrush(Color.FromRgb(211, 47, 47));
                    TxtAlertTitle.Text = $"🚨 {highest.Code} (正在发生)";
                }

                TxtAlertMessage.Text = highest.FormattedMessage;

                // 只有存在未确认报警时，批量确认按钮才亮起
                BtnAckAll.IsEnabled = activeAlarms.Any(a => a.Status != AlarmStatus.Acknowledged);
            }
        }

        private string GetSopForAlarm(string code)
        {
            return code switch
            {
                "ERR_MOT_001" => "▶ 处理步骤：\n1. 请检查伺服驱动器面板是否有报警代码。\n2. 检查运动模组导轨是否卡入异物。\n3. 如果排除物理卡死，请按复位按钮。\n4. 若仍需临时开机，可右键该行，选择[临时屏蔽(Bypass)]。",
                "ERR_VSN_001" => "▶ 处理步骤：\n1. 检查相机光源是否被遮挡。\n2. 用无尘布清理镜头。\n3. 进入 3D 孪生大屏检查当前产品是否有明显偏斜。",
                "ERR_AIR_001" => "▶ 处理步骤：\n1. 检查车间主气源总阀门是否打开。\n2. 观察机台气压表指针是否在 0.5 MPa 以上。\n3. 检查内部气管是否有破损漏气的声音。",
                _ => "请联系设备工艺工程师获取技术支持，或在历史记录中导出诊断日志。"
            };
        }

        // ==========================================
        // 【新增】：行级确认处理逻辑 (Row-level ACK)
        // ==========================================
        private async void BtnRowAck_Click(object sender, RoutedEventArgs e)
        {
            if (sender is DevExpress.Xpf.Core.SimpleButton btn && btn.Tag is AlarmUiModel alarm)
            {
                btn.IsEnabled = false; // 立即禁用按钮，防止连点
                try
                {
                    string op = NatsROS.Dashboard.Security.GlobalSecurityContext.CurrentUser?.DisplayName ?? "操作员";
                    await _ackClient.CallAsync(new AckAlarmReq(alarm.Code, op), TimeSpan.FromSeconds(2));
                }
                catch(Exception ex)
                {
                    MessageBox.Show($"确认失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                } 
                finally
                {
                    btn.IsEnabled = true;
                }
            }
        }

        private async void BtnAckAll_Click(object sender, RoutedEventArgs e)
        {
            BtnAckAll.IsEnabled = false;
            try
            {
                string op = NatsROS.Dashboard.Security.GlobalSecurityContext.CurrentUser?.DisplayName ?? "操作员";
                var tasks = AlarmList.Where(a => a.Status != "Acknowledged")
                    .Select(a => _ackClient.CallAsync(new AckAlarmReq(a.Code, op), TimeSpan.FromSeconds(2)));
                await Task.WhenAll(tasks);
            }
            catch(Exception ex)
            {
                MessageBox.Show($"批量确认失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnAckAll.IsEnabled = true;
            }
        }

        // ==========================================
        // 【新增】：底层解耦的消音指令 (Mute)
        // ==========================================
        private async void BtnMute_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 发送给底层的纯指令，不影响 AEM 里的状态流转
                var headers = new NATS.Client.Core.NatsHeaders { { "ros-type", "NatsROS.Messages.StdMsgs.StringMsg, NatsROS.Messages" } };
                var rawSer = NATS.Client.Core.NatsDefaultSerializerRegistry.Default.GetSerializer<byte[]>();
                var bytes = MessagePackSerializer.Serialize(new StringMsg("MUTE"));

                await _nats.PublishAsync("hw.towerlight.command", data: bytes, headers: headers, serializer: rawSer);
            }
            catch(Exception  ex)
            {
                MessageBox.Show($"发送消音指令失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void ExecuteBypass(int minutes)
        {
            if (GridAlarms.SelectedItem is not AlarmUiModel selected) return;

            if (!selected.AllowBypass)
            {
                MessageBox.Show($"报警 [{selected.Code}] 在契约字典中配置为【不允许屏蔽】！\n强行屏蔽具有极高的设备危险性，操作被系统拦截。", "拦截提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (MessageBox.Show($"确定要将报警 [{selected.Code}] 屏蔽 {minutes} 分钟吗？\n屏蔽期间，系统将无视该异常，您将为此操作承担工艺责任。", "屏蔽确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                try
                {
                    string op = RosSecurityContext.DisplayName ?? "操作员";
                    var res = await _bypassClient.CallAsync(new BypassAlarmReq(selected.Code, minutes, op), TimeSpan.FromSeconds(3));
                    if (res != null) MessageBox.Show(res.Message, "执行结果");
                }
                catch (Exception ex) { MessageBox.Show($"通信失败: {ex.Message}"); }
            }
        }

        private async void BtnRefreshHistory_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var res = await _historyClient.CallAsync(new GetAlarmHistoryReq(1000), TimeSpan.FromSeconds(3));
                if (res != null && res.Records != null)
                {
                    HistoryList.Clear();
                    foreach (var r in res.Records)
                    {
                        HistoryList.Add(new HistoryUiModel
                        {
                            FormattedTime = DateTimeOffset.FromUnixTimeMilliseconds(r.Timestamp).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"),
                            Code = r.Code,
                            Action = r.Action,
                            Operator = r.Operator,
                            Details = r.Details
                        });
                    }
                }
            }
            catch (Exception ex)
            { 
                MessageBox.Show($"获取历史记录失败: {ex.Message}"); 
            }
        }

        public void Dispose() => _cts.Cancel();

        private void MenuBypass30_ItemClick(object sender, DevExpress.Xpf.Bars.ItemClickEventArgs e)
        {
            ExecuteBypass(30);
        }

        private void MenuBypass120_ItemClick(object sender, DevExpress.Xpf.Bars.ItemClickEventArgs e)
        {
            ExecuteBypass(120);
        }

        private void MenuBypass480_ItemClick(object sender, DevExpress.Xpf.Bars.ItemClickEventArgs e)
        {
            ExecuteBypass(480);
        }
    }
}