using DevExpress.Xpf.Bars;
using DevExpress.Xpf.Core;
using DevExpress.Xpf.Core.Native;
using DevExpress.Xpf.Docking;
using DevExpress.Xpf.Editors.Settings;
using DevExpress.Xpf.Ribbon;
using NATS.Client.Core;
using NATS.Net;
using NatsROS.Core.Serialization;
using NatsROS.Core.SystemMessages;
using NatsROS.Dashboard.Infrastructure;
using NatsROS.Dashboard.Plugins.BehaviorTree;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;
using Size = System.Windows.Size;

namespace NatsROS.Dashboard
{
    public partial class MainWindow : ThemedWindow
    {
        // 缓存发现的插件列表
        private readonly List<IDashboardPlugin> _discoveredPlugins = new();

        // 日志批量渲染引擎的核心字段
        private readonly CancellationTokenSource _sysCts = new();
        private readonly ConcurrentQueue<(string Text, Color Color)> _logQueue = new();
        private readonly System.Windows.Threading.DispatcherTimer _logRenderTimer = new();
        private INatsClient? _nats;

        public MainWindow()
        {
            // 【新增】：在 WPF 渲染 UI 之前，强制将全局默认主题锁定为 Win11 Dark！
            DevExpress.Xpf.Core.ApplicationThemeHelper.ApplicationThemeName = "Win11Dark";

            InitializeComponent();

            // 配置日志渲染定时器
            _logRenderTimer.Interval = TimeSpan.FromMilliseconds(100);
            _logRenderTimer.Tick += LogRenderTimer_Tick;

            // 【新增拆弹代码】：监听 MDI 容器中任何面板被关闭的事件
            DockManager.DockItemClosed += DockManager_DockItemClosed;
        }

        // 【新增】：当面板关闭时，安全释放内部插件的后台资源！
        private void DockManager_DockItemClosed(object sender, DevExpress.Xpf.Docking.Base.DockItemClosedEventArgs e)
        {
            if (e.Item is DevExpress.Xpf.Docking.DocumentPanel panel)
            {
                // 如果里面的 UserControl 实现了 IDisposable，就坚决调用它！
                if (panel.Content is IDisposable disposablePlugin)
                {
                    disposablePlugin.Dispose();
                    AppendLog("SYSTEM", $"🧹 插件资源已安全释放 ({panel.Caption})", Colors.Gray);
                }
            }
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // 1. 获取全局 NATS 客户端
                var options = NatsOpts.Default with { SerializerRegistry = new NatsRosSerializerRegistry() };
                _nats = new NatsClient(options);
                await _nats.ConnectAsync();

                if (_nats != null)
                {

                    var imageConverter = new System.Windows.Media.ImageSourceConverter();
                    // 点亮状态栏！
                    StatusConnection.Content = "已连接到 NATS 核心网络 (127.0.0.1:4222)";
                    try
                    {
                        string uriStr = "pack://application:,,,/DevExpress.Images.v23.2;component/SvgImages/Icon Builder/Security_Security.svg";
                        Uri uri = new System.Uri(uriStr);
                        StatusConnection.Glyph = WpfSvgRenderer.CreateImageSource(uri);
                    }
                    catch (Exception ex)
                    {
                        AppendLog("SYSTEM", $"状态栏出错：{ex.Message}", Colors.Red);
                    }


                    // 启动系统级后台监听
                    _logRenderTimer.Start();
                    _ = ListenToRosOutAsync(_sysCts.Token);
                    AppendLog("SYSTEM", "✅ NatsROS 监控大屏启动成功，日志拦截引擎已就绪。", Colors.LimeGreen);
                }

                // 2. 扫盘加载插件
                ScanAndLoadPlugins();
                BuildRibbonMenu();

                StatusPluginCount.Content = $"成功挂载了 {_discoveredPlugins.Count} 个业务插件";

                //// 初始化主题选择器
                //var themes = DevExpress.Xpf.Core.Theme.Themes.Select(t => t.Name).ToList();

                //// 使用标准的 C# 类型强转，而不是 XAML 别名
                //((ComboBoxEditSettings)ThemeSelector.EditSettings).ItemsSource = themes;

                //// 使用 ApplicationThemeHelper 来获取当前的全局主题
                //ThemeSelector.EditValue = DevExpress.Xpf.Core.ApplicationThemeHelper.ApplicationThemeName;

                //// 【修复】：强制指定参数类型为 RoutedEventArgs 并没有 NewValue
                //// 为了安全稳妥地拿到最新选中的皮肤名字，我们直接去读控件本身的 EditValue 属性！
                //ThemeSelector.EditValueChanged += (s, ev) =>
                //{
                //    if (ThemeSelector.EditValue is string themeName)
                //    {
                //        // 使用 ApplicationThemeHelper 瞬间切换整个软件的所有窗口的主题！
                //        DevExpress.Xpf.Core.ApplicationThemeHelper.ApplicationThemeName = themeName;
                //    }
                //};


            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _sysCts.Cancel();
            _logRenderTimer.Stop();
        }

