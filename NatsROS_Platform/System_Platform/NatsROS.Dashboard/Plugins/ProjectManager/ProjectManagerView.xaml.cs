using NATS.Client.Core;
using NatsROS.Core.Communication;
using NatsROS.Core.Environment; // 引入我们写的 WorkspaceManager
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MessageBox = System.Windows.MessageBox;
using UserControl = System.Windows.Controls.UserControl;

namespace NatsROS.Dashboard.Plugins.ProjectManager
{
    public class ProjectItem
    {
        public string FileName { get; set; } = "";
        public string FullPath { get; set; } = "";
        public double FileSizeMb { get; set; }
        public string LastModified { get; set; } = "";
    }

    public class GlobalAppConfig
    {
        public string SystemName { get; set; } = "NatsROS";
        public string LastProjectName { get; set; } = "";
        public string UiMode { get; set; } = "Dashboard";
        public bool DebugMode { get; set; } = false;
    }


    public partial class ProjectManagerView : UserControl, IDisposable
    {
        private readonly INatsClient _nats;
        public ObservableCollection<ProjectItem> Projects { get; set; } = new();

        private string _appConfigPath = "";
        private GlobalAppConfig _currentAppConfig = new();
        private bool _isInitializing = true; // 防止 UI 绑定时触发循环保存

        public ProjectManagerView(INatsClient nats)
        {
            InitializeComponent();
            _nats = nats;
            GridProjects.ItemsSource = Projects;

            //加载 app_config.json
            _appConfigPath = Path.Combine(WorkspaceManager.LocalDataPath, "..", "bin", "app_config.json");
            LoadAppConfig();

            Loaded += (s, e) => RefreshProjectList();
        }

        private void LoadAppConfig()
        {
            if (File.Exists(_appConfigPath))
            {
                try
                {
                    _currentAppConfig = System.Text.Json.JsonSerializer.Deserialize<GlobalAppConfig>(File.ReadAllText(_appConfigPath)) ?? new();
                    TogDebugMode.IsChecked = _currentAppConfig.DebugMode;
                }
                catch { }
            }
            _isInitializing = false;
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e) => RefreshProjectList();

        private void RefreshProjectList()
        {
            try
            {
                Projects.Clear();
                // 找到 Projects 文件夹
                string projDir = Path.Combine(WorkspaceManager.LocalDataPath, "..", "Projects");
                if (!Directory.Exists(projDir)) Directory.CreateDirectory(projDir);

                var files = new DirectoryInfo(projDir).GetFiles("*.natsros").OrderByDescending(f => f.LastWriteTime);

                foreach (var file in files)
                {
                    Projects.Add(new ProjectItem
                    {
                        FileName = file.Name,
                        FullPath = file.FullName,
                        FileSizeMb = Math.Round(file.Length / 1024.0 / 1024.0, 2),
                        LastModified = file.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
                    });
                }
            }
            catch (Exception ex) { MessageBox.Show("读取工程列表失败: " + ex.Message); }
        }

        private void GridProjects_SelectedItemChanged(object sender, DevExpress.Xpf.Grid.SelectedItemChangedEventArgs e)
        {
            if (e.NewItem is ProjectItem item)
            {
                TxtSelectedProject.Text = item.FileName;
                BtnLoadProject.IsEnabled = true;
            }
            else
            {
                TxtSelectedProject.Text = "未选择工程";
                BtnLoadProject.IsEnabled = false;
            }
        }

        // ==========================================
        // 核心一：打包当前工作区为 .natsros (导出)
        // ==========================================
        private async void BtnBackupCurrent_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtNewProjectName.Text)) { MessageBox.Show("请输入工程名称！"); return; }

            BtnBackupCurrent.IsEnabled = false;
            try
            {
                string projDir = Path.Combine(WorkspaceManager.LocalDataPath, "..", "Projects");
                string targetZipFile = Path.Combine(projDir, $"{TxtNewProjectName.Text.Trim()}.natsros");

                if (File.Exists(targetZipFile))
                {
                    if (MessageBox.Show("工程包已存在，是否覆盖？", "警告", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                    File.Delete(targetZipFile);
                }

                // 将 CurrentWorkspace 整个文件夹压缩成 .natsros 文件！
                await Task.Run(() => ZipFile.CreateFromDirectory(WorkspaceManager.CurrentWorkspacePath, targetZipFile, CompressionLevel.Optimal, false));

                MessageBox.Show("✅ 当前工作区已成功打包为 NatsROS 工程！");
                TxtNewProjectName.Text = "";
                RefreshProjectList();
            }
            catch (Exception ex) { MessageBox.Show($"打包失败: {ex.Message}"); }
            finally { BtnBackupCurrent.IsEnabled = true; }
        }

        // ==========================================
        // 核心二：一键解压并热切换全网配置 (加载)
        // ==========================================
        private async void BtnLoadProject_Click(object sender, RoutedEventArgs e)
        {
            if (GridProjects.SelectedItem is not ProjectItem selectedItem) return;

            var confirm = MessageBox.Show($"加载工程 [{selectedItem.FileName}] 将覆盖当前正在运行的配置！\n是否继续？", "高危操作", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            BtnLoadProject.IsEnabled = false;
            try
            {
                await Task.Run(() =>
                {
                    // 1. 清空现有的 CurrentWorkspace 目录
                    var di = new DirectoryInfo(WorkspaceManager.CurrentWorkspacePath);
                    foreach (var file in di.GetFiles()) file.Delete();
                    foreach (var dir in di.GetDirectories()) dir.Delete(true);

                    // 2. 将选中的 .natsros 压缩包解压进去！
                    ZipFile.ExtractToDirectory(selectedItem.FullPath, WorkspaceManager.CurrentWorkspacePath, true);
                });

                // 3. 【神级魔法】：向全网发送系统广播，通知所有管家微服务热重载文件！
                var wsEvent = new NatsROS.Core.SystemMessages.WorkspaceChangedEvent(selectedItem.FileName, DateTime.UtcNow.Ticks);
                var headers = new NatsHeaders { { "ros-type", "NatsROS.Core.SystemMessages.WorkspaceChangedEvent, NatsROS.Core" } };

                await _nats.PublishAsync("sys.workspace.changed", wsEvent, headers: headers);

                MessageBox.Show("🚀 工程已加载并解压！系统已发出全网热重载广播！\n底层的报警字典、配方、权限均已自动切换为新工程的状态。", "加载成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { MessageBox.Show($"加载工程失败: {ex.Message}"); }
            finally { BtnLoadProject.IsEnabled = true; }
        }

        private void TogDebugMode_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;

            _currentAppConfig.DebugMode = TogDebugMode.IsChecked == true;
            try
            {
                var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(_appConfigPath, System.Text.Json.JsonSerializer.Serialize(_currentAppConfig, opts));

                MessageBox.Show("调试模式已切换！\n将在下次启动系统时生效。", "配置已保存", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { MessageBox.Show("保存配置失败: " + ex.Message); }
        }

        public void Dispose()
        {
            //throw new NotImplementedException();
        }
    }
}