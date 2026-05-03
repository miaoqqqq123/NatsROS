using NATS.Client.Core;
using NatsROS.Dashboard.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;
using UserControl = System.Windows.Controls.UserControl;

namespace NatsROS.Dashboard.Plugins.BagPlayer
{
    public partial class BagPlayerView : UserControl, IDisposable
    {
        private readonly INatsClient _nats;

        // 录制状态
        private bool _isRecording = false;
        private FileStream? _bagOutStream;
        private BinaryWriter? _bagWriter;
        private long _recordedMsgCount = 0;
        private CancellationTokenSource? _recordCts;
        public ObservableCollection<RecordTopicItem> RecordTopics { get; set; } = new();
        private readonly ConcurrentDictionary<string, RecordTopicItem> _recordTopicDict = new();

        // 回放状态
        public ObservableCollection<BagTopicInfo> BagTopics { get; set; } = new();
        private bool _isPlaying = false;
        private bool _isPaused = false;
        private bool _isDraggingSlider = false;
        private CancellationTokenSource? _playCts;
        private long _bagStartTick = 0;
        private double _bagTotalSeconds = 0;

        public BagPlayerView(INatsClient nats)
        {
            InitializeComponent();
            _nats = nats;
            GridBagTopics.ItemsSource = BagTopics;

            // 绑定录制网格
            GridRecordTopics.ItemsSource = RecordTopics;
        }

        // ==========================================
        // 0. 预扫描网络话题
        // ==========================================
        private async void BtnRefreshTopics_Click(object sender, RoutedEventArgs e)
        {
            BtnRefreshTopics.IsEnabled = false;
            BtnRefreshTopics.Content = "⏳ 嗅探中...";
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
                var rawSub = _nats.SubscribeAsync<byte[]>(">", serializer: NATS.Client.Core.NatsDefaultSerializerRegistry.Default.GetDeserializer<byte[]>(), cancellationToken: cts.Token);

                await foreach (var msg in rawSub)
                {
                    if (msg.Subject.StartsWith("_INBOX.")) continue;

                    var item = _recordTopicDict.GetOrAdd(msg.Subject, key =>
                    {
                        var newItem = new RecordTopicItem { TopicName = key, RecordedCount = 0 };
                        Dispatcher.InvokeAsync(() => RecordTopics.Add(newItem));
                        return newItem;
                    });

                    // 提取并更新类型
                    if (item.TopicType == "未知" && msg.Headers != null && msg.Headers.TryGetValue("ros-type", out var typeVal))
                    {
                        string t = typeVal.ToString().Trim();
                        Dispatcher.InvokeAsync(() => item.TopicType = t);
                    }
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                BtnRefreshTopics.IsEnabled = true;
                BtnRefreshTopics.Content = "🔄 预扫描网络话题";
            }
        }

        // ==========================================
        // 1. 极速录制引擎
        // ==========================================
        private void BtnToggleRecord_Click(object sender, RoutedEventArgs e)
        {
            if (!_isRecording)
            {
                try
                {
                    string dir = @"C:\temp"; if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    string path = Path.Combine(dir, $"natsros_{DateTime.Now:yyyyMMdd_HHmmss}.bag");
                    _bagOutStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
                    _bagWriter = new BinaryWriter(_bagOutStream);
                    TxtBagSavePath.Text = path;
                    _recordedMsgCount = 0;
                    _isRecording = true;

                    // 【核心魔法】：刚开始录制时，把表格里已经存在且勾选的话题的“类型信息”作为【元数据帧(Tick=-1)】先写入文件！
                    foreach (var item in RecordTopics)
                    {
                        if (item.IsRecordEnabled)
                        {
                            _bagWriter.Write(-1L); // Magic Tick = -1 代表元数据
                            _bagWriter.Write(item.TopicName);
                            _bagWriter.Write(item.TopicType); // 写入类型字符串
                        }
                    }

                    BtnToggleRecord.Content = "⏹ 停止录制 (Stop)";
                    BtnToggleRecord.Background = new SolidColorBrush(Colors.LightGray);
                    BtnToggleRecord.Foreground = new SolidColorBrush(Colors.Black);

                    _recordCts = new CancellationTokenSource();
                    _ = StartRecordingTaskAsync(_recordCts.Token);
                }
                catch (Exception ex) { MessageBox.Show($"创建 Bag 失败: {ex.Message}"); }
            }
            else
            {
                StopRecording();
            }
        }

