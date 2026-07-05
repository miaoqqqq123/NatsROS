using NATS.Client.Core;
using NatsROS.Core.Communication;
using NatsROS.Core.SystemMessages;
using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Threading;
using UserControl = System.Windows.Controls.UserControl;

namespace NatsROS.Dashboard.Plugins.Watchdog
{
    public class ApmItem : INotifyPropertyChanged
    {
        private byte _lifecycleState; private double _cpuUsagePercent; private double _memoryWorkingSetMb; private int _threadCount; private double _uptimeSeconds;
        public string NodeName { get; set; } = "";
        public string StateStr => _lifecycleState switch { 0 => "⚪ Unconfigured", 1 => "🟡 Inactive", 2 => "🟢 Active", 3 => "🔴 Faulted", _ => "⚫ Finalized" };
        public string UptimeStr => TimeSpan.FromSeconds(_uptimeSeconds).ToString(@"dd\.hh\:mm\:ss");

        public byte LifecycleState { get => _lifecycleState; set { _lifecycleState = value; OnPropertyChanged(); OnPropertyChanged(nameof(StateStr)); } }
        public double CpuUsagePercent { get => _cpuUsagePercent; set { _cpuUsagePercent = value; OnPropertyChanged(); } }
        public double MemoryWorkingSetMb { get => _memoryWorkingSetMb; set { _memoryWorkingSetMb = value; OnPropertyChanged(); } }
        public int ThreadCount { get => _threadCount; set { _threadCount = value; OnPropertyChanged(); } }
        public double UptimeSeconds { get => _uptimeSeconds; set { _uptimeSeconds = value; OnPropertyChanged(); OnPropertyChanged(nameof(UptimeStr)); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public partial class WatchdogView : UserControl, IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        public ObservableCollection<ApmItem> ApmList { get; set; } = new();
        private readonly ConcurrentDictionary<string, ApmItem> _apmDict = new();

        public WatchdogView(INatsClient nats)
        {
            InitializeComponent();
            GridApm.ItemsSource = ApmList;

            _ = Task.Run(async () =>
            {
                try
                {
                    var sub = new RosSubscriber<NodeHeartbeatMsg>(nats, "sys.apm.heartbeat", RosQosProfile.SensorData);
                    await foreach (var msg in sub.SubscribeAsync(_cts.Token))
                    {
                        if (msg != null)
                        {
                            var data = msg;
                            Dispatcher.InvokeAsync(() =>
                            {
                                if (!_apmDict.TryGetValue(data.NodeName, out var item))
                                {
                                    item = new ApmItem { NodeName = data.NodeName };
                                    _apmDict[data.NodeName] = item;
                                    ApmList.Add(item);
                                }
                                item.LifecycleState = data.LifecycleState;
                                item.CpuUsagePercent = data.CpuUsagePercent;
                                item.MemoryWorkingSetMb = data.MemoryWorkingSetMb;
                                item.ThreadCount = data.ThreadCount;
                                item.UptimeSeconds = data.UptimeSeconds;
                            });
                        }
                    }
                }
                catch (OperationCanceledException) { }
            }, _cts.Token);
        }

        public void Dispose() => _cts.Cancel();
    }
}