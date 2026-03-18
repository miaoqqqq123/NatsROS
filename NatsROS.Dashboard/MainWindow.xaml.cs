using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using NATS.Client.Core;
using NATS.Net;
using NatsROS.Core.Serialization;
using NatsROS.Core.SystemMessages;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;


namespace NatsROS.Dashboard
{
    public class NodeItem
    {
        public string NodeName { get; set; } = "";
    }


    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : DevExpress.Xpf.Core.ThemedWindow
    {
        private INatsClient? _nats;
        private CancellationTokenSource _logCts = new();
        public ObservableCollection<NodeItem> RunningNodes { get; set; } = new();

        public MainWindow()
        {
            InitializeComponent();
            GridNodes.ItemsSource = RunningNodes; // 绑定 DX Grid
        }

        // 窗口加载时，连接 NATS 并启动日志监听
        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            var options = NatsOpts.Default with 
            { 
                SerializerRegistry = new NatsRosSerializerRegistry() 
            };

            _nats = new NatsClient(options);

            try
            {
                await _nats.ConnectAsync();
                AppendLog("SYSTEM", "✅ 成功连接到 NATS 网络！", Colors.LimeGreen);

                // 启动后台线程监听全局日志
                _ = ListenToRosOutAsync(_logCts.Token);

                // 自动刷新一次节点列表
                await RefreshNodeListAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"NATS 连接失败: {ex.Message}", "严重错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _logCts.Cancel();
            _nats?.DisposeAsync();
        }

        // ==========================================
        // 核心功能 1：刷新母体节点列表 (RPC)
        // ==========================================
        private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await RefreshNodeListAsync();

        private async Task RefreshNodeListAsync()
        {
            if (_nats == null) return;
            try
            {
                var res = await _nats.RequestAsync<ListNodesReq, ListNodesRes>(
                    "container.list_nodes", new ListNodesReq(), replyOpts: new NatsSubOpts { Timeout = TimeSpan.FromSeconds(2) });

                if (res.Data != null)
                {
                    RunningNodes.Clear();
                    foreach (var name in res.Data.NodeNames)
                    {
                        RunningNodes.Add(new NodeItem { NodeName = name });
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", $"获取节点列表超时，母体可能未运行。({ex.Message})", Colors.Red);
            }
        }

        // ==========================================
        // 核心功能 2：通知母体加载新节点 (RPC)
        // ==========================================
        private async void BtnLoadNode_Click(object sender, RoutedEventArgs e)
        {
            if (_nats == null) return;

            var req = new LoadNodeReq(TxtAssembly.Text, TxtType.Text, TxtNodeName.Text);
            try
            {
                var res = await _nats.RequestAsync<LoadNodeReq, LoadNodeRes>(
                    "container.load_node", req, replyOpts: new NatsSubOpts { Timeout = TimeSpan.FromSeconds(3) });

                if (res.Data != null && res.Data.Success)
                {
                    AppendLog("UI", $"节点 {req.NodeName} 注入成功！", Colors.Cyan);
                    await RefreshNodeListAsync(); // 刷新网格
                }
                else
                {
                    MessageBox.Show(res.Data?.Message ?? "无响应", "加载失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"RPC 失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ==========================================
        // 核心功能 3：通知母体卸载节点 (RPC)
        // ==========================================
        private async void BtnUnloadNode_Click(object sender, RoutedEventArgs e)
        {
            if (_nats == null || GridNodes.SelectedItem is not NodeItem selectedItem)
            {
                MessageBox.Show("请先在列表中选中一个节点！", "提示");
                return;
            }

            try
            {
                var req = new UnloadNodeReq(selectedItem.NodeName);
                var res = await _nats.RequestAsync<UnloadNodeReq, UnloadNodeRes>(
                    "container.unload_node", req, replyOpts: new NatsSubOpts { Timeout = TimeSpan.FromSeconds(3) });

                if (res.Data != null && res.Data.Success)
                {
                    AppendLog("UI", $"节点 {req.NodeName} 已成功卸载。", Colors.Yellow);
                    await RefreshNodeListAsync();
                }
                else
                {
                    MessageBox.Show(res.Data?.Message, "卸载失败");
                }
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }

        // ==========================================
        // 核心功能 4：实时监听彩色日志 (/rosout)
        // ==========================================
        private async Task ListenToRosOutAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var msg in _nats!.SubscribeAsync<LogMsg>("rosout", cancellationToken: ct))
                {
                    if (msg.Data is not null)
                    {
                        var log = msg.Data;
                        Color color = log.Level switch
                        {
                            RosLogLevels.Debug => Colors.DarkGray,
                            RosLogLevels.Info => Colors.White,
                            RosLogLevels.Warn => Colors.Yellow,
                            RosLogLevels.Error or RosLogLevels.Fatal => Colors.Red,
                            _ => Colors.White
                        };

                        var timeStr = DateTimeOffset.FromUnixTimeMilliseconds(log.Stamp).ToLocalTime().ToString("HH:mm:ss.fff");
                        string formattedMsg = $"[{timeStr}] [{log.Name}] {log.Msg}";

                        // 切回主线程更新 UI 文本框
                        Dispatcher.Invoke(() => AppendLog(log.Name, formattedMsg, color));
                    }
                }
            }
            catch (OperationCanceledException) { /* 正常退出 */ }
        }

        private void AppendLog(string source, string text, Color color)
        {
            var range = new TextRange(LogDocument.ContentEnd, LogDocument.ContentEnd)
            {
                Text = text + Environment.NewLine
            };
            range.ApplyPropertyValue(TextElement.ForegroundProperty, new SolidColorBrush(color));

            // 自动滚动到底部
            RtbLogs.ScrollToEnd();
        }
    }
}