        private async Task StartRecordingTaskAsync(CancellationToken ct)
        {
            try
            {
                var rawSub = _nats.SubscribeAsync<byte[]>(">", serializer: NATS.Client.Core.NatsDefaultSerializerRegistry.Default.GetDeserializer<byte[]>(), cancellationToken: ct);
                await foreach (var msg in rawSub)
                {
                    if (!_isRecording || _bagWriter == null || msg.Data == null || msg.Subject.StartsWith("_INBOX.")) continue;

                    var subject = msg.Subject;

                    // 如果是录制中途冒出来的新话题
                    if (!_recordTopicDict.TryGetValue(subject, out var recordItem))
                    {
                        recordItem = new RecordTopicItem { TopicName = subject, RecordedCount = 0 };

                        if (msg.Headers != null && msg.Headers.TryGetValue("ros-type", out var typeVal))
                        {
                            recordItem.TopicType = typeVal.ToString().Split(',')[0].Trim();
                            recordItem.TopicType = typeVal.ToString().Trim();
                        }
                            

                        _recordTopicDict[subject] = recordItem;
                        Dispatcher.InvokeAsync(() => RecordTopics.Add(recordItem));

                        // 将新话题的【元数据帧】补写入文件
                        if (recordItem.IsRecordEnabled)
                        {
                            _bagWriter.Write(-1L);
                            _bagWriter.Write(recordItem.TopicName);
                            _bagWriter.Write(recordItem.TopicType);
                        }
                    }

                    if (!recordItem.IsRecordEnabled) continue;

                    // 写入正式的【数据帧】
                    _bagWriter.Write(DateTime.UtcNow.Ticks);
                    _bagWriter.Write(subject);
                    _bagWriter.Write(msg.Data.Length);
                    _bagWriter.Write(msg.Data);

                    recordItem.RecordedCount++;
                    _recordedMsgCount++;

                    if (_recordedMsgCount % 50 == 0)
                        Dispatcher.InvokeAsync(() => LblRecordStats.Content = $"已录制: {_recordedMsgCount} 条 | 大小: {_bagOutStream!.Length / 1024 / 1024.0:F2} MB");
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private void StopRecording()
        {
            _isRecording = false;
            _recordCts?.Cancel();
            _bagWriter?.Dispose();
            _bagOutStream?.Dispose();

            BtnToggleRecord.Content = "⏺ 开始录制 (Start Record)";
            BtnToggleRecord.Background = new SolidColorBrush(Color.FromRgb(255, 221, 221));
            BtnToggleRecord.Foreground = new SolidColorBrush(Colors.DarkRed);
            LblRecordStats.Content = $"录制结束，共保存 {_recordedMsgCount} 条报文。";
        }

        // ==========================================
        // 2. 扫盘解析引擎
        // ==========================================
        private async void BtnLoadBag_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "NatsROS Bag Files (*.bag)|*.bag" };
            if (dlg.ShowDialog() == true)
            {
                TxtBagLoadPath.Text = dlg.FileName; BtnPlayBag.IsEnabled = false; BagTopics.Clear();
                await Task.Run(() =>
                {
                    try
                    {
                        using var fs = new FileStream(dlg.FileName, FileMode.Open, FileAccess.Read);
                        using var reader = new BinaryReader(fs);
                        var topicCounts = new Dictionary<string, int>();
                        var topicTypes = new Dictionary<string, string>(); // 暂存文件里读出的类型
                        _bagStartTick = -1;
                        long lastTick = 0;

                        while (fs.Position < fs.Length)
                        {
                            long tick = reader.ReadInt64();
                            string topic = reader.ReadString();

                            // 读取我们刚才存入的内联元数据
                            if (tick == -1L)
                            {
                                // 这是一条元数据帧
                                string typeName = reader.ReadString();
                                topicTypes[topic] = typeName;
                                continue; // 元数据读完直接跳过，它没有 Payload
                            }

                            // 正常的数据帧
                            int dataLen = reader.ReadInt32();
                            fs.Seek(dataLen, SeekOrigin.Current);
                            if (_bagStartTick == -1) _bagStartTick = tick;
                            lastTick = tick;
                            if (topicCounts.ContainsKey(topic))
                                topicCounts[topic]++;
                            else
                                topicCounts[topic] = 1;
                        }
                        _bagTotalSeconds = TimeSpan.FromTicks(lastTick - _bagStartTick).TotalSeconds;
                        Dispatcher.Invoke(() =>
                        {
                            foreach (var kvp in topicCounts) BagTopics.Add(new BagTopicInfo { TopicName = kvp.Key, MessageCount = kvp.Value, TopicType = topicTypes[kvp.Key] });
                            SliderTimeline.Maximum = _bagTotalSeconds;
                            SliderTimeline.Value = 0;
                            LblPlayTime.Content = $"00:00.0 / {_bagTotalSeconds:F1}s"; BtnPlayBag.IsEnabled = true;
                        });
                    }
                    catch (Exception ex) { Dispatcher.Invoke(() => MessageBox.Show($"解析失败: {ex.Message}")); }
                });
            }
        }

        private void SliderTimeline_EditValueChanged(object sender, DevExpress.Xpf.Editors.EditValueChangedEventArgs e)
        {
            if (SliderTimeline.IsKeyboardFocusWithin || SliderTimeline.IsMouseOver) _isDraggingSlider = true;
        }

        private void BtnPauseBag_Click(object sender, RoutedEventArgs e)
        {
            _isPaused = !_isPaused; BtnPauseBag.Content = _isPaused ? "▶ 继续" : "⏸ 暂停";
        }

        // ==========================================
        // 3. 时光倒流引擎
        // ==========================================
        private async void BtnPlayBag_Click(object sender, RoutedEventArgs e)
        {
            if (_isPlaying) { _playCts?.Cancel(); return; }

            string bagFilePath = TxtBagLoadPath.Text;
            if (_nats == null || string.IsNullOrEmpty(bagFilePath)) return;

            string playSpeedText = CboPlaySpeed.Text;
            double speedMultiplier = 1.0;
            if (playSpeedText.Contains("0.5")) speedMultiplier = 0.5;
            else if (playSpeedText.Contains("2.0")) speedMultiplier = 2.0;
            else if (playSpeedText.Contains("5.0")) speedMultiplier = 5.0;
            else if (playSpeedText.Contains("Max")) speedMultiplier = 9999.0;

            HashSet<string> enabledTopics = new HashSet<string>();
            foreach (var t in BagTopics) { if (t.IsPlayEnabled) enabledTopics.Add(t.TopicName); }

            _isPlaying = true; _playCts = new CancellationTokenSource(); BtnPauseBag.IsEnabled = true;
            BtnPlayBag.Content = "⏹ 停止播放"; BtnPlayBag.Background = new SolidColorBrush(Colors.LightGray); BtnPlayBag.Foreground = new SolidColorBrush(Colors.Black);

            try
            {
                await Task.Run(async () =>
                {
                    using var fs = new FileStream(bagFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
                    using var reader = new BinaryReader(fs);
                    long playbackStartTick = DateTime.UtcNow.Ticks;
                    long playedCount = 0;
                    var rawSerializer = NATS.Client.Core.NatsDefaultSerializerRegistry.Default.GetSerializer<byte[]>();

                    // 用于暂存回放过程中遇到的所有话题的 Header 身份证！
                    var topicHeadersDict = new Dictionary<string, NatsHeaders>();

                    while (fs.Position < fs.Length && !_playCts.Token.IsCancellationRequested)
                    {
                        while (_isPaused && !_playCts.Token.IsCancellationRequested) await Task.Delay(100);

                        long recordedTick = reader.ReadInt64();
                        string topic = reader.ReadString();

                        // 如果是元数据帧，读取掉类型字符串，然后直接跳过！绝不发送！
                        if (recordedTick == -1L)
                        {
                            // 1. 读取当年冻结的类型字符串
                            string typeFullName = reader.ReadString();

                            // 2. 如果类型不是"未知"，就把它做成一个新鲜的 NatsHeaders 存起来！
                            if (typeFullName != "未知")
                            {
                                topicHeadersDict[topic] = new NatsHeaders { { "ros-type", typeFullName } };
                            }

                            continue;
                        }

                        int dataLen = reader.ReadInt32();
                        byte[] data = reader.ReadBytes(dataLen);


                        if (!enabledTopics.Contains(topic)) continue;

                        if (speedMultiplier < 9999.0 && !_isDraggingSlider)
                        {
                            long targetElapsedTicks = (long)((recordedTick - _bagStartTick) / speedMultiplier);
                            long delayTicks = targetElapsedTicks - (DateTime.UtcNow.Ticks - playbackStartTick);
                            if (delayTicks > 10000) await Task.Delay(TimeSpan.FromTicks(delayTicks), _playCts.Token);
                        }


                        // 3. 【核心升维】：发送时，看看字典里有没有当时保存的头信息，如果有，贴上去再发！
                        topicHeadersDict.TryGetValue(topic, out var headersToAttach);
                        await _nats.PublishAsync(
                                                    topic,
                                                    data: data,
                                                    headers: headersToAttach, // 带着身份证重生！
                                                    serializer: rawSerializer,
                                                    cancellationToken: _playCts.Token);

                        playedCount++;
                        //if (playedCount % 50 == 0)
                        {
                            double currentSec = TimeSpan.FromTicks(recordedTick - _bagStartTick).TotalSeconds;
                            await Dispatcher.InvokeAsync(() =>
                            {
                                if (!_isDraggingSlider)
                                    SliderTimeline.Value = currentSec;
                                LblPlayTime.Content = $"{currentSec:F1} / {_bagTotalSeconds:F1}s";
                            });
                        }
                    }
                }, _playCts.Token);
            }
            catch { }
            finally
            {
                _isPlaying = false; _isDraggingSlider = false; BtnPauseBag.IsEnabled = false; _isPaused = false; BtnPauseBag.Content = "⏸ 暂停";
                BtnPlayBag.Content = "▶️ 播放 (Play)"; BtnPlayBag.Background = new SolidColorBrush(Color.FromRgb(221, 255, 221)); BtnPlayBag.Foreground = new SolidColorBrush(Colors.DarkGreen);
            }
        }

        public void Dispose()
        {
            StopRecording();
            _playCts?.Cancel();
        }
    }
}
