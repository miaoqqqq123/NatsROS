using NATS.Client.Core;
using NATS.Net;
using NatsROS.Core.Communication;
using NatsROS.Core.Serialization;
using NatsROS.Messages.ATE;
using NatsROS.Messages.Motion;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;

namespace NatsROS.Hmi
{
    // 用于绑定 UI 表格的数据模型
    public class StepResultItem : INotifyPropertyChanged
    {
        private string _status = "";
        private double _progress;

        public string StepName { get; set; } = "";
        public string Status { get => _status; set { _status = value; OnPropertyChanged(); } }
        public double Progress { get => _progress; set { _progress = value; OnPropertyChanged(); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public partial class MainWindow : DevExpress.Xpf.Core.ThemedWindow
    {
        private INatsClient? _nats;
        private RosActionClient<RunPlanGoal, RunPlanFeedback, RunPlanResult>? _engineClient;
        
        // 绑定到界面的测试步骤列表
        public ObservableCollection<StepResultItem> StepResults { get; set; } = new();
        
        // 记录当前测试的唯一 ID，用于急停
        private string _currentGoalId = "";

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

                // 声明一个纯客户端，指向我们在母体里配好的 OpenTAP 引擎的 Action 路由
                _engineClient = new RosActionClient<RunPlanGoal, RunPlanFeedback, RunPlanResult>(_nats, "engine.engine_1.run");

                // 【神级越权监控】：HMI 根本不管 OpenTAP，直接去网络里截获底层轴的物理反馈！
                // 假设底层轴叫 axis_x
                //var axisFeedbackSub = new RosSubscriber<AxisMoveFeedback>(_nats, "axis_x.move.feedback", RosQosProfile.SensorData);
                //_ = Task.Run(async () =>
                //{
                //    await foreach (var fb in axisFeedbackSub.SubscribeAsync())
                //    {
                //        Dispatcher.InvokeAsync(() => PbAxisPos.Value = fb.CurrentPosition);
                //    }
                //});

                // 【修复】：使用带外衣的 ActionFeedback<T> 进行订阅
                var axisFeedbackSub = new RosSubscriber<ActionFeedback<AxisMoveFeedback>>(_nats, "axis_x.move.feedback", RosQosProfile.SensorData);

                _ = Task.Run(async () =>
                {
                    // 这里收到的 fbMsg 是包含了 GoalId 和真实 Data 的包装对象
                    await foreach (var fbMsg in axisFeedbackSub.SubscribeAsync())
                    {
                        // 剥开外衣，拿到真正的 CurrentPosition
                        Dispatcher.InvokeAsync(() => PbAxisPos.Value = fbMsg.Data.CurrentPosition);
                    }
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"网络连接失败: {ex.Message}");
            }
        }

        private void Window_Closing(object sender, CancelEventArgs e) => _nats?.DisposeAsync();

        // ==========================================
        // 1. 发起测试 (呼叫远端 OpenTAP 引擎)
        // ==========================================
        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (_engineClient == null || string.IsNullOrWhiteSpace(TxtBarcode.Text)) return;

            // UI 状态重置
            StepResults.Clear();
            TxtResult.Text = "TESTING...";
            BorderResult.Background = new SolidColorBrush( Color.FromRgb(255, 160, 0)); // 橙色
            BtnStart.IsEnabled = false;

            try
            {
                var goal = new RunPlanGoal(Barcode: TxtBarcode.Text, TaskId: Guid.NewGuid().ToString());
                _currentGoalId = goal.TaskId; // 真实的 GoalId 在 ActionClient 内部生成，这里简写记录意图

                // 【核心魔法】：发起分布式长任务，并挂载一个回调函数，实时听取 OpenTAP 传回来的弹幕！
                var result = await _engineClient.SendGoalAsync(goal, feedback =>
                {
                    // 切回 UI 线程更新表格
                    Dispatcher.InvokeAsync(() =>
                    {
                        // 找找这个步骤是否已经在表格里了
                        var existingStep = StepResults.FirstOrDefault(s => s.StepName == feedback.StepName);
                        if (existingStep != null)
                        {
                            existingStep.Status = feedback.Status;
                            existingStep.Progress = feedback.ProgressPercentage;
                        }
                        else
                        {
                            // 这是一个新步骤，加到表格最后
                            StepResults.Add(new StepResultItem 
                            { 
                                StepName = feedback.StepName, 
                                Status = feedback.Status, 
                                Progress = feedback.ProgressPercentage 
                            });
                            // 让表格自动滚动到底部 (可选)
                            GridSteps.View.FocusedRowHandle = StepResults.Count - 1;
                        }
                    });
                });

                // 流程结束，展示最终极其醒目的结果
                if (result != null && result.Status == ActionStatus.Succeeded && result.Data.IsPassed)
                {
                    TxtResult.Text = "PASS";
                    BorderResult.Background = new SolidColorBrush(Color.FromRgb(46, 125, 50)); // 绿色
                }
                else
                {
                    TxtResult.Text = "FAIL";
                    BorderResult.Background = new SolidColorBrush(Color.FromRgb(198, 40, 40)); // 红色
                }
            }
            catch (Exception ex)
            {
                TxtResult.Text = "ERROR";
                BorderResult.Background = new SolidColorBrush(Colors.DarkRed);
                // 【核心侦探魔法】：把当前 HMI 试图呼叫的真实地址暴露出来！
                string targetSubject = _engineClient?.GetType()
                    .GetProperty("ServiceName", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)?
                    .GetValue(_engineClient)?.ToString() ?? "未知地址";

                MessageBox.Show(
                    $"测试异常: {ex.Message}\n\n" +
                    $"🕵️ 排障信息：\n" +
                    $"HMI 正在呼叫的地址: [{targetSubject}]\n" +
                    $"请检查 Dashboard 的【节点拓扑图】中，是否真的存在名为 [{targetSubject}] 的紫色菱形节点！",
                    "严重错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnStart.IsEnabled = true;
            }
        }

        // ==========================================
        // 2. 分布式急停 (瞬间截停底层硬件)
        // ==========================================
        private async void BtnEStop_Click(object sender, RoutedEventArgs e)
        {
            if (_engineClient != null)
            {
                // 发送分布式的取消指令，底层 OpenTAP 收到后会瞬间触发 TapThread.Abort()！
                await _engineClient.CancelGoalAsync(_currentGoalId); // 实际中应传入 Client 真正生成的 GoalId
                TxtResult.Text = "ABORTED";
                BorderResult.Background = new SolidColorBrush(Colors.DarkRed);
            }
        }
    }
}