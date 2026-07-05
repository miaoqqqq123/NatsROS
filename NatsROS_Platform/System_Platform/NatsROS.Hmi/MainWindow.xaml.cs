using NATS.Client.Core;
using NATS.Net;
using NatsROS.Core.Communication;
using NatsROS.Core.Serialization;
using NatsROS.Core.SystemMessages;
using NatsROS.Messages.AEM;
using NatsROS.Messages.Motion;
using NatsROS.Messages.RMS;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;

namespace NatsROS.Hmi
{
    public class StepResultItem : INotifyPropertyChanged
    {
        private string _status = "等待中";
        private double _progress = 0;

        public string NodeId { get; set; } = "";
        public string StepName { get; set; } = "";
        public string Status { get => _status; set { _status = value; OnPropertyChanged(); } }
        public double Progress { get => _progress; set { _progress = value; OnPropertyChanged(); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public partial class MainWindow : DevExpress.Xpf.Core.ThemedWindow
    {
        private INatsClient? _nats;
        private RosServiceClient<StartTreeReq, StartTreeRes>? _startClient;
        private RosServiceClient<StopTreeReq, StopTreeRes>? _stopClient;
        private RosServiceClient<BtTopologyReq, BtTopologyMsg>? _topologyClient;
        private RosServiceClient<AckAlarmReq, AckAlarmRes>? _ackClient;
        // 增加 RMS 查询客户端
        private RosServiceClient<GetRecipesReq, GetRecipesRes>? _rmsClient;
        private RosServiceClient<PauseTreeReq, PauseTreeRes>? _pauseClient;
        private RosServiceClient<ResumeTreeReq, ResumeTreeRes>? _resumeClient;

        private string _currentHighestAlarmCode = "";
        private const string NODE_BRAIN = "brain";
        private const string NODE_DISPENSER = "simulateddispensernode_1";

        public ObservableCollection<StepResultItem> StepResults { get; set; } = new();
        private readonly Dictionary<string, StepResultItem> _stepDict = new();

        public MainWindow()
        {
            DevExpress.Xpf.Core.ApplicationThemeHelper.ApplicationThemeName = "Win11Dark";
            InitializeComponent();
            GridSteps.ItemsSource = StepResults;
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var options = NatsOpts.Default with { SerializerRegistry = new NatsRosSerializerRegistry() };
                _nats = new NatsClient(options);
                await _nats.ConnectAsync();

                _startClient = new RosServiceClient<StartTreeReq, StartTreeRes>(_nats, $"{NODE_BRAIN}.bt.start");
                _stopClient = new RosServiceClient<StopTreeReq, StopTreeRes>(_nats, $"{NODE_BRAIN}.bt.stop");
                _topologyClient = new RosServiceClient<BtTopologyReq, BtTopologyMsg>(_nats, $"{NODE_BRAIN}.bt.topology.request");
                // 【AEM 报警系统 1】：初始化复位客户端
                _ackClient = new RosServiceClient<AckAlarmReq, AckAlarmRes>(_nats, "aem.ack");
                _pauseClient = new RosServiceClient<PauseTreeReq, PauseTreeRes>(_nats, $"{NODE_BRAIN}.bt.pause");
                _resumeClient = new RosServiceClient<ResumeTreeReq, ResumeTreeRes>(_nats, $"{NODE_BRAIN}.bt.resume");

                // 【新增】：初始化配方客户端，并拉取“已批准”的配方！
                _rmsClient = new RosServiceClient<GetRecipesReq, GetRecipesRes>(_nats, "rms.get_recipes");
                try
                {
                    var res = await _rmsClient.CallAsync(new GetRecipesReq(), TimeSpan.FromSeconds(2));
                    if (res != null)
                    {
                        // 过滤：操作员只允许看到 Approved 状态的正式配方！
                        var approvedRecipes = res.Recipes.Where(r => r.State == RecipeState.Approved).ToList();
                        Dispatcher.Invoke(() => CboRecipe.ItemsSource = approvedRecipes);
                    }
                }
                catch { /* RMS 可能没启动 */ }

                // 1. 越权监听底层 Z 轴反馈，用于动画
                _ = Task.Run(() =>
                {
                    //var axisSub = new RosSubscriber<ActionFeedback<DispenserMoveFeedback>>(_nats, $"{NODE_DISPENSER}.move.feedback", RosQosProfile.SensorData);
                    //await foreach (var msg in axisSub.SubscribeAsync())
                    //{
                    //    Dispatcher.InvokeAsync(() =>
                    //    {
                    //        PbAxisPosX.Value = msg.Data.CurrentX;
                    //        PbAxisPosY.Value = msg.Data.CurrentY;
                    //        PbAxisPosZ.Value = msg.Data.CurrentZ;
                    //    });
                    //}
                    try
                    {
                        var feedbackSub = new RosSubscriber<ActionFeedback<DispenserMoveFeedback>>(_nats, $"{NODE_DISPENSER}.move.feedback", RosQosProfile.SensorData);
                        var trajFeedbackSub = new RosSubscriber<ActionFeedback<DispenserMoveFeedback>>(_nats, $"{NODE_DISPENSER}.trajectory.feedback", RosQosProfile.SensorData);

                        _ = Task.Run(async () => { await foreach (var msg in feedbackSub.SubscribeAsync()) ProcessFeedback(msg.Data); } );
                        _ = Task.Run(async () => { await foreach (var msg in trajFeedbackSub.SubscribeAsync()) ProcessFeedback(msg.Data); } );
                    }
                    catch (OperationCanceledException) { }
                });

                // 2. 监听行为树状态流转，用于在 UI 列表上打钩
                _ = Task.Run(async () =>
                {
                    var stateSub = new RosSubscriber<BtStateMsg>(_nats, $"{NODE_BRAIN}.bt.state", RosQosProfile.SensorData);
                    await foreach (var msg in stateSub.SubscribeAsync())
                    {
                        if (msg == null) continue;
                        Dispatcher.InvokeAsync(() =>
                        {
                            foreach (var kvp in msg.NodeStates)
                            {
                                if (_stepDict.TryGetValue(kvp.Key, out var item))
                                {
                                    if (kvp.Value == BtNodeStatus.Running) { item.Status = "🔄 执行中"; item.Progress = 50; }
                                    else if (kvp.Value == BtNodeStatus.Success) { item.Status = "✅ 通过"; item.Progress = 100; }
                                    else if (kvp.Value == BtNodeStatus.Failure) { item.Status = "❌ 失败"; item.Progress = 100; }
                                    else if (kvp.Value == BtNodeStatus.Idle) { item.Status = "等待中"; item.Progress = 0; }
                                }
                            }
                        });
                    }
                });

                // 3. 监听全局结果
                _ = Task.Run(async () =>
                {
                    var resSub = new RosSubscriber<BtResultMsg>(_nats, $"{NODE_BRAIN}.bt.result", RosQosProfile.SensorData);
                    await foreach (var msg in resSub.SubscribeAsync())
                    {
                        if (msg != null)
                        {
                            Dispatcher.InvokeAsync(() =>
                            {
                                if (msg.IsSuccess)
                                {
                                    TxtResult.Text = "PASS";
                                    BorderResult.Background = new SolidColorBrush(Color.FromRgb(46, 125, 50));
                                }
                                else
                                {
                                    TxtResult.Text = "FAIL";
                                    BorderResult.Background = new SolidColorBrush(Color.FromRgb(198, 40, 40));
                                }
                                BtnStart.IsEnabled = true;
                            });
                        }
                    }
                });

                // 【AEM 报警系统 2】：监听全网活动报警变化
                _ = Task.Run(async () =>
                {
                    var alarmSub = new RosSubscriber<AlarmsChangedEvent>(_nats, "aem.changed", RosQosProfile.SensorData);
                    await foreach (var msg in alarmSub.SubscribeAsync())
                    {
                        if (msg != null)
                            Dispatcher.Invoke(() => UpdateAlarmBanner(msg.ActiveAlarms));
                    }
                });

                // 【AEM 报警系统 3】：HMI 开机时主动去拉取一次当前有哪些未处理的报警
                var syncClient = new RosServiceClient<SyncAlarmsReq, SyncAlarmsRes>(_nats, "aem.sync");
                var syncRes = await syncClient.CallAsync(new SyncAlarmsReq(), TimeSpan.FromSeconds(2));
                if (syncRes != null && syncRes.ActiveAlarms != null)
                {
                    UpdateAlarmBanner(syncRes.ActiveAlarms);
                }

                await BuildStepsFromTopologyAsync();
            }
            catch (Exception ex) { MessageBox.Show($"网络连接失败: {ex.Message}"); }
        }

        // ==========================================
        // AEM 报警横幅刷新算法
        // ==========================================
        private void UpdateAlarmBanner(List<ActiveAlarmState> activeAlarms)
        {
            if (activeAlarms == null || activeAlarms.Count == 0)
            {
                // 没有报警，天下太平，隐藏横幅并解锁启动按钮！
                BannerAlarm.Visibility = Visibility.Collapsed;
                BtnStart.IsEnabled = true;
                _currentHighestAlarmCode = "";
            }
            else
            {
                // 找出最严重、最新的那一个报警顶在屏幕上
                var highestAlarm = activeAlarms
                    .OrderByDescending(a => a.Level)
                    .ThenByDescending(a => a.LastRaisedTime)
                    .First();

                _currentHighestAlarmCode = highestAlarm.Code;

                // 拼接显示文本 (例如：[ERR_VSN_001] 相机未找到定位点... (已发生 3 次))
                string statusText = highestAlarm.Status == AlarmStatus.Cleared ? "(物理已恢复，请复位)" : "";
                TxtAlarmMsg.Text = $"[{highestAlarm.Code}] {highestAlarm.FormattedMessage} " +
                                   $"(发生 {highestAlarm.Occurrences} 次) {statusText}";

                // 强制拦截：一旦有活动报警，红灯霸屏，开始按钮彻底死锁！
                BannerAlarm.Visibility = Visibility.Visible;
                BtnStart.IsEnabled = false;
            }
        }

        // ==========================================
        // 工人点击“复位”按钮
        // ==========================================
        private async void BtnAckAlarm_Click(object sender, RoutedEventArgs e)
        {
            if (_ackClient != null && !string.IsNullOrEmpty(_currentHighestAlarmCode))
            {
                BtnAckAlarm.IsEnabled = false;
                try
                {
                    // 告诉 AEM 管理器：工人已经确认了这个报警！
                    await _ackClient.CallAsync(new AckAlarmReq(_currentHighestAlarmCode), TimeSpan.FromSeconds(2));
                }
                catch (Exception ex) { MessageBox.Show("复位通信失败: " + ex.Message); }
                finally { BtnAckAlarm.IsEnabled = true; }
            }
        }

        private void ProcessFeedback(DispenserMoveFeedback fb)
        {
            Dispatcher.InvokeAsync(() =>
            {
                PbAxisPosX.Value = fb.CurrentX;
                PbAxisPosY.Value = fb.CurrentY;
                PbAxisPosZ.Value = fb.CurrentZ;
            });
        }

        // ==========================================
        // 【核心魔法】：向大脑索要拓扑，动态生成测试清单！
        // ==========================================
        private async Task BuildStepsFromTopologyAsync()
        {
            try
            {
                var topMsg = await _topologyClient!.CallAsync(new BtTopologyReq(), TimeSpan.FromSeconds(2));
                if (topMsg != null && topMsg.Nodes != null)
                {
                    StepResults.Clear();
                    _stepDict.Clear();

                    // 我们只在工人的界面上显示“动作积木(Action)”，隐藏掉那些起控制作用的逻辑节点
                    foreach (var node in topMsg.Nodes.Where(n => n.Type == BtNodeType.Action))
                    {
                        var item = new StepResultItem { NodeId = node.Id, StepName = node.Name };
                        StepResults.Add(item);
                        _stepDict[node.Id] = item;
                    }
                }
            }
            catch { /* 大脑没开，或者配方没加载，静默失败，等待用户点击部署 */ }
        }

        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (_startClient == null || string.IsNullOrWhiteSpace(TxtBarcode.Text)) return;

            // 【核心防御】：必须选择配方！
            if (CboRecipe.SelectedItem is not RecipeModel selectedRecipe)
            {
                MessageBox.Show("请先选择要生产的产品配方！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 每次启动前，重新拉取一次最新的配方拓扑（工程师可能刚用 Dashboard 更新过！）
            await BuildStepsFromTopologyAsync();

            foreach (var step in StepResults)
            { 
                step.Status = "等待中"; 
                step.Progress = 0;
            }

            TxtResult.Text = "TESTING...";
            BorderResult.Background = new SolidColorBrush(Color.FromRgb(255, 160, 0));
            BtnStart.IsEnabled = false;

            try
            {
                // 【一键换型魔法】：把配方里的所有 Formula 参数提取出来！
                var context = new Dictionary<string, string>(selectedRecipe.Formula);

                // 把条码也塞进去
                context["Barcode"] = TxtBarcode.Text;

                // 瞬间将几百个配方参数灌入 L1 大脑黑板！
                var res = await _startClient.CallAsync(new StartTreeReq(context), TimeSpan.FromSeconds(2));
                if (res != null && !res.Success) throw new Exception(res.Message);
            }
            catch (Exception ex)
            {
                TxtResult.Text = "ERROR";
                BorderResult.Background = new SolidColorBrush(Colors.DarkRed);
                MessageBox.Show($"无法启动机器：\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally 
            {
                BtnStart.IsEnabled = true; 
            }
        }

        private async void BtnEStop_Click(object sender, RoutedEventArgs e)
        {
            if (_stopClient != null)
            {
                await _stopClient.CallAsync(new StopTreeReq());
                TxtResult.Text = "ABORTED";
                BorderResult.Background = new SolidColorBrush(Colors.DarkRed);
                BtnStart.IsEnabled = true;
            }
        }

        private void Window_Closing(object sender, CancelEventArgs e) => _nats?.DisposeAsync();

        private async void BtnPause_Click(object sender, RoutedEventArgs e)
        {
            if (_pauseClient != null)
            {
                await _pauseClient.CallAsync(new PauseTreeReq());
                BtnPause.IsEnabled = false;
                BtnResume.IsEnabled = true;
                TxtResult.Text = "PAUSED";
                BorderResult.Background = new SolidColorBrush(Color.FromRgb(249, 168, 37)); // 橙色
            }
        }

        private async void BtnResume_Click(object sender, RoutedEventArgs e)
        {
            if (_resumeClient != null)
            {
                await _resumeClient.CallAsync(new ResumeTreeReq());
                BtnPause.IsEnabled = true;
                BtnResume.IsEnabled = false;
                TxtResult.Text = "TESTING...";
                BorderResult.Background = new SolidColorBrush(Color.FromRgb(255, 160, 0));
            }
        }
    }
}