using System;
using System.Threading;
using System.Threading.Tasks;
using MessagePack;
using NATS.Client.Core;
using NatsROS.Dashboard.Models;

namespace NatsROS.Dashboard
{
    public partial class MainWindow
    {
        private void GridTopics_SelectedItemChanged(object sender, DevExpress.Xpf.Grid.SelectedItemChangedEventArgs e)
        {
            _selectedTopicItem = e.NewItem as TopicMonitorItem;
            if (_selectedTopicItem == null || !_selectedTopicItem.IsMonitored) PropGridLive.SelectedObject = null;
        }

        private async Task StartTopicRadarAsync(CancellationToken ct)
        {
            try
            {
                var rawSub = _nats!.SubscribeAsync<byte[]>(">", serializer: NATS.Client.Core.NatsDefaultSerializerRegistry.Default.GetDeserializer<byte[]>(), cancellationToken: ct);
                await foreach (var msg in rawSub)
                {
                    var subject = msg.Subject;
                    if (subject.StartsWith("_INBOX.")) continue;

                    if (_isRecording && _bagWriter != null && msg.Data != null)
                    {
                        _bagWriter.Write(DateTime.UtcNow.Ticks);
                        _bagWriter.Write(subject);
                        _bagWriter.Write(msg.Data.Length);
                        _bagWriter.Write(msg.Data);
                        _recordedMsgCount++;
                        if (_recordedMsgCount % 100 == 0) Dispatcher.InvokeAsync(() => LblRecordStats.Content = $"已录制: {_recordedMsgCount} 条报文 | 大小: {_bagOutStream!.Length / 1024 / 1024.0:F2} MB");
                    }

                    var item = _topicDict.GetOrAdd(subject, key =>
                    {
                        var newItem = new TopicMonitorItem { TopicName = key };
                        Dispatcher.Invoke(() => MonitoredTopics.Add(newItem));
                        return newItem;
                    });

                    item.TotalMessages++; item.MessagesInCurrentSecond++; item.LastSize = msg.Data?.Length ?? 0; item.LastActive = DateTime.Now;

                    if (item.RealType == null && msg.Headers != null && msg.Headers.TryGetValue("ros-type", out var typeValues))
                    {
                        string qualifiedName = typeValues.ToString();
                        Dispatcher.Invoke(() => item.MessageTypeName = qualifiedName.Split(',')[0].Trim());
                        item.RealType = Type.GetType(qualifiedName);
                    }

                    if (item.IsMonitored && item == _selectedTopicItem && msg.Data != null && item.RealType != null)
                    {
                        try
                        {
                            var realObj = MessagePackSerializer.Deserialize(item.RealType, msg.Data);
                            Dispatcher.Invoke(() => PropGridLive.SelectedObject = realObj);
                        }
                        catch { }
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        private void CalculateHz_Tick(object? sender, EventArgs e)
        {
            foreach (var item in _topicDict.Values)
            {
                item.Hz = (DateTime.Now - item.LastActive).TotalSeconds > 2 ? 0 : item.MessagesInCurrentSecond;
                item.MessagesInCurrentSecond = 0;
            }
        }
    }
}
