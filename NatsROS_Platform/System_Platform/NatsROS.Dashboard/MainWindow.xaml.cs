using DevExpress.Mvvm;
using DevExpress.Xpf.Bars;
using DevExpress.Xpf.Core;
using DevExpress.Xpf.Core.Native;
using DevExpress.Xpf.Docking;
using DevExpress.Xpf.Ribbon;
using NATS.Client.Core;
using NATS.Net;
using NatsROS.Core.Serialization;
using NatsROS.Core.SystemMessages;
using NatsROS.Core.UI;
using NatsROS.Dashboard.Localization;
using NatsROS.Dashboard.Security;
using NatsROS.Messages.RMS;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using NatsROS.Dashboard.UI.Dialogs;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;
using Size = System.Windows.Size;
using SplashScreen = NatsROS.Dashboard.UI.ThemedSplashScreen.SplashScreen;

namespace NatsROS.Dashboard
{
    /// <summary>
    /// 用于解析 ui_manifest.json 的模型
    /// </summary>
    public class UiManifest { public List<string> EnabledPlugins { get; set; } = new(); }
    /// <summary>
    ///  应用程序配置模型，用于记录最后加载的工程名，方便下次启动时自动恢复
    /// </summary>
    public class AppConfig { public string LastProjectName { get; set; } = "DefaultProject"; }

    /// <summary>
    /// 用于序列化 ui_state.json 的内部模型
    /// </summary>
    public class PanelStateInfo
    {
        public string PanelId { get; set; } = "";
        public string PluginTypeName { get; set; } = "";
        public string CustomState { get; set; } = "";
    }


    public partial class MainWindow : ThemedWindow
    {
        private AppConfig _appConfig = new(); // 记录当前工程名
        // 缓存发现的插件列表
        private readonly List<IDashboardPlugin> _discoveredPlugins = new();
        // 专门记录加载失败或者崩溃的 DLL 信息 (名字, 错误原因, 来源)
        private readonly List<(string Name, string Error, string Source)> _failedDlls = new();

        // 日志批量渲染引擎的核心字段
        private readonly CancellationTokenSource _sysCts = new();
        private readonly ConcurrentQueue<(string Text, Color Color)> _logQueue = new();
        private readonly System.Windows.Threading.DispatcherTimer _logRenderTimer = new();
        private INatsClient? _nats;

        // 【新增】：用于保存“出厂默认布局”的内存快照
        private readonly System.IO.MemoryStream _defaultLayoutStream = new System.IO.MemoryStream();

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

            // 【核心魔法】：在 InitializeComponent 刚画完原汁原味的 XAML 时，立刻拍照存入内存！
            DockManager.SaveLayoutToStream(_defaultLayoutStream);
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

                // 【核心修复】：清空引用，彻底释放内存，给下次“复活”腾出空壳！
                panel.Content = null;
            }
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            //this.Visibility = System.Windows.Visibility.Collapsed;
            // 呼出 DevExpress 极具质感的加载大屏 (在独立线程运行)
            //初始化启动页面
            var dXSplashScreenViewModel = new DXSplashScreenViewModel
            {
                Title = "NatsROS",
                Subtitle = "Enterprise Distributed OS",
                Status = "正在初始化 NatsROS 大屏控制中心...",
                Copyright = "Copyright © Hexiv Automation",
                IsIndeterminate = true // 开启无限滚动进度条
            };

            var splash = DevExpress.Xpf.Core.SplashScreenManager.Create(() => new SplashScreen(), dXSplashScreenViewModel);
            splash.Show();

            await Task.Delay(100); // 给大屏一点时间渲染出来

