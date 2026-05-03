using NATS.Client.Core;
using NATS.Net;
using NatsROS.Core;
using NatsROS.Core.Serialization;
using NatsROS.Dashboard.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;

namespace NatsROS.Dashboard
{
    public partial class MainWindow : DevExpress.Xpf.Core.ThemedWindow
    {
        // ==========================================
        // 全局状态变量集中管理
        // ==========================================
        private INatsClient? _nats;
        private readonly CancellationTokenSource _logCts = new();

        // Tab 1 & Tab 2 数据源
        public ObservableCollection<NodeItem> RunningNodes { get; set; } = new();
        public ObservableCollection<TopicMonitorItem> MonitoredTopics { get; set; } = new();
        private readonly ConcurrentDictionary<string, TopicMonitorItem> _topicDict = new();

        // Tab 2: 雷达状态
        private readonly DispatcherTimer _hzTimer = new();
        private TopicMonitorItem? _selectedTopicItem;

        // Tab 3 & 4: 发布器与参数状态
        private readonly DispatcherTimer _publisherTimer = new();
        private bool _isPublishing = false;
        private string? _currentEditingNodeName;
        private DynamicParameterObject? _currentParams;

        // Tab 6: Bag 录制与回放状态
        public ObservableCollection<BagTopicInfo> BagTopics { get; set; } = new();
        private bool _isRecording = false;
        private FileStream? _bagOutStream;
        private BinaryWriter? _bagWriter;
        private long _recordedMsgCount = 0;
        private bool _isPlaying = false;
        private bool _isPaused = false;
        private bool _isDraggingSlider = false;
        private CancellationTokenSource? _playCts;
        private long _bagStartTick = 0;
        private long _bagEndTick = 0;
        private double _bagTotalSeconds = 0;

        // ==========================================
        // 【新增】：高频日志缓冲队列与渲染定时器
        // ==========================================
        private readonly ConcurrentQueue<(string Text, Color Color)> _logQueue = new();
        private readonly DispatcherTimer _logRenderTimer = new();

        public MainWindow()
        {
            InitializeComponent();

            // 绑定数据源
            GridNodes.ItemsSource = RunningNodes;
            GridTopics.ItemsSource = MonitoredTopics;
            GridBagTopics.ItemsSource = BagTopics;

            // 定时器初始化
            _hzTimer.Interval = TimeSpan.FromSeconds(1);
            _hzTimer.Tick += CalculateHz_Tick;
            _publisherTimer.Tick += PublisherTimer_Tick;

            // 【新增】：配置日志批量渲染定时器 (每 100 毫秒刷新一次屏幕)
            _logRenderTimer.Interval = TimeSpan.FromMilliseconds(100);
            _logRenderTimer.Tick += LogRenderTimer_Tick;
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            var options = NatsOpts.Default with { SerializerRegistry = new NatsRosSerializerRegistry() };
            _nats = new NatsClient(options);

            try
            {
                await _nats.ConnectAsync();
                AppendLog("SYSTEM", "✅ 成功连接到 NATS 网络核心！", Colors.LimeGreen);

                _ = ListenToRosOutAsync(_logCts.Token);
                _ = StartTopicRadarAsync(_logCts.Token);

                // 【新增】：启动日志渲染引擎
                _logRenderTimer.Start();

                _hzTimer.Start();
                await RefreshNodeListAsync();
            }
            catch (Exception ex) { MessageBox.Show($"网络连接失败: {ex.Message}", "严重错误"); }

            // 主动扫盘加载契约 DLL
            try
            {
                string binPath = AppDomain.CurrentDomain.BaseDirectory;
                var dllFiles = Directory.GetFiles(binPath, "NatsROS.*.dll");
                foreach (var dllPath in dllFiles) { try { Assembly.LoadFrom(dllPath); } catch { } }

                var availableTypes = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                    .Where(t => typeof(IRosMessage).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
                    .Select(t => new RosMessageTypeInfo { FullName = t.FullName ?? "", Type = t })
                    .OrderBy(t => t.FullName).ToList();

                CboPubType.ItemsSource = availableTypes;
                CboSrvReqType.ItemsSource = availableTypes;
                CboSrvResType.ItemsSource = availableTypes;
                AppendLog("SYSTEM", $"✅ 成功扫描并加载了 {availableTypes.Count} 个消息契约类型！", Colors.Cyan);

                // ==========================================
                // 【新增】：扫描所有可用的业务节点类 (RosNode 的非抽象子类)
                // ==========================================
                var availableNodes = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                    .Where(t => typeof(NatsROS.Core.RosNode).IsAssignableFrom(t) && !t.IsAbstract)
                    .Select(t => new AvailableNodeInfo
                    {
                        AssemblyName = t.Assembly.GetName().Name ?? "",
                        TypeName = t.FullName ?? ""
                    })
                    // 过滤掉内部管理节点，防止用户误点
                    .Where(n => !n.TypeName.Contains("ContainerManagerNode"))
                    .OrderBy(n => n.TypeName)
                    .ToList();

                // 绑定给注入引擎的下拉框，并默认选中第一个
                CboAvailableNodes.ItemsSource = availableNodes;
                if (availableNodes.Count > 0) CboAvailableNodes.SelectedIndex = 0;

                AppendLog("SYSTEM", $"✅ 成功发现了 {availableNodes.Count} 个可注入的业务节点类！", Colors.Cyan);


            }
            catch (Exception ex) { AppendLog("SYSTEM", $"⚠️ 消息类型扫描出现警告: {ex.Message}", Colors.Yellow); }
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            _logCts.Cancel();
            _hzTimer.Stop();
            _publisherTimer.Stop();
            _nats?.DisposeAsync();

            _logRenderTimer.Stop(); // 【新增】停止日志定时器
        }
    }
}
