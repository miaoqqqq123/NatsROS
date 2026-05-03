using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using NATS.Client.Core;
using NatsROS.Core.SystemMessages;
using NatsROS.Dashboard.Models;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;

namespace NatsROS.Dashboard
{
    public partial class MainWindow
    {
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
                    foreach (var name in res.Data.NodeNames) RunningNodes.Add(new NodeItem { NodeName = name });
                    GridNodes.RefreshData();
                }
            }
            catch { AppendLog("ERROR", $"获取节点列表超时，母体可能未运行。", Colors.Red); }
        }

        private async void BtnLoadNode_Click(object sender, RoutedEventArgs e)
        {
            if (_nats == null) return;

            // 获取下拉框选中的智能对象
            var selectedNodeInfo = CboAvailableNodes.SelectedItem as AvailableNodeInfo;
            var nodeName = TxtNodeName.Text;

            if (selectedNodeInfo == null || string.IsNullOrWhiteSpace(nodeName))
            {
                MessageBox.Show("请从下拉框中选择一个节点类，并为其指定一个运行节点名！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 提取 AssemblyName 和 TypeName
            var req = new LoadNodeReq(selectedNodeInfo.AssemblyName, selectedNodeInfo.TypeName, nodeName);

            try
            {
                var res = await _nats.RequestAsync<LoadNodeReq, LoadNodeRes>(
                    "container.load_node", req, replyOpts: new NatsSubOpts { Timeout = TimeSpan.FromSeconds(3) });

                if (res.Data != null && res.Data.Success)
                {
                    AppendLog("UI", $"指令下达成功！节点 [{req.NodeName}] 已在母体内激活。", Colors.Cyan);
                    await RefreshNodeListAsync();
                }
                else MessageBox.Show(res.Data?.Message ?? "无响应", "加载失败");
            }
            catch (Exception ex) { MessageBox.Show($"RPC 失败: {ex.Message}", "错误"); }
        }

        private async void BtnUnloadNode_Click(object sender, RoutedEventArgs e)
        {
            if (_nats == null || GridNodes.SelectedItem is not NodeItem selectedItem) return;
            try
            {
                var req = new UnloadNodeReq(selectedItem.NodeName);
                var res = await _nats.RequestAsync<UnloadNodeReq, UnloadNodeRes>(
                    "container.unload_node", req, replyOpts: new NatsSubOpts { Timeout = TimeSpan.FromSeconds(3) });

                if (res.Data != null && res.Data.Success)
                {
                    AppendLog("UI", $"节点 [{req.NodeName}] 已成功卸载。", Colors.Yellow);
                    await RefreshNodeListAsync();
                }
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }

        private async Task ListenToRosOutAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var msg in _nats!.SubscribeAsync<LogMsg>("rosout", cancellationToken: ct))
                {
                    if (msg.Data is not null)
                    {
                        var log = msg.Data;
                        Color color = log.Level switch { 10 => Colors.DarkGray, 20 => Colors.White, 30 => Colors.Yellow, _ => Colors.Red };
                        var timeStr = DateTimeOffset.FromUnixTimeMilliseconds(log.Stamp).ToLocalTime().ToString("HH:mm:ss.fff");
                        string levelStr = log.Level switch { 10 => "DEBUG", 20 => "INFO", 30 => "WARN", 40 => "ERROR", 50 => "FATAL", _ => "UNK" };
                        string formattedMsg = $"[{timeStr}] [{levelStr.PadRight(5)}] [{log.Name.PadRight(35)}] {log.Msg}";
                        //Dispatcher.Invoke(() => AppendLog(log.Name, formattedMsg, color));
                        AppendLog(log.Name, formattedMsg, color);
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        private void AppendLog(string source, string text, Color color)
        {
            // 极其轻量，耗时几乎为 0，绝对不会卡死后台 NATS 窃听线程
            _logQueue.Enqueue((text, color));

            //var range = new TextRange(LogDocument.ContentEnd, LogDocument.ContentEnd) { Text = text + Environment.NewLine };
            //range.ApplyPropertyValue(TextElement.ForegroundProperty, new SolidColorBrush(color));
            //RtbLogs.ScrollToEnd();
        }

        // ==========================================
        // 核心修改 2：UI 线程专用的批量渲染器 (每 100ms 触发)
        // ==========================================
        private void LogRenderTimer_Tick(object? sender, EventArgs e)
        {
            if (_logQueue.IsEmpty) return;

            // 冻结 RichTextBox 的重绘机制，避免闪烁和卡顿
            RtbLogs.BeginChange();

            int count = 0;
            // 每次最多只消费 500 条，防止挤压过多导致 UI 单次渲染时间过长
            while (count < 500 && _logQueue.TryDequeue(out var logItem))
            {
                // 使用 Paragraph 包装，去除默认段落间距，使其紧凑
                var p = new Paragraph(new Run(logItem.Text) { Foreground = new SolidColorBrush(logItem.Color) })
                {
                    Margin = new Thickness(0)
                };
                LogDocument.Blocks.Add(p);
                count++;
            }

            // 【性能保险】：只保留最近的 1000 行日志，防止内存撑爆和彻底卡死
            while (LogDocument.Blocks.Count > 1000)
            {
                LogDocument.Blocks.Remove(LogDocument.Blocks.FirstBlock);
            }

            // 恢复重绘机制
            RtbLogs.EndChange();

            // 一次性滚动到底部
            RtbLogs.ScrollToEnd();
        }

    }
}