            try
            {
                // 1. 获取全局 NATS 客户端
                splash.ViewModel.Status = "正在连接 NATS 核心网络...";
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

                // 2. 开机去查一下当前激活的配方是谁 (同步底层的记忆)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var getActiveClient = new NatsROS.Core.Communication.RosServiceClient<GetActiveRecipeReq, GetActiveRecipeRes>(_nats, "rms.get_active");
                        var res = await getActiveClient.CallAsync(new GetActiveRecipeReq(), TimeSpan.FromSeconds(2));
                        if (res != null && res.ActiveRecipe != null)
                        {
                            Dispatcher.Invoke(() => StatusActiveRecipe.Content = $"当前配方: {res.ActiveRecipe.RecipeName} [{res.ActiveRecipe.Version}]");
                        }
                    }
                    catch { /* 如果 RMS 还没启动，静默忽略 */ }
                });

                // 3. 永远监听全网的配方热切换广播！
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var activeSub = new NatsROS.Core.Communication.RosSubscriber<RecipeActivatedEvent>(_nats, "rms.event.recipe_activated", NatsROS.Core.Communication.RosQosProfile.SensorData);
                        await foreach (var msg in activeSub.SubscribeAsync(_sysCts.Token))
                        {
                            if (msg != null)
                            {
                                Dispatcher.Invoke(() => StatusActiveRecipe.Content = $"当前配方: {msg.Recipe.RecipeName} [{msg.Recipe.Version}]");
                                AppendLog("SYSTEM", $"🎯 产线配方已热切换为: {msg.Recipe.RecipeName}", Colors.Cyan);
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                });

                // 4.监听全网的工程热切换广播！
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // 去读一下当前的工程名
                        string cfgPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_config.json");
                        if (File.Exists(cfgPath)) _appConfig = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(cfgPath)) ?? new();

                        var workspaceSub = new NatsROS.Core.Communication.RosSubscriber<WorkspaceChangedEvent>(_nats, "sys.workspace.changed", NatsROS.Core.Communication.RosQosProfile.Reliable);

                        // 【核心魔法】：使用 unique consumer name 解决冲突
                        string consumerName = $"DASHBOARD_WS_SUB_{Guid.NewGuid().ToString("N").Substring(0, 8)}";

                        await foreach (var msg in workspaceSub.SubscribeAsync(_sysCts.Token))
                        {
                            if (msg != null)
                            {
                                _appConfig.LastProjectName = msg.NewProjectName;

                                Dispatcher.Invoke(() =>
                                {
                                    // 1. 根据新工程的 manifest 重建菜单！
                                    BuildRibbonMenu();
                                    // 2. 加载新工程的专属布局！
                                    LoadUserLayout();
                                    AppendLog("SYSTEM", $"📦 产线工程已切换为: {_appConfig.LastProjectName}，界面与功能已热更新", Colors.Yellow);
                                });
                            }
                        }
                    }
                    catch { }
                });

                // 5. 扫盘加载插件
                // 把插件扫描放进 Task.Run，绝不阻塞 UI 线程！

                splash.ViewModel.Status = "正在扫描并加载动态业务插件...";
                await Task.Run(() => ScanAndLoadPlugins());

                splash.ViewModel.Status = "正在构建智能菜单与恢复用户布局...";
                BuildRibbonMenu();
                StatusPluginCount.Content = $"成功挂载了 {_discoveredPlugins.Count} 个业务插件";

                // 6. 根据当前登录的账号，加载专属的停靠布局！
                LoadUserLayout();

                // 7. 挂载 IAM 监听，并立刻对当前登录的账号执行一次 UI 评估！
                SetupSecurityModeListener();
                ApplySecurityMode();

                await Task.Delay(3000); // 给大屏一点时间渲染出来

            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
            finally
            {
                // 【核心收尾】：无论成功失败，关闭加载动画，展示主界面！
                splash.Close();
                this.Visibility = System.Windows.Visibility.Visible;
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _sysCts.Cancel();
            _logRenderTimer.Stop();

            // 【核心升级】：程序退出时，自动保存该用户的布局偏好
            SaveUserLayout();
        }


        /// <summary>
        /// 布局持久化引擎 (Layout Persistence)
        /// </summary>
        /// <returns></returns>
        private string GetUserLayoutFilePath()
        {
            string layoutDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Layouts");
            if (!Directory.Exists(layoutDir)) Directory.CreateDirectory(layoutDir);

            string username = GlobalSecurityContext.CurrentUser?.Username ?? "default";
            string project = string.IsNullOrEmpty(_appConfig.LastProjectName) ? "DefaultProject" : _appConfig.LastProjectName;

            // 隔离机制的核心 -> admin_AppleV1_layout.xml
            return Path.Combine(layoutDir, $"{username}_{project}_layout.xml");
        }

        /// <summary>
        /// 获取伴生状态文件路径 (_uistate.json)
        /// </summary>
        /// <returns></returns>
        private string GetUiStateFilePath()
        {
            string layoutDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Layouts");
            if (!Directory.Exists(layoutDir)) Directory.CreateDirectory(layoutDir);

            string username = Security.GlobalSecurityContext.CurrentUser?.Username ?? "default";
            string project = string.IsNullOrEmpty(_appConfig.LastProjectName) ? "DefaultProject" : _appConfig.LastProjectName;

            // 命名规则: admin_AppleV1_uistate.json
            return Path.Combine(layoutDir, $"{username}_{project}_uistate.json");
        }

        /// <summary>
        /// 加载用户专属布局 (Layout Persistence)
        /// </summary>
        private void LoadUserLayout()
        {
            string layoutPath = GetUserLayoutFilePath();
            string statePath = GetUiStateFilePath();

            // 【核心防串扰】：如果从 A 工程切换到 B 工程，先把 A 工程打开的所有业务面板全部强行关闭！
            var activePanels = DockManager.GetItems().OfType<DevExpress.Xpf.Docking.DocumentPanel>().Where(p => p.Name.StartsWith("Panel_")).ToList();
            foreach (var p in activePanels) p.Closed = true;

            if (File.Exists(layoutPath))
            {
                try
                {
                    // 1. 【绝杀修正】：提前解析 XML 造空壳！
                    // DevExpress 不会自动实例化代码生成的面板，我们必须先造好空壳等它归位
                    var xmlDoc = System.Xml.Linq.XDocument.Load(layoutPath);

                    // 从 XML 中找出所有名字以 "Panel_" 开头的面板
                    var savedPanelNames = xmlDoc.Descendants("property")
                        .Where(e => e.Attribute("name")?.Value == "Name" && e.Value.StartsWith("Panel_"))
                        .Select(e => e.Value)
                        .ToList();

                    foreach (var panelName in savedPanelNames)
                    {
                        // 如果当前容器里没有这个面板，赶紧建一个空的垫底
                        if (DockManager.GetItem(panelName) == null)
                        {
                            var shell = new DevExpress.Xpf.Docking.DocumentPanel
                            {
                                Name = panelName,
                                BindableName = panelName
                            };
                            // 随便塞进主容器里，等会 RestoreLayoutFromXml 时，DX 引擎会自动把它拎出来放到该去的分屏组里！
                            MdiContainer.Add(shell);
                        }
                    }

                    // 2. 恢复面板位置尺寸偏好 (此时 DX 引擎会把刚才建好的空壳，完美移动到拖拽过的位置)
                    DockManager.RestoreLayoutFromXml(layoutPath);

                    // 3. 读取伴生 JSON 状态数据
                    List<PanelStateInfo>? savedStates = null;
                    if (File.Exists(statePath))
                    {
                        savedStates = System.Text.Json.JsonSerializer.Deserialize<List<PanelStateInfo>>(File.ReadAllText(statePath));
                    }

                    // 4. 扫描所有空壳，注入真实的 UI
                    var docPanels = DockManager.GetItems().OfType<DevExpress.Xpf.Docking.DocumentPanel>();
                    foreach (var panel in docPanels)
                    {
                        string panelId = panel.Name;

                        // 如果它是一个动态插件面板，且【里面没有内容】
                        if (!string.IsNullOrEmpty(panelId) &&
                            panelId.StartsWith("Panel_") &&
                            !panel.IsClosed &&
                            panel.Content == null)
                        {
                            // 尝试从 JSON 中找回它当年的类型和数据
                            var stateInfo = savedStates?.FirstOrDefault(s => s.PanelId == panelId);
                            string pluginTypeName = stateInfo?.PluginTypeName ?? panelId.Substring(6).Split('_')[0];

                            // 在我们已经发现的插件字典中找到它，并实例化 UI 塞进去！
                            var plugin = _discoveredPlugins.FirstOrDefault(p => p.GetType().Name == pluginTypeName);
                            if (plugin != null)
                            {
                                var view = plugin.CreateView(App.ServiceProvider);
                                // 如果它支持状态恢复，就把当年存的参数还给它！
                                if (view is IStatefulView statefulView && stateInfo != null && !string.IsNullOrEmpty(stateInfo.CustomState))
                                {
                                    statefulView.RestoreState(stateInfo.CustomState);
                                }

                                panel.Content = view;
                                panel.Caption = plugin.DisplayName;
                                panel.BindableName = panelId;
                            }
                        }
                    }

                    AppendLog("SYSTEM", $"🌟 欢迎回来，{Security.GlobalSecurityContext.CurrentUser?.DisplayName}。专属布局及记忆状态已恢复。", Colors.Cyan);
                }
                catch (Exception ex)
                {
                    AppendLog("SYSTEM", $"⚠️ 布局恢复失败: {ex.Message}", Colors.Yellow);
                }
            }

            // 【保底机制】：如果恢复后，除了欢迎大屏没有任何业务面板，强行亮出欢迎大屏！
            var welcomePanel = DockManager.GetItem("PanelWelcome") as DevExpress.Xpf.Docking.DocumentPanel;
            if (welcomePanel != null && !welcomePanel.IsVisible)
            {
                bool hasOtherActivePanels = DockManager.GetItems().OfType<DevExpress.Xpf.Docking.DocumentPanel>().Any(p => p.IsVisible && p.Name != "PanelWelcome");
                if (!hasOtherActivePanels)
                {
                    welcomePanel.Visibility = Visibility.Visible;
                    DockManager.Activate(welcomePanel);
                }
            }
        }

        private void SaveUserLayout()
        {
            try
            {
                // 1. 保存 DevExpress 的外壳尺寸和位置
                DockManager.SaveLayoutToXml(GetUserLayoutFilePath());

                // 2. 【核心新增】：伴生状态收集
                var stateList = new List<PanelStateInfo>();
                var docPanels = DockManager.GetItems().OfType<DevExpress.Xpf.Docking.DocumentPanel>().Where(p => !p.IsClosed && p.Name.StartsWith("Panel_"));

                foreach (var panel in docPanels)
                {
                    // 提取类型名 (如果是多开，名字可能是 "Panel_BehaviorTreePlugin_a1b2c3d4")
                    string panelId = panel.Name;
                    string pluginTypeName = panelId.Substring(6);
                    if (pluginTypeName.Contains("_"))
                    {
                        pluginTypeName = pluginTypeName.Split('_')[0]; // 切掉 GUID 后缀
                    }

                    // 如果这个插件实现了状态接口，就向它索要数据！
                    string customState = "";
                    if (panel.Content is IStatefulView statefulView)
                    {
                        customState = statefulView.SaveState();
                    }

                    stateList.Add(new PanelStateInfo
                    {
                        PanelId = panelId,
                        PluginTypeName = pluginTypeName,
                        CustomState = customState
                    });
                }

                // 3. 将所有收集到的数据存入 JSON 伴生文件
                var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(GetUiStateFilePath(), System.Text.Json.JsonSerializer.Serialize(stateList, opts));
            }
            catch
            {
                /* 退出时若保存失败可静默忽略 */
            }
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
            _discoveredPlugins.Clear();
            _failedDlls.Clear(); // 清空历史报错

            string binPath = AppDomain.CurrentDomain.BaseDirectory;
            // 定位到沙盒的插件目录
            string workspacePluginsDir = Path.Combine(NatsROS.Core.Environment.WorkspaceManager.CurrentWorkspacePath, "Plugins");

            if (!Directory.Exists(workspacePluginsDir)) Directory.CreateDirectory(workspacePluginsDir);

            // 加入沙盒插件目录进行扫描
            string[] searchDirs = {
                binPath,
                workspacePluginsDir
            };

            // 1. 强制将所有相关的 DLL 吸入内存！
            foreach (var dir in searchDirs)
            {
                if (!Directory.Exists(dir)) continue;

                // 1. 改为 SearchOption.TopDirectoryOnly (只扫表面，绝对不进 runtimes 等深水区子文件夹)
                // 2. 使用 StartsWith 配合 GetFileName 精准匹配，防止误伤
                // 只要不是微软、第三方基础库的 DLL，我们全部吸入内存！
                var dllFiles = Directory.GetFiles(dir, "*.dll", SearchOption.TopDirectoryOnly)
                    .Where(f =>
                    {
                        string name = Path.GetFileName(f);
                        return !name.StartsWith("System.") &&
                               !name.StartsWith("Microsoft.") &&
                               !name.StartsWith("DevExpress.") &&
                               !name.StartsWith("NATS.") &&
                               !name.StartsWith("NLog") &&
                               !name.StartsWith("MessagePack");
                    });

                foreach (var dllPath in dllFiles)
                {
                    try
                    {
                        var asm = Assembly.LoadFrom(dllPath);

                        // 【极其严苛的防线】：强行获取该 DLL 声明的所有类型！
                        // 如果它里面有任何一个方法依赖了找不到的 DLL，这一步就会触发 ReflectionTypeLoadException！
                        var types = asm.GetTypes();
                    }
                    catch (ReflectionTypeLoadException ex)
                    {
                        // 抓住了！
                        string missingFiles = string.Join(", ", ex.LoaderExceptions.Select(e => e?.Message).Distinct());
                        _failedDlls.Add((Path.GetFileName(dllPath), $"缺少依赖: {missingFiles}", dir.Contains("CurrentWorkspace") ? "📦 当前工程" : "⚙️ 核心内置"));
                        AppendLog("SYSTEM", $"⚠️ 插件载入失败，缺少依赖: {missingFiles}", Colors.Red);
                    }
                    catch (Exception ex)
                    {
                        // 记录崩溃 DLL！
                        _failedDlls.Add((Path.GetFileName(dllPath), ex.Message, dir.Contains("CurrentWorkspace") ? "📦 当前工程" : "⚙️ 核心内置"));
                        AppendLog("SYSTEM", $"⚠️ 插件 {Path.GetFileName(dllPath)} 载入失败: {ex.Message}", Colors.Red);
                    }
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
                            // 实例化报错，同样记录下来
                            _failedDlls.Add((type.Name, $"实例化失败: {ex.Message}", "代码错误"));
                            AppendLog("SYSTEM", $"⚠️ 插件 {type.Name} 实例化失败: {ex.Message}", Colors.Yellow);
                        }
                    }
                }
                catch (ReflectionTypeLoadException rtlEx)
                {
                    // 如果因为缺少依赖报错，记录下来
                    _failedDlls.Add((asm.GetName().Name ?? "Unknown", "缺少依赖包或版本冲突", "反射错误"));
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
            // 每次构建前，保留 XAML 里写死的第 0 个【主页 (Home)】，清空动态页面
            while (RibbonCategory.Pages.Count > 1)
            {
                RibbonCategory.Pages.RemoveAt(1);
            }

            // 1. 读取沙盒里的 ui_manifest.json
            List<string>? enabledPlugins = null;
            string manifestPath = NatsROS.Core.Environment.WorkspaceManager.GetConfigPath("ui_manifest.json");

            if (File.Exists(manifestPath))
            {
                try
                {
                    var manifest = System.Text.Json.JsonSerializer.Deserialize<UiManifest>(File.ReadAllText(manifestPath));
                    enabledPlugins = manifest?.EnabledPlugins;
                }
                catch { AppendLog("SYSTEM", "⚠️ ui_manifest.json 解析失败，将加载所有可用插件。", Colors.Yellow); }
            }

            // 2. 过滤插件名单
            var pluginsToLoad = _discoveredPlugins;
            if (enabledPlugins != null && enabledPlugins.Count > 0)
            {
                pluginsToLoad = _discoveredPlugins.Where(p => enabledPlugins.Contains(p.GetType().Name)).ToList();
            }

            // ==========================================
            // 3. 【核心升维】：嵌套双重分组渲染 (Page -> Group)
            // ==========================================

            // 第一层分组：按 RibbonPage (选项卡) 聚合
            var pageGroups = pluginsToLoad.GroupBy(p => p.RibbonPage);
            foreach (var pageGroup in pageGroups)
            {
                var page = new RibbonPage { Caption = pageGroup.Key };

                // 第二层分组：在同一个选项卡内，按 RibbonGroup (功能区块) 聚合
                var innerGroups = pageGroup.GroupBy(p => p.RibbonGroup);
                foreach (var innerGroup in innerGroups)
                {
                    var pageGroupControl = new RibbonPageGroup { Caption = innerGroup.Key };

                    // 第三层：生成按钮
                    foreach (var plugin in innerGroup)
                    {
                        var btn = new BarButtonItem { Content = plugin.DisplayName, RibbonStyle = RibbonItemStyles.Large };
                        try { btn.LargeGlyph = WpfSvgRenderer.CreateImageSource(new Uri($"pack://application:,,,/DevExpress.Images.v23.2;component/{plugin.GlyphPath}")); } catch { }

                        btn.ItemClick += (s, e) => OpenPluginInMdi(plugin);
                        pageGroupControl.ItemLinks.Add(btn);
                    }
                    // 把区域块塞进选项卡
                    page.Groups.Add(pageGroupControl);
                }
                // 把选项卡塞进主菜单
                RibbonCategory.Pages.Add(page);
            }
        }

        /// <summary>
        /// 在 MDI 容器中渲染插件界面
        /// </summary>
        /// <param name="plugin"></param>
        private void OpenPluginInMdi(IDashboardPlugin plugin)
        {
            try
            {
                string uniqueName;

                // 【核心新增】：多开 vs 单例 判定
                if (plugin.AllowMultiple)
                {
                    // 多开模式：每次都生成一个带有 8 位随机后缀的名字！
                    uniqueName = "Panel_" + plugin.GetType().Name + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                }
                else
                {
                    // 单例模式：使用类名作为唯一 ID，并查重
                    uniqueName = "Panel_" + plugin.GetType().Name;

                    var existingPanel = DockManager.GetItem(uniqueName) as DevExpress.Xpf.Docking.DocumentPanel;
                    if (existingPanel != null)
                    {
                        // 【核心修复】：如果面板被用户点击 X 关闭了，它会被标记为 IsClosed
                        if (existingPanel.IsClosed || existingPanel.Content == null)
                        {
                            // 重新生成 UI 灵魂，注入这个旧壳子里！
                            existingPanel.Content = plugin.CreateView(App.ServiceProvider);

                            // 把它从“隐藏回收站”里复活！(DevExpress 会神奇地把它放回上次用户拖拽的位置！)
                            existingPanel.Closed = false;
                        }

                        DockManager.Activate(existingPanel);
                        return;
                    }
                }

                // 2. 生成真正的 UI 控件
                var view = plugin.CreateView(App.ServiceProvider);

                // 3. 【核心修复 2】：必须使用 BindableName 才能被 DevExpress 引擎序列化进 XML！
                var panel = new DevExpress.Xpf.Docking.DocumentPanel
                {
                    Name = uniqueName,              // 内存寻址用
                    BindableName = uniqueName,      // DX 序列化专用
                    Caption = plugin.DisplayName,
                    Content = view,
                    MDISize = new Size(800, 600)
                };

                // 4. 将新面板加入 DockManager (如果 XML 布局里有它的位置记录，DX 引擎会自动把它吸附过去！)
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

            // 1. 删除磁盘上的个性化偏好
            string path = GetUserLayoutFilePath();
            if (File.Exists(path)) File.Delete(path);

            // 2. 闭屏当前除了核心组件外的所有动态生成的插件面板
            var panelsToClose = DockManager.GetItems().OfType<DevExpress.Xpf.Docking.DocumentPanel>().ToList();
            foreach (var panel in panelsToClose)
            {
                // 排除内置的欢迎大屏
                if (panel.Name != "PanelWelcome")
                {
                    panel.Closed = true; // 触发销毁
                }
            }

            // 3. 【核心修复】：将内存中的布局瞬间重置为开机那一刻的出厂状态！
            // 这一步会彻底粉碎那些因为拖拽而遗留的“幽灵空白组 (Empty LayoutGroups)”
            _defaultLayoutStream.Position = 0; // 必须把游标拨回起点
            DockManager.RestoreLayoutFromStream(_defaultLayoutStream);

            // 4. 确保欢迎大屏存在并激活
            var welcomePanel = DockManager.GetItem("PanelWelcome") as DevExpress.Xpf.Docking.DocumentPanel;
            if (welcomePanel != null)
            {
                welcomePanel.IsActive = true;
                welcomePanel.Visibility = Visibility.Visible;
                DockManager.Activate(welcomePanel);
            }

            MessageBox.Show("个性化布局已清除，已完美恢复至出厂状态！", "恢复默认", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnSaveLayout_ItemClick(object sender, ItemClickEventArgs e)
        {
            SaveUserLayout();
            MessageBox.Show("当前面板布局已保存！下次登录将自动恢复此状态。", "布局持久化", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnSwitchLang_ItemClick(object sender, ItemClickEventArgs e)
        {
            // 【核心升级】：一键热切换全局多语言字典！
            LocalizationManager.ToggleLanguage();
            AppendLog("SYSTEM", "🌐 UI 语言已热切换 (UI Language Toggled)。", Colors.MediumPurple);
        }

        private async void BtnImportExternalProject_ItemClick(object sender, EventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "NatsROS 工程包 (*.natsros)|*.natsros",
                Title = "请选择外部设备工程包"
            };

            if (dlg.ShowDialog() == true)
            {
                string externalFilePath = dlg.FileName;
                string projectName = Path.GetFileNameWithoutExtension(externalFilePath);

                var confirm = MessageBox.Show($"即将载入外部工程：[{projectName}]\n这将覆盖当前沙盒中正在运行的配置！是否继续？",
                    "高危操作", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (confirm != MessageBoxResult.Yes) return;

                try
                {
                    await Task.Run(() =>
                    {
                        // 1. 清空现有的 CurrentWorkspace 沙盒
                        var di = new DirectoryInfo(NatsROS.Core.Environment.WorkspaceManager.CurrentWorkspacePath);
                        foreach (var file in di.GetFiles()) file.Delete();
                        foreach (var dir in di.GetDirectories()) dir.Delete(true);

                        // 2. 将外部 .natsros 压缩包解压到安全沙盒中
                        ZipFile.ExtractToDirectory(externalFilePath, NatsROS.Core.Environment.WorkspaceManager.CurrentWorkspacePath, true);

                        // 3. 往内部 Projects 库里 Copy 一份快照备份
                        string projDir = Path.Combine(NatsROS.Core.Environment.WorkspaceManager.LocalDataPath, "..", "Projects");
                        if (!Directory.Exists(projDir)) Directory.CreateDirectory(projDir);
                        File.Copy(externalFilePath, Path.Combine(projDir, Path.GetFileName(externalFilePath)), true);
                    });

                    // 4. 更新全局配置（记录最后加载的工程名）
                    _appConfig.LastProjectName = projectName;
                    SaveAppConfig();

                    // 5. 向全网发送工程切换广播！触发自身的菜单刷新、布局重载，以及底层管家的数据刷新
                    var wsEvent = new NatsROS.Core.SystemMessages.WorkspaceChangedEvent(projectName, DateTime.UtcNow.Ticks);
                    var headers = new NATS.Client.Core.NatsHeaders { { "ros-type", "NatsROS.Core.SystemMessages.WorkspaceChangedEvent, NatsROS.Core" } };
                    await _nats!.PublishAsync("sys.workspace.changed", wsEvent, headers: headers);

                    MessageBox.Show("🚀 外部工程已解压至安全沙盒并热加载成功！\n系统菜单与布局将自动刷新！", "导入成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex) { MessageBox.Show($"导入工程失败: {ex.Message}"); }
            }
        }

        private void SaveAppConfig()
        {
            try
            {
                string cfgPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_config.json");
                var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(cfgPath, System.Text.Json.JsonSerializer.Serialize(_appConfig, opts));
            }
            catch { }
        }

        private async void BtnExportProject_ItemClick(object sender, EventArgs e)
        {
            string defaultName = string.IsNullOrEmpty(_appConfig.LastProjectName) ? "NewProject" : _appConfig.LastProjectName;

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "NatsROS 工程包 (*.natsros)|*.natsros",
                Title = "导出当前运行的设备工程",
                FileName = $"{defaultName}.natsros" // 默认文件名使用当前工程名
            };

            if (dlg.ShowDialog() == true)
            {
                string targetZipFile = dlg.FileName;

                try
                {
                    if (File.Exists(targetZipFile))
                    {
                        File.Delete(targetZipFile);
                    }

                    // 异步执行打包，防止因为文件太多导致大屏 UI 卡死
                    await Task.Run(() =>
                    {
                        ZipFile.CreateFromDirectory(
                            NatsROS.Core.Environment.WorkspaceManager.CurrentWorkspacePath,
                            targetZipFile,
                            CompressionLevel.Optimal,
                            false);
                    });

                    MessageBox.Show($"✅ 当前沙盒配置已成功打包导出至：\n{targetZipFile}\n您可以将此工程包分发给其他同类机台直接导入运行！",
                        "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);

                    AppendLog("SYSTEM", $"📤 工程包已成功导出: {Path.GetFileName(targetZipFile)}", Colors.LimeGreen);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"导出工程失败，可能是某个配置文件正在被底层服务锁定: {ex.Message}", "导出错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BtnPluginManager_ItemClick(object sender, ItemClickEventArgs e)
        {
            string manifestPath = NatsROS.Core.Environment.WorkspaceManager.GetConfigPath("ui_manifest.json");
            List<string>? currentlyEnabled = null;

            // 1. 读取当前的清单状态
            if (File.Exists(manifestPath))
            {
                try
                {
                    var manifest = System.Text.Json.JsonSerializer.Deserialize<UiManifest>(File.ReadAllText(manifestPath));
                    currentlyEnabled = manifest?.EnabledPlugins;
                }
                catch { }
            }

            // 2. 弹出可视化管理器
            var dialog = new PluginManagerDialog(_discoveredPlugins, currentlyEnabled, _failedDlls)
            {
                Owner = Window.GetWindow(this)
            };

            // 3. 用户点击了【保存并应用】
            if (dialog.ShowDialog() == true)
            {
                var newManifest = new UiManifest { EnabledPlugins = dialog.FinalEnabledPlugins };

                // 写入文件
                var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(manifestPath, System.Text.Json.JsonSerializer.Serialize(newManifest, opts));

                // 【核心魔法】：不需要重启软件，直接在内存中瞬间重建菜单！
                BuildRibbonMenu();

                AppendLog("SYSTEM", "🧩 ui_manifest.json 已更新，插件菜单已热重载。", Colors.LimeGreen);
                MessageBox.Show("插件配置已保存，菜单栏已实时更新！", "配置成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnSwitchUser_ItemClick(object sender, ItemClickEventArgs e)
        {
            var loginWnd = new Security.LoginWindow(_nats!) { Owner = this };
            if (loginWnd.ShowDialog() == true)
            {
                // 登录成功后，全局上下文已经更新，OnUserChanged 事件会自动触发 UI 锁定/解锁

                // 【绝杀】：因为不同用户有专属的 layout.xml，所以切换账号后立刻热重载属于他的布局！
                LoadUserLayout();
            }
        }


        /// <summary>
        /// 监听全局用户切换，执行双轨模式
        /// </summary>
        private void SetupSecurityModeListener()
        {
            // 订阅全局 IAM 身份变化事件
            NatsROS.Dashboard.Security.GlobalSecurityContext.OnUserChanged += (s, e) =>
            {
                Dispatcher.Invoke(() =>
                {
                    ApplySecurityMode();
                });
            };
        }

        /// <summary>
        /// 应用当前用户的权限模式，切换 UI 的可见性和交互性。
        /// </summary>
        private void ApplySecurityMode()
        {
            var user = NatsROS.Dashboard.Security.GlobalSecurityContext.CurrentUser;
            if (user == null) return;

            // 更新底部状态栏
            StatusCurrentUser.Content = $"👤 用户: {user.DisplayName} ({user.Role})";
            // 更新右上角头像菜单的内容
            MenuUserProfile.Content = $"{user.DisplayName} ({user.Role})";
            MenuRoleInfo.Content = $"所属角色: {user.Role}";

            // 【核心逻辑】：判断是否为操作员
            // 只要不是 Admin 和 Engineer，统统判定为操作员运行模式！
            bool isDebugMode = user.Role == "Admin" || user.Role == "Engineer";

            // 1. 隐藏/显示 顶部 Ribbon 菜单
            MainRibbon.Visibility = isDebugMode ? Visibility.Visible : Visibility.Collapsed;
            RibbonCategory.IsVisible = isDebugMode;

            // 2. 锁定/解锁 DevExpress 底层布局引擎！
            DockManager.AllowCustomization = isDebugMode; // 禁用右键高级布局菜单

            var allPanels = DockManager.GetItems();
            foreach (var item in allPanels)
            {
                item.AllowDrag = isDebugMode;   // 禁止拖拽
                item.AllowFloat = isDebugMode;  // 禁止悬浮
                item.AllowClose = isDebugMode;  // 禁止点 X 关闭
                item.AllowHide = isDebugMode;   // 禁止隐藏
            }

            string modeName = isDebugMode ? "研发调试模式 (Debug)" : "生产运行模式 (Operator)";
            AppendLog("SYSTEM", $"🔐 权限已切换，当前 UI 进入: {modeName}", Colors.Orange);
        }
    }
}
