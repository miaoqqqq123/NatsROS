using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using NATS.Client.Core;
using NatsROS.Dashboard.Models;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;

namespace NatsROS.Dashboard
{
    public partial class MainWindow
    {
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
                    TxtBagSavePath.Text = path; _recordedMsgCount = 0; _isRecording = true;
                    BtnToggleRecord.Content = "⏹ 停止录制 (Stop)"; BtnToggleRecord.Background = new SolidColorBrush(Colors.LightGray); BtnToggleRecord.Foreground = new SolidColorBrush(Colors.Black);
                }
                catch (Exception ex) { MessageBox.Show($"创建 Bag 文件失败: {ex.Message}"); }
            }
            else
            {
                _isRecording = false; _bagWriter?.Dispose(); _bagOutStream?.Dispose();
                BtnToggleRecord.Content = "⏺ 开始录制 (Start Record)"; BtnToggleRecord.Background = new SolidColorBrush(Color.FromRgb(255, 221, 221)); BtnToggleRecord.Foreground = new SolidColorBrush(Colors.DarkRed);
                LblRecordStats.Content = $"录制完成: {_recordedMsgCount} 条报文。";
            }
        }

        private async void BtnLoadBag_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "NatsROS Bag Files (*.bag)|*.bag" };
            if (dlg.ShowDialog() == true)
            {
                TxtBagLoadPath.Text = dlg.FileName; 
                BtnPlayBag.IsEnabled = false; 
                BagTopics.Clear();
                await Task.Run(() =>
                {
                    try
                    {
                        using var fs = new FileStream(dlg.FileName, FileMode.Open, FileAccess.Read);
                        using var reader = new BinaryReader(fs);
                        var topicCounts = new Dictionary<string, int>();
                        _bagStartTick = -1;
                        while (fs.Position < fs.Length)
                        {
                            long tick = reader.ReadInt64(); string topic = reader.ReadString(); int dataLen = reader.ReadInt32();
                            fs.Seek(dataLen, SeekOrigin.Current);
                            if (_bagStartTick == -1) _bagStartTick = tick;
                            _bagEndTick = tick;
                            if (topicCounts.ContainsKey(topic)) topicCounts[topic]++; else topicCounts[topic] = 1;
                        }
                        _bagTotalSeconds = TimeSpan.FromTicks(_bagEndTick - _bagStartTick).TotalSeconds;
                        Dispatcher.Invoke(() =>
                        {
                            foreach (var kvp in topicCounts) BagTopics.Add(new BagTopicInfo { TopicName = kvp.Key, MessageCount = kvp.Value });
                            SliderTimeline.Maximum = _bagTotalSeconds; SliderTimeline.Value = 0;
                            LblPlayTime.Content = $"00:00.0 / {_bagTotalSeconds:F1}s"; 
                            BtnPlayBag.IsEnabled = true;
                        });
                    }
                    catch { }
                });
            }
        }

        private void BtnPauseBag_Click(object sender, RoutedEventArgs e)
        {
            _isPaused = !_isPaused; BtnPauseBag.Content = _isPaused ? "▶ 继续" : "⏸ 暂停";
        }

        private void SliderTimeline_EditValueChanged(object sender, DevExpress.Xpf.Editors.EditValueChangedEventArgs e)
        {
            if (SliderTimeline.IsKeyboardFocusWithin || SliderTimeline.IsMouseOver) _isDraggingSlider = true;
        }

        private async void BtnPlayBag_Click(object sender, RoutedEventArgs e)
        {
            if (_isPlaying)
            { 
                _playCts?.Cancel(); 
                return; 
            }
            // ==========================================
            // 【第一步 (在 UI 线程)】：提取所有 UI 控件的值
            // ==========================================
            string bagFilePath = TxtBagLoadPath.Text;
            if (_nats == null || string.IsNullOrEmpty(bagFilePath)) return;

            string playSpeedText = CboPlaySpeed.Text;
            double speedMultiplier = 1.0;
            if (playSpeedText.Contains("0.5")) speedMultiplier = 0.5;
            else if (playSpeedText.Contains("2.0")) speedMultiplier = 2.0;
            else if (playSpeedText.Contains("5.0")) speedMultiplier = 5.0;
            else if (playSpeedText.Contains("Max")) speedMultiplier = 9999.0;

            _isPlaying = true;
            _playCts = new CancellationTokenSource();

            BtnPlayBag.Content = "⏹ 停止播放";
            BtnPlayBag.Background = new SolidColorBrush(Colors.LightGray);
            BtnPlayBag.Foreground = new SolidColorBrush(Colors.Black);

            try
            {
                // ==========================================
                // 【第二步 (进入后台线程)】：只使用刚才提取的局部变量 (bagFilePath, speedMultiplier)
                // ==========================================
                await Task.Run(async () =>
                {
                    // 使用安全提取出来的 bagFilePath 变量
                    using var fs = new FileStream(bagFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
                    using var reader = new BinaryReader(fs);

                    long playbackStartTick = DateTime.UtcNow.Ticks;
                    long playedCount = 0;
                    //var rawSerializer = NATS.Client.Core.NatsDefaultSerializerRegistry.Default.GetSerializer<byte[]>();

                    // ==========================================
                    // 【修复 1】：在启动回放线程前，抓取当前 UI 上被用户勾选的话题名单
                    // ==========================================
                    HashSet<string> enabledTopics = new HashSet<string>();
                    Dispatcher.Invoke(() =>
                    {
                        foreach (var t in BagTopics)
                        {
                            if (t.IsPlayEnabled) enabledTopics.Add(t.TopicName);
                        }
                    });

                    var rawSerializer = NATS.Client.Core.NatsDefaultSerializerRegistry.Default.GetSerializer<byte[]>();


                    while (fs.Position < fs.Length && !_playCts.Token.IsCancellationRequested)
                    {
                        while (_isPaused && !_playCts.Token.IsCancellationRequested) await Task.Delay(100);

                        long recordedTick = reader.ReadInt64();
                        string topic = reader.ReadString();
                        int dataLen = reader.ReadInt32();
                        byte[] data = reader.ReadBytes(dataLen);

                        // ==========================================
                        // 【修复 2】：如果这个话题没有被勾选，直接跳过，绝不发入网络！
                        // ==========================================
                        if (!enabledTopics.Contains(topic))
                        {
                            continue;
                        }

                        if (speedMultiplier < 9999.0 && !_isDraggingSlider)
                        {
                            long targetElapsedTicks = (long)((recordedTick - _bagStartTick) / speedMultiplier);
                            long delayTicks = targetElapsedTicks - (DateTime.UtcNow.Ticks - playbackStartTick);
                            if (delayTicks > 10000) await Task.Delay(TimeSpan.FromTicks(delayTicks), _playCts.Token);
                        }

                        // 将历史数据打入网络
                        await _nats.PublishAsync(topic, data: data, serializer: rawSerializer, cancellationToken: _playCts.Token);

                        playedCount++;
                        if (playedCount % 50 == 0)
                        {
                            double currentSec = TimeSpan.FromTicks(recordedTick - _bagStartTick).TotalSeconds;

                            // 更新 UI 控件必须使用 Dispatcher.InvokeAsync
                            _ = Dispatcher.InvokeAsync(() =>
                            {
                                if (!_isDraggingSlider) SliderTimeline.Value = currentSec;
                                LblPlayTime.Content = $"{currentSec:F1} / {_bagTotalSeconds:F1}s";
                            });
                        }
                    }
                }, _playCts.Token);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
            finally
            { 
                _isPlaying = false; 
                BtnPlayBag.Content = "▶️ 播放 (Play)"; 
                BtnPlayBag.Background = new SolidColorBrush(Color.FromRgb(221, 255, 221)); 
                BtnPlayBag.Foreground = new SolidColorBrush(Colors.DarkGreen); 
                _isDraggingSlider = false; 
            }
        }
    }
}
