using MessagePack;
using NATS.Client.Core;
using NatsROS.Dashboard.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using UserControl = System.Windows.Controls.UserControl;

namespace NatsROS.Dashboard.Plugins.TopicRadar
{
    public partial class TopicRadarView : UserControl, IDisposable
    {
        private readonly INatsClient _nats;
        private readonly CancellationTokenSource _radarCts = new();
        private readonly DispatcherTimer _hzTimer = new();

        public ObservableCollection<TopicMonitorItem> MonitoredTopics { get; set; } = new();
        private readonly ConcurrentDictionary<string, TopicMonitorItem> _topicDict = new();
        private TopicMonitorItem? _selectedTopicItem;

        // 依赖注入 NATS 客户端
        public TopicRadarView(INatsClient nats)
        {
            InitializeComponent();
            _nats = nats;

            string binPath = AppDomain.CurrentDomain.BaseDirectory;
            var dllFiles = Directory.GetFiles(binPath, "NatsROS.*.dll");
            foreach (var dllPath in dllFiles) { try { Assembly.LoadFrom(dllPath); } catch { } }


            GridTopics.ItemsSource = MonitoredTopics;

            _hzTimer.Interval = TimeSpan.FromSeconds(1);
            _hzTimer.Tick += CalculateHz_Tick;
            _hzTimer.Start();

            // 启动后台截获
            _ = StartTopicRadarAsync(_radarCts.Token);
        }

        private async Task StartTopicRadarAsync(CancellationToken ct)
        {
            try
            {
                var rawSub = _nats.SubscribeAsync<byte[]>(">",
                    serializer: NATS.Client.Core.NatsDefaultSerializerRegistry.Default.GetDeserializer<byte[]>(),
                    cancellationToken: ct);

                await foreach (var msg in rawSub)
                {
                    var subject = msg.Subject;
                    if (subject.StartsWith("_INBOX.")) continue;

                    var item = _topicDict.GetOrAdd(subject, key =>
                    {
                        var newItem = new TopicMonitorItem { TopicName = key };
                        Dispatcher.InvokeAsync(() => MonitoredTopics.Add(newItem));
                        return newItem;
                    });

                    item.TotalMessages++;
                    item.MessagesInCurrentSecond++;
                    item.LastSize = msg.Data?.Length ?? 0;
                    item.LastActive = DateTime.Now;

                    if (item.RealType == null && msg.Headers != null )
                    {
                        var retType = msg.Headers.TryGetValue("ros-type", out var typeValues);
                        if(retType)
                        {
                            string qualifiedName = typeValues.ToString();
                            Dispatcher.InvokeAsync(() => item.MessageTypeName = qualifiedName.Split(',')[0].Trim());
                            item.RealType = Type.GetType(qualifiedName);
                        }
                    }

                    if (item.IsMonitored && item == _selectedTopicItem && msg.Data != null && item.RealType != null)
                    {
                        try
                        {
                            var realObj = MessagePackSerializer.Deserialize(item.RealType, msg.Data);
                            Dispatcher.InvokeAsync(() => PropGridLive.SelectedObject = realObj);
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

        private void GridTopics_SelectedItemChanged(object sender, DevExpress.Xpf.Grid.SelectedItemChangedEventArgs e)
        {
            _selectedTopicItem = e.NewItem as TopicMonitorItem;
            if (_selectedTopicItem == null || !_selectedTopicItem.IsMonitored) PropGridLive.SelectedObject = null;
        }

        // 当用户关闭这个 MDI 窗口时，清理资源和停止监听
        public void Dispose()
        {
            _hzTimer.Stop();
            _radarCts.Cancel();
        }
    }
}
