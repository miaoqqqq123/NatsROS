using NATS.Client.Core;
using NatsROS.Core.Communication;
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
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;
using UserControl = System.Windows.Controls.UserControl;

namespace ScrewMachine.UI
{
    // 这个类原来在 MainWindow 里，现在搬家到这里
    public class StepResultItem : INotifyPropertyChanged
    {
        private string _status = "等待中";
        private double _progress = 0;

        // 行为树节点的全局唯一ID（用于和底层状态精确匹配）
        public string NodeId { get; set; } = "";

        // 在 UI 上显示的工序名称
        public string StepName { get; set; } = "";

        // 状态文字 (如：等待中、🔄 执行中、✅ 通过)
        public string Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(); }
        }

        // 进度条数值 (0~100)
        public double Progress
        {
            get => _progress;
            set { _progress = value; OnPropertyChanged(); }
        }

        // 触发 UI 实时更新的魔法事件
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public partial class DispenserConsole : UserControl, IDisposable
    {
        private INatsClient _nats;
        private CancellationTokenSource _cts = new CancellationTokenSource();
        private RosServiceClient<StartTreeReq, StartTreeRes>? _startClient;
        private RosServiceClient<StopTreeReq, StopTreeRes>? _stopClient;
        private RosServiceClient<PauseTreeReq, PauseTreeRes>? _pauseClient;
        private RosServiceClient<ResumeTreeReq, ResumeTreeRes>? _resumeClient;
        private RosServiceClient<BtTopologyReq, BtTopologyMsg>? _topologyClient;
        private RosServiceClient<GetRecipesReq, GetRecipesRes>? _rmsClient;

        private const string NODE_BRAIN = "brainnode_1";
        private const string NODE_DISPENSER = "simulateddispensernode_1";

        public ObservableCollection<StepResultItem> StepResults { get; set; } = new();
        private readonly Dictionary<string, StepResultItem> _stepDict = new();

        public DispenserConsole(INatsClient nats)
        {
            InitializeComponent();
            _nats = nats;

            GridSteps.ItemsSource = StepResults;

            // 构造函数注入 NATS 后，立刻初始化所有网络客户端
            InitializeClients();

            // 控件加载后，启动所有后台监听
            Loaded += async (s, e) => await OnLoadedAsync();
        }

        private void InitializeClients()
        {
            _startClient = new RosServiceClient<StartTreeReq, StartTreeRes>(_nats, $"{NODE_BRAIN}.bt.start");
            _stopClient = new RosServiceClient<StopTreeReq, StopTreeRes>(_nats, $"{NODE_BRAIN}.bt.stop");
            _pauseClient = new RosServiceClient<PauseTreeReq, PauseTreeRes>(_nats, $"{NODE_BRAIN}.bt.pause");
            _resumeClient = new RosServiceClient<ResumeTreeReq, ResumeTreeRes>(_nats, $"{NODE_BRAIN}.bt.resume");
            _topologyClient = new RosServiceClient<BtTopologyReq, BtTopologyMsg>(_nats, $"{NODE_BRAIN}.bt.topology.request");
            _rmsClient = new RosServiceClient<GetRecipesReq, GetRecipesRes>(_nats, "rms.get_recipes");
        }

        private async Task OnLoadedAsync()
        {
            try
            {
                // 拉取配方
                var res = await _rmsClient!.CallAsync(new GetRecipesReq(), TimeSpan.FromSeconds(2));
                if (res != null)
                {
                    var approved = res.Recipes.Where(r => r.State == RecipeState.Approved).ToList();
                    Dispatcher.Invoke(() => CboRecipe.ItemsSource = approved);
                }

                // 启动所有后台监听 (坐标、行为树状态、全局结果)
                _ = Task.Run(async () => await ListenToPositionAsync(_cts.Token));
                _ = Task.Run(async () => await ListenToBtStateAsync(_cts.Token));
                _ = Task.Run(async () => await ListenToFinalResultAsync(_cts.Token));
            }
            catch (Exception ex) { MessageBox.Show("无法连接到核心服务: " + ex.Message); }
        }

        private async Task ListenToPositionAsync(CancellationToken ct)
        {
            var axisSub = new RosSubscriber<ActionFeedback<DispenserMoveFeedback>>(_nats, $"{NODE_DISPENSER}.move.feedback", RosQosProfile.SensorData);
            var trajSub = new RosSubscriber<ActionFeedback<DispenserMoveFeedback>>(_nats, $"{NODE_DISPENSER}.trajectory.feedback", RosQosProfile.SensorData);
            _ = Task.Run(async () => { await foreach (var msg in axisSub.SubscribeAsync(ct)) UpdatePosition(msg.Data); });
            _ = Task.Run(async () => { await foreach (var msg in trajSub.SubscribeAsync(ct)) UpdatePosition(msg.Data); });
        }
        private void UpdatePosition(DispenserMoveFeedback fb) => Dispatcher.InvokeAsync(() => { PbAxisPosX.Value = fb.CurrentX; PbAxisPosY.Value = fb.CurrentY; PbAxisPosZ.Value = fb.CurrentZ; });

        private async Task ListenToBtStateAsync(CancellationToken ct)
        {
            var stateSub = new RosSubscriber<BtStateMsg>(_nats, $"{NODE_BRAIN}.bt.state", RosQosProfile.SensorData);
            await foreach (var msg in stateSub.SubscribeAsync(ct))
            {
                if (msg == null) continue;
                Dispatcher.InvokeAsync(() => {
                    foreach (var kvp in msg.NodeStates)
                    {
                        if (_stepDict.TryGetValue(kvp.Key, out var item))
                        {
                            if (kvp.Value == BtNodeStatus.Running) { item.Status = "🔄 执行中"; item.Progress = 50; }
                            else if (kvp.Value == BtNodeStatus.Success) { item.Status = "✅ 通过"; item.Progress = 100; }
                            else if (kvp.Value == BtNodeStatus.Failure) { item.Status = "❌ 失败"; item.Progress = 100; }
                            else item.Status = "等待中";
                        }
                    }
                });
            }
        }

        private async Task ListenToFinalResultAsync(CancellationToken ct)
        {
            var resSub = new RosSubscriber<BtResultMsg>(_nats, $"{NODE_BRAIN}.bt.result", RosQosProfile.SensorData);
            await foreach (var msg in resSub.SubscribeAsync(ct))
            {
                if (msg == null) continue;
                Dispatcher.InvokeAsync(() => {
                    TxtResult.Text = msg.IsSuccess ? "PASS" : "FAIL";
                    BorderResult.Background = new SolidColorBrush(msg.IsSuccess ? Color.FromRgb(46, 125, 50) : Color.FromRgb(198, 40, 40));
                    ResetButtons();
                });
            }
        }

        private async Task BuildStepsFromTopologyAsync()
        {
            var topMsg = await _topologyClient!.CallAsync(new BtTopologyReq(), TimeSpan.FromSeconds(2));
            if (topMsg == null) return;
            StepResults.Clear(); _stepDict.Clear();
            foreach (var node in topMsg.Nodes.Where(n => n.Type == BtNodeType.Action))
            {
                var item = new StepResultItem { NodeId = node.Id, StepName = node.Name };
                StepResults.Add(item);
                _stepDict[node.Id] = item;
            }
        }

        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (_startClient == null || CboRecipe.SelectedItem is not RecipeModel recipe) { MessageBox.Show("请先选择生产配方！"); return; }

            await BuildStepsFromTopologyAsync();
            foreach (var step in StepResults) { step.Status = "等待中"; step.Progress = 0; }
            TxtResult.Text = "TESTING...";
            BorderResult.Background = new SolidColorBrush(Color.FromRgb(255, 160, 0));
            BtnStart.IsEnabled = false;

            var context = new Dictionary<string, string>(recipe.Formula) { ["Barcode"] = TxtBarcode.Text };
            try
            {
                var res = await _startClient.CallAsync(new StartTreeReq(context), TimeSpan.FromSeconds(2));
                if (res != null && !res.Success) throw new Exception(res.Message);
            }
            catch (Exception ex)
            {
                TxtResult.Text = "ERROR"; BorderResult.Background = new SolidColorBrush(Colors.DarkRed);
                MessageBox.Show($"启动失败: {ex.Message}");
                ResetButtons();
            }
        }

        private async void BtnPause_Click(object sender, RoutedEventArgs e)
        {
            await _pauseClient!.CallAsync(new PauseTreeReq());
            BtnPause.IsEnabled = false; BtnResume.IsEnabled = true;
            TxtResult.Text = "PAUSED"; BorderResult.Background = new SolidColorBrush(Color.FromRgb(249, 168, 37));
        }

        private async void BtnResume_Click(object sender, RoutedEventArgs e)
        {
            await _resumeClient!.CallAsync(new ResumeTreeReq());
            BtnPause.IsEnabled = true; BtnResume.IsEnabled = false;
            TxtResult.Text = "TESTING..."; BorderResult.Background = new SolidColorBrush(Color.FromRgb(255, 160, 0));
        }

        private async void BtnEStop_Click(object sender, RoutedEventArgs e)
        {
            await _stopClient!.CallAsync(new StopTreeReq());
            TxtResult.Text = "ABORTED"; BorderResult.Background = new SolidColorBrush(Colors.DarkRed);
            ResetButtons();
        }

        private void ResetButtons()
        {
            BtnStart.IsEnabled = true;
            BtnPause.IsEnabled = true;
            BtnResume.IsEnabled = false;
        }

        public void Dispose() => _cts.Cancel();
    }
}