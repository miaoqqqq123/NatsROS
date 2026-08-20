using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace NatsROS.Launcher
{
    public class AppConfig
    { 
        public string SystemName { get; set; } = "NatsROS"; 
        public string LastProjectName { get; set; } = ""; 
        public string UiMode { get; set; } = "Dashboard";

        public bool DebugMode { get; set; } = true;
    }

    public partial class MainWindow : DevExpress.Xpf.Core.ThemedWindow
    {
        private AppConfig _config = new();
        private readonly List<Process> _daemons = new();

        public MainWindow()
        {
            // 锁定主题，保证视觉效果的一致性
            DevExpress.Xpf.Core.ApplicationThemeHelper.ApplicationThemeName = "Win11Dark";
            InitializeComponent();
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await BootSequenceAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"系统启动遭遇致命错误:\n{ex.Message}", "Kernel Panic", MessageBoxButton.OK, MessageBoxImage.Error);
                Application.Current.Shutdown();
            }
        }

        /// <summary>
        /// 启动沙盒
        /// </summary>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        private async Task BootSequenceAsync()
        {
            // 1. 获取部署沙盒根目录 (因为 Launcher.exe 运行在 Bin 目录下，所以要退一级)
            string rootDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
            string binDir = AppDomain.CurrentDomain.BaseDirectory;

            // 2. 读取开机配置
            string configPath = Path.Combine(binDir, "app_config.json");
            if (File.Exists(configPath)) _config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(configPath)) ?? new();

            TxtSysName.Text = _config.SystemName;
            TxtStatus.Text = string.IsNullOrEmpty(_config.LastProjectName) ? "空载启动模式" : $"加载工程: {_config.LastProjectName}";

            // ==========================================
            // 步骤一：工程环境恢复 (解压 .natsros 覆盖工作区)
            // ==========================================
            UpdateStatus(20, "[1/4] 正在恢复现场工程资产...");
            await Task.Delay(500); // UI 缓冲

            string wsDir = Path.Combine(rootDir, "CurrentWorkspace");
            if (!Directory.Exists(wsDir)) Directory.CreateDirectory(wsDir);

            if (!string.IsNullOrEmpty(_config.LastProjectName))
            {
                string projZip = Path.Combine(rootDir, "Projects", $"{_config.LastProjectName}.natsros");
                if (File.Exists(projZip))
                {
                    // 清空旧工作区
                    var di = new DirectoryInfo(wsDir);
                    foreach (var file in di.GetFiles()) file.Delete();
                    foreach (var dir in di.GetDirectories()) dir.Delete(true);

                    // 解压新工程
                    await Task.Run(() => ZipFile.ExtractToDirectory(projZip, wsDir, true));
                }
            }

            // ==========================================
            // 步骤二：拉起 NATS 通信总线
            // ==========================================
            UpdateStatus(50, "[2/4] 启动 NATS 毫秒级总线...");
            string natsPath = Path.Combine(binDir, "nats-server.exe");

            // 【强化防呆】：如果没有 NATS，整个系统根本没法玩，直接抛出致命异常！
            if (!File.Exists(natsPath))
            {
                throw new FileNotFoundException($"找不到核心通信总线！\n请确保 nats-server.exe 文件存在于 Bin 目录下。\n路径: {natsPath}");
            }

            // StartDaemon 现在会返回是否是“新启动”的
            bool isNatsNewStarted = StartDaemon(natsPath, "-js", _config.DebugMode);
            if (isNatsNewStarted)
            {
                // 如果是刚启动的，必须等它 1.5 秒完成端口监听，否则后面的程序连不上会崩
                await Task.Delay(1500);
            }
            else
            {
                // 如果检测到后台已经有 nats-server 在跑了，直接秒过
                LoggerOrDebug("NATS 总线已在后台运行中...");
            }
            await Task.Delay(1000); // 必须等 NATS 端口监听成功

            // ==========================================
            // 步骤三：拉起 NatsROS 微服务母体
            // ==========================================
            UpdateStatus(80, "[3/4] 唤醒 NatsROS 核心中枢与管家...");
            string containerPath = Path.Combine(binDir, "NatsROS.Container.exe");
            if (!File.Exists(containerPath)) throw new FileNotFoundException($"找不到微服务母体程序: {containerPath}");

            StartDaemon(containerPath, "", _config.DebugMode);
            await Task.Delay(1500); // 必须等待 Container 把各大管家全都初始化完毕！

            // ==========================================
            // 步骤四：拉起前端 UI (并移交生死控制权)
            // ==========================================
            UpdateStatus(100, "[4/4] 启动用户交互终端...");
            string uiExeName = _config.UiMode;
            if (uiExeName.Equals("HMI", StringComparison.OrdinalIgnoreCase))
                uiExeName = "NatsROS.Hmi.exe";
            else if (uiExeName.Equals("Dashboard", StringComparison.OrdinalIgnoreCase))
                uiExeName = "NatsROS.Dashboard.exe"; 

            string uiPath = Path.Combine(binDir, uiExeName);

            if (File.Exists(uiPath))
            {
                var uiProcess = new Process { StartInfo = new ProcessStartInfo { FileName = uiPath, WorkingDirectory = binDir } };
                uiProcess.EnableRaisingEvents = true;

                // 【核心生命周期拦截】：UI 一旦关闭，整个系统全部销毁！
                uiProcess.Exited += (s, args) =>
                {
                    KillAllDaemons();
                    Dispatcher.Invoke(() => Application.Current.Shutdown());
                };
                uiProcess.Start();
            }
            else throw new Exception($"找不到 UI 程序: {uiPath}");

            // 隐藏启动器，在后台静默守护
            this.Hide();
        }

        /// <summary>
        /// 更新状态
        /// </summary>
        /// <param name="progress"></param>
        /// <param name="text"></param>
        private void UpdateStatus(double progress, string text)
        {
            PbBoot.Value = progress;
            TxtStatus.Text = text;
        }

        /// <summary>
        /// 强化版：启动进程并返回是否真正执行了启动动作
        /// </summary>
        private bool StartDaemon(string exePath, string args, bool showConsole)
        {
            string processName = Path.GetFileNameWithoutExtension(exePath);

            // 检查是否已经在运行
            if (Process.GetProcessesByName(processName).Length > 0)
            {
                return false; // 已经在跑了，不重复拉起
            }

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = args,
                WorkingDirectory = Path.GetDirectoryName(exePath),

                //如果是 Debug 模式，开启独立窗口显示输出！
                UseShellExecute = showConsole,
                CreateNoWindow = !showConsole,
                WindowStyle = showConsole ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden
            };

            var p = Process.Start(psi);
            if (p != null) _daemons.Add(p); // 记入生死簿

            return true;
        }

        private void LoggerOrDebug(string msg)
        {
            System.Diagnostics.Debug.WriteLine(msg);
        }

        /// <summary>
        /// 杀死进程
        /// </summary>
        private void KillAllDaemons()
        {
            foreach (var p in _daemons)
            {
                try { if (!p.HasExited) p.Kill(true); } catch { }
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            KillAllDaemons();
        }
    }
}