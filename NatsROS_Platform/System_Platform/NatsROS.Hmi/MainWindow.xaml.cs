using NATS.Client.Core;
using NATS.Net;
using NatsROS.Core.Communication;
using NatsROS.Core.Serialization;
using NatsROS.Core.UI;
using NatsROS.Messages.AEM;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MessageBox = System.Windows.MessageBox;

namespace NatsROS.Hmi
{
    public partial class MainWindow : DevExpress.Xpf.Core.ThemedWindow
    {
        private INatsClient? _nats;
        private RosServiceClient<AckAlarmReq, AckAlarmRes>? _ackClient;
        private string _currentHighestAlarmCode = "";

        // 插件缓存
        private readonly List<IHmiPlugin> _discoveredPlugins = new();

        public MainWindow()
        {
            DevExpress.Xpf.Core.ApplicationThemeHelper.ApplicationThemeName = "Win11Dark";
            InitializeComponent();
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var options = NatsOpts.Default with { SerializerRegistry = new NatsRosSerializerRegistry() };
                _nats = new NatsClient(options);
                await _nats.ConnectAsync();

                // 1. 初始化 AEM 报警复位客户端
                _ackClient = new RosServiceClient<AckAlarmReq, AckAlarmRes>(_nats, "aem.ack");

                // 2. 监听全网活动报警变化
                _ = Task.Run(async () =>
                {
                    var alarmSub = new RosSubscriber<AlarmsChangedEvent>(_nats, "aem.changed", RosQosProfile.SensorData);
                    await foreach (var msg in alarmSub.SubscribeAsync())
                        if (msg != null) Dispatcher.Invoke(() => UpdateAlarmBanner(msg.ActiveAlarms));
                });

                // 3. 开机同步一次报警
                var syncClient = new RosServiceClient<SyncAlarmsReq, SyncAlarmsRes>(_nats, "aem.sync");
                var syncRes = await syncClient.CallAsync(new SyncAlarmsReq(), TimeSpan.FromSeconds(2));
                if (syncRes != null && syncRes.ActiveAlarms != null) UpdateAlarmBanner(syncRes.ActiveAlarms);

                // ==========================================
                // 【核心逻辑】：扫盘加载机台专属 UI 插件！
                // ==========================================
                ScanAndLoadPlugins();
            }
            catch (Exception ex)
            { 
                MessageBox.Show($"网络连接失败: {ex.Message}"); 
            }
        }

        private void ScanAndLoadPlugins()
        {
            string binPath = AppDomain.CurrentDomain.BaseDirectory;

            // 极速扫描根目录下属于我们公司的 DLL
            var dllFiles = Directory.GetFiles(binPath, "*.dll", SearchOption.TopDirectoryOnly)
                .Where(f => {
                    string name = Path.GetFileName(f);
                    return name.StartsWith("NatsROS") || name.StartsWith("ScrewMachine") || name.StartsWith("Hexiv");
                });

            foreach (var dllPath in dllFiles)
            {
                try { Assembly.LoadFrom(dllPath); } catch { }
            }

            var allAssemblies = AppDomain.CurrentDomain.GetAssemblies().ToList();
            if (!allAssemblies.Contains(Assembly.GetExecutingAssembly())) allAssemblies.Add(Assembly.GetExecutingAssembly());

            foreach (var asm in allAssemblies)
            {
                try
                {
                    var pluginTypes = asm.GetTypes().Where(t => typeof(IHmiPlugin).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);
                    foreach (var type in pluginTypes)
                    {
                        if (Activator.CreateInstance(type) is IHmiPlugin plugin) _discoveredPlugins.Add(plugin);
                    }
                }
                catch { }
            }

            // 按 OrderIndex 排序后加载到界面
            foreach (var plugin in _discoveredPlugins.OrderBy(p => p.OrderIndex))
            {
                var view = plugin.CreateView(App.ServiceProvider); // 传入 DI 容器，方便插件拿 NATS

                var tabItem = new DevExpress.Xpf.Core.DXTabItem
                {
                    Header = plugin.DisplayName,
                    Content = view
                };
                MainTabControl.Items.Add(tabItem);
            }

            if (MainTabControl.Items.Count > 0) MainTabControl.SelectedIndex = 0;
            StatusPluginCount.Content = $"加载了 {_discoveredPlugins.Count} 个工站插件";
        }

        // ==========================================
        // 报警横幅逻辑 (保持原样)
        // ==========================================
        private void UpdateAlarmBanner(List<ActiveAlarmState> activeAlarms)
        {
            if (activeAlarms == null || activeAlarms.Count == 0)
            {
                BannerAlarm.Visibility = Visibility.Collapsed;
                _currentHighestAlarmCode = "";
                MainTabControl.IsEnabled = true; // 解锁工作区
            }
            else
            {
                var highestAlarm = activeAlarms.OrderByDescending(a => a.Level).ThenByDescending(a => a.LastRaisedTime).First();
                _currentHighestAlarmCode = highestAlarm.Code;

                string statusText = highestAlarm.Status == AlarmStatus.Cleared ? "(物理已恢复，请复位)" : "";
                TxtAlarmMsg.Text = $"[{highestAlarm.Code}] {highestAlarm.FormattedMessage} (发生 {highestAlarm.Occurrences} 次) {statusText}";

                BannerAlarm.Visibility = Visibility.Visible;
                MainTabControl.IsEnabled = false; // 有致命报警时，锁死整个操作台的按钮！
            }
        }

        private async void BtnAckAlarm_Click(object sender, RoutedEventArgs e)
        {
            if (_ackClient != null && !string.IsNullOrEmpty(_currentHighestAlarmCode))
            {
                BtnAckAlarm.IsEnabled = false;
                try 
                { 
                    await _ackClient.CallAsync(new AckAlarmReq(_currentHighestAlarmCode), TimeSpan.FromSeconds(2)); 
                }
                catch
                {
                }
                finally { BtnAckAlarm.IsEnabled = true; }
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e) => _nats?.DisposeAsync();
    }
}