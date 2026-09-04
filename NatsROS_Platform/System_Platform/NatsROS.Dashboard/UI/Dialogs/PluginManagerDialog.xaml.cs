using NatsROS.Core.UI;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace NatsROS.Dashboard.UI.Dialogs
{
    public class PluginItemModel : INotifyPropertyChanged
    {
        private bool _isEnabled;
        public bool IsEnabled { get => _isEnabled; set { _isEnabled = value; OnPropertyChanged(); } }
        public string ClassName { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string RibbonPage { get; set; } = "";
        public string RibbonGroup { get; set; } = "";
        public string Source { get; set; } = "";

        public string Version { get; set; } = "1.0.0.0";

        // 【新增】：状态和错误信息
        public string Status { get; set; } = "✅ 正常";
        public string ErrorMessage { get; set; } = "就绪";

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public partial class PluginManagerDialog : DevExpress.Xpf.Core.ThemedWindow
    {
        public ObservableCollection<PluginItemModel> PluginList { get; set; } = new();

        public List<string> FinalEnabledPlugins => PluginList.Where(p => p.IsEnabled).Select(p => p.ClassName).ToList();

        // 构造函数接收三种数据源
        public PluginManagerDialog(List<IDashboardPlugin> allPlugins, List<string>? currentlyEnabled, List<(string Name, string Error, string Source)> failedDlls)
        {
            InitializeComponent();
            GridPlugins.ItemsSource = PluginList;

            bool enableAll = currentlyEnabled == null || currentlyEnabled.Count == 0;

            // 1. 挂载所有健康加载的插件
            foreach (var plugin in allPlugins)
            {
                string className = plugin.GetType().Name;
                string source = plugin.GetType().Assembly.Location.Contains("CurrentWorkspace") ? "📦 当前工程" : "⚙️ 核心内置";
                //反射提取插件物理版本号
                string version = plugin.GetType().Assembly.GetName().Version?.ToString() ?? "1.0.0.0";

                PluginList.Add(new PluginItemModel
                {
                    ClassName = className,
                    DisplayName = plugin.DisplayName,
                    RibbonPage = plugin.RibbonPage,
                    RibbonGroup = plugin.RibbonGroup,
                    Source = source,
                    Version = version,
                    Status = "✅ 正常",
                    ErrorMessage = "模块加载与实例化成功",
                    IsEnabled = enableAll || currentlyEnabled!.Contains(className)
                });
            }

            // 2. 追查在 ui_manifest.json 中写了，但物理上不存在的“丢失插件”
            if (currentlyEnabled != null)
            {
                foreach (var expectedClass in currentlyEnabled)
                {
                    if (!allPlugins.Any(p => p.GetType().Name == expectedClass))
                    {
                        PluginList.Add(new PluginItemModel
                        {
                            ClassName = expectedClass,
                            DisplayName = "???",
                            RibbonPage = "未知",
                            RibbonGroup = "未知",
                            Source = "👻 清单遗留",
                            Version = "未知",
                            Status = "⚠️ 丢失",
                            ErrorMessage = "清单中配置了该插件，但在磁盘/内存中未找到对应的代码类，已被跳过。",
                            IsEnabled = true // 保持打钩，如果用户修好 DLL 放回去还能复活
                        });
                    }
                }
            }

            // 3. 曝光那些导致反射崩溃、依赖缺失的“坠机 DLL”
            foreach (var failed in failedDlls)
            {
                PluginList.Add(new PluginItemModel
                {
                    ClassName = failed.Name,
                    DisplayName = "无法解析的组件",
                    RibbonPage = "代码异常",
                    RibbonGroup = "未知",
                    Source = failed.Source,
                    Version = "未知",
                    Status = "❌ 崩溃",
                    ErrorMessage = failed.Error,
                    IsEnabled = false
                });
            }
        }

        private void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }
        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}