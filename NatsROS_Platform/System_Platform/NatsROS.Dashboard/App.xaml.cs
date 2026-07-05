using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NATS.Net;
using NatsROS.Core.Serialization;
using NatsROS.Dashboard.Security; // 引入登录框所在的命名空间
using Application = System.Windows.Application;

namespace NatsROS.Dashboard
{
    public partial class App : Application
    {
        public static IServiceProvider ServiceProvider { get; private set; } = null!;

        //// 用于保存我们在后台拉起的进程引用
        //private Process? _natsProcess;
        //private Process? _containerProcess;

        protected override void OnStartup(StartupEventArgs e)
        {
            // ==========================================
            // 【核心修复】：挂起 WPF 的自动退出机制！
            // 防止登录框关闭的瞬间，把整个程序连带杀掉
            // ==========================================
            this.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            //base.OnStartup(e);

            // 1. 静默拉起底层网络和母体 (充当启动器)
            //StartBackgroundServices();

            // 2. 等待 1 秒，确保后台服务端口开启
            //System.Threading.Thread.Sleep(1000);

            base.OnStartup(e);

            // 3. DI 与 NATS 客户端注册
            var services = new ServiceCollection();
            services.AddSingleton<INatsClient>(_ =>
            {
                var options = NatsOpts.Default with { SerializerRegistry = new NatsRosSerializerRegistry() };
                var client = new NatsClient(options);
                client.ConnectAsync().GetAwaiter().GetResult();
                return client;
            });
            ServiceProvider = services.BuildServiceProvider();

            // 4. 强行切出登录框！
            var nats = ServiceProvider.GetRequiredService<INatsClient>();
            var loginWnd = new LoginWindow(nats);

            // ShowDialog 阻塞当前线程，直到登录窗口关闭
            if (loginWnd.ShowDialog() == true)
            {
                // 登录成功，放行主程序！
                var mainWindow = new MainWindow();

                // 【核心修复】：告诉 WPF，这才是我们真正的主窗口！
                this.MainWindow = mainWindow;

                // 恢复默认行为：当主窗口关掉时，程序退出
                this.ShutdownMode = ShutdownMode.OnMainWindowClose;

                mainWindow.Show();
            }
            else
            {
                // 如果用户点 X 关闭了登录框，主动终止程序
                Application.Current.Shutdown();
            }
        }

        //private void StartBackgroundServices()
        //{
        //    string baseDir = AppDomain.CurrentDomain.BaseDirectory;

        //    // A. 静默拉起 nats-server
        //    var natsExe = Path.Combine(baseDir, "nats-server.exe");
        //    if (File.Exists(natsExe) && Process.GetProcessesByName("nats-server").Length == 0)
        //    {
        //        _natsProcess = new Process
        //        {
        //            StartInfo = new ProcessStartInfo { FileName = natsExe, Arguments = "-js", UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }
        //        };
        //        _natsProcess.Start();
        //    }

        //    // B. 静默拉起 NatsROS.Container
        //    var containerExe = Path.Combine(baseDir, "NatsROS.Container.exe");
        //    if (File.Exists(containerExe) && Process.GetProcessesByName("NatsROS.Container").Length == 0)
        //    {
        //        _containerProcess = new Process
        //        {
        //            StartInfo = new ProcessStartInfo { FileName = containerExe, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }
        //        };
        //        _containerProcess.Start();
        //    }
        //}

        protected override async void OnExit(ExitEventArgs e)
        {
            // 优雅关闭 NATS
            var nats = ServiceProvider?.GetService<INatsClient>();
            if (nats != null) await nats.DisposeAsync();

            //// 强杀我们在后台拉起的影子进程
            //try
            //{
            //    if (_containerProcess != null && !_containerProcess.HasExited) _containerProcess.Kill();
            //    if (_natsProcess != null && !_natsProcess.HasExited) _natsProcess.Kill();
            //}
            //catch
            //{ 
            //}

            base.OnExit(e);
        }
    }
}