        // ==========================================
        // 恢复：极速日志截获与渲染引擎
        // ==========================================
        private async Task ListenToRosOutAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var msg in _nats!.SubscribeAsync<LogMsg>("rosout", cancellationToken: ct))
                {
                    if (msg.Data != null)
                    {
                        var log = msg.Data;
                        Color color = log.Level switch
                        {
                            10 => Colors.DarkGray,
                            20 => Colors.White,
                            30 => Colors.Yellow,
                            _ => Colors.Red
                        };

                        var timeStr = DateTimeOffset.FromUnixTimeMilliseconds(log.Stamp).ToLocalTime().ToString("HH:mm:ss.fff");
                        string levelStr = log.Level switch
                        {
                            10 => "DEBUG",
                            20 => "INFO",
                            30 => "WARN",
                            40 => "ERROR",
                            50 => "FATAL",
                            _ => "UNK"
                        };

                        string formattedMsg = $"[{timeStr}] [{levelStr.PadRight(5)}] [{log.Name.PadRight(35)}] {log.Msg}";
                        _logQueue.Enqueue((formattedMsg, color)); // 无锁极速压入队列
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        private void AppendLog(string source, string text, Color color)
        {
            _logQueue.Enqueue((text, color));
        }

        private void LogRenderTimer_Tick(object? sender, EventArgs e)
        {
            if (_logQueue.IsEmpty) return;

            RtbLogs.BeginChange();
            int count = 0;
            while (count < 500 && _logQueue.TryDequeue(out var logItem))
            {
                var p = new Paragraph(new Run(logItem.Text) { Foreground = new SolidColorBrush(logItem.Color) }) { Margin = new Thickness(0) };
                LogDocument.Blocks.Add(p);
                count++;
            }
            while (LogDocument.Blocks.Count > 1000) LogDocument.Blocks.Remove(LogDocument.Blocks.FirstBlock);
            RtbLogs.EndChange();
            RtbLogs.ScrollToEnd();
        }


        // ==========================================
        // 1. OpenTAP 核心思想：动态扫盘发现插件
        // ==========================================
        private void ScanAndLoadPlugins()
        {
            string binPath = AppDomain.CurrentDomain.BaseDirectory;
            string deployRoot = Path.GetFullPath(Path.Combine(binPath, "..")); // 退回 Deploy 根目录

            // 寻找包含插件和节点的文件夹
            string[] searchDirs = {
                binPath,
                Path.Combine(deployRoot, "Organs"),
                Path.Combine(deployRoot, "Plugins")
            };

            // 1. 强制将所有相关的 DLL 吸入内存！
            foreach (var dir in searchDirs)
            {
                if (!Directory.Exists(dir)) continue;

                var dllFiles = Directory.GetFiles(dir, "*.dll", SearchOption.AllDirectories)
                    .Where(f => f.Contains("NatsROS") || f.Contains("ScrewMachine") || f.Contains("Hexiv"));

                foreach (var dllPath in dllFiles)
                {
                    try { Assembly.LoadFrom(dllPath); }
                    catch (Exception ex) { AppendLog("SYSTEM", $"⚠️ 插件 {Path.GetFileName(dllPath)} 载入失败: {ex.Message}", Colors.Red); }
                }
            }

            // 2. 此时 AppDomain 已经饱满了！获取当前 AppDomain 里所有的 Assembly
            var allAssemblies = AppDomain.CurrentDomain.GetAssemblies().ToList();

            // 【超级保底】：强行把当前在运行的 Dashboard 程序集自己也塞进去！
            var currentAsm = Assembly.GetExecutingAssembly();
            if (!allAssemblies.Contains(currentAsm))
            {
                allAssemblies.Add(currentAsm);
            }

            // 开始地毯式搜索
            foreach (var asm in allAssemblies)
            {
                try
                {
                    // 找出所有非抽象、且实现了 IDashboardPlugin 接口的类
                    var pluginTypes = asm.GetTypes()
                        .Where(t => typeof(IDashboardPlugin).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);
                    var pluginTypes1 = asm.GetTypes();
                    foreach (var type in pluginTypes)
                    {
                        try
                        {
                            if (Activator.CreateInstance(type) is IDashboardPlugin plugin)
                            {
                                _discoveredPlugins.Add(plugin);
                                // 把发现的插件打印到我们的全局系统日志里！
                                AppendLog("SYSTEM", $"🧩 成功发现插件: [{plugin.DisplayName}] 来自 {asm.GetName().Name}", Colors.Cyan);
                            }
                        }
                        catch (Exception ex)
                        {
                            AppendLog("SYSTEM", $"⚠️ 插件 {type.Name} 实例化失败: {ex.Message}", Colors.Yellow);
                        }
                    }
                }
                catch (ReflectionTypeLoadException rtlEx)
                {
                    // 如果因为缺少依赖导致整个 DLL 无法被反射，在这里抓出来！
                    AppendLog("SYSTEM", $"⚠️ 扫描 {asm.GetName().Name} 失败，可能是缺少依赖包。", Colors.Yellow);
                }
                catch 
                {
                    /* 忽略反射异常的库 */
                }
            }
        }

        // ==========================================
        // 2. 根据发现的插件，动态构建 Ribbon 菜单
        // ==========================================
        private void BuildRibbonMenu()
        {
            // 按 Category (如"诊断工具") 对插件进行分组
            var groupedPlugins = _discoveredPlugins.GroupBy(p => p.RibbonCategory);

            foreach (var group in groupedPlugins)
            {
                // 创建一个 Ribbon 选项卡
                var page = new RibbonPage { Caption = group.Key };
                var pageGroup = new RibbonPageGroup { Caption = "可用工具" };
                var imageConverter = new System.Windows.Media.ImageSourceConverter();

                foreach (var plugin in group)
                {
                    // 生成 Ribbon 按钮
                    var btn = new BarButtonItem
                    {
                        Content = plugin.DisplayName,
                        RibbonStyle = RibbonItemStyles.Large
                    };

                    try
                    {
                        // 尝试加载 DevExpress 库里的内置图标
                        // 请确保下面这行字符串里的 v23.2 与你实际引用的 DevExpress.Xpf.Core.vXX.X 匹配！
                        string uriStr = $"pack://application:,,,/DevExpress.Xpf.Core.v23.2;component/{plugin.GlyphPath}";
                        Uri uri = new System.Uri(uriStr);
                        btn.LargeGlyph = WpfSvgRenderer.CreateImageSource(uri);
                    }
                    catch
                    {
                        // 如果因为版本号写错或者找不到图标导致异常，就忽略，不影响按钮生成
                    }

                    // 【核心事件】：点击按钮时，呼出该插件的 MDI 窗口
                    btn.ItemClick += (s, e) => OpenPluginInMdi(plugin);

                    pageGroup.ItemLinks.Add(btn);
                }

                page.Groups.Add(pageGroup);
                RibbonCategory.Pages.Add(page);
            }
        }

        // ==========================================
        // 3. 在 MDI 容器中渲染插件界面
        // ==========================================
        private void OpenPluginInMdi(IDashboardPlugin plugin)
        {
            try
            {
                // 调用插件的契约方法，由全局 DI 容器提供依赖，生成真正的 UI 控件
                var view = plugin.CreateView(App.ServiceProvider);

                // 创建一个可停靠的选项卡文档
                var panel = new DocumentPanel
                {
                    Caption = plugin.DisplayName,
                    Content = view,
                    MDISize = new Size(800, 600)
                };

                // 添加到主容器并激活
                MdiContainer.Add(panel);
                DockManager.Activate(panel);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载插件 [{plugin.DisplayName}] 失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // 清空日志富文本框
        private void BtnClearLog_ItemClick(object sender, DevExpress.Xpf.Bars.ItemClickEventArgs e)
        {
            RtbLogs.BeginChange();
            LogDocument.Blocks.Clear();
            RtbLogs.EndChange();
            AppendLog("SYSTEM", "🗑️ 日志已清空。", Colors.Gray);
        }

        // 恢复默认的 Dock 布局 (如果有拖乱的面板，让它们复位)
        private void BtnRestoreLayout_ItemClick(object sender, DevExpress.Xpf.Bars.ItemClickEventArgs e)
        {
            // 对于复杂的停靠布局，最极端的恢复方式就是清空再加，这里我们简单处理为激活欢迎页
            if (MdiContainer.Items.Count > 0)
            {
                DockManager.Activate(MdiContainer.Items[0]);
            }
            MessageBox.Show("布局已恢复默认。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
