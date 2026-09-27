using NATS.Client.Core;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using NatsROS.Messages.Motion;

namespace NatsROS.Dashboard.Plugins.BehaviorTreeEditor
{
    public class PointEntryViewModel
    {
        public string Source { get; set; } = "";
        public string PointName { get; set; } = "";
        public string Coordinates { get; set; } = "";
        public string Description { get; set; } = "";
    }

    public partial class PointSelectorDialog : DevExpress.Xpf.Core.ThemedWindow
    {
        public string SelectedPointName { get; private set; } = "";
        public ObservableCollection<PointEntryViewModel> PointList { get; set; } = new();

        // 【新增】：用于暂存传进来的旧点位名称
        private readonly string _initialPointName;

        public PointSelectorDialog(INatsClient nats, string initialPointName = "")
        {
            InitializeComponent();
            _initialPointName = initialPointName; // 记下传进来的值
            GridPoints.ItemsSource = PointList;

            // 异步加载全网点位
            _ = LoadPointsAsync(nats);
        }

        private async Task LoadPointsAsync(INatsClient nats)
        {
            try
            {
                // ==========================================
                // 1. 局部作用域：去 RMS 索要【当前激活配方】的点位！
                // ==========================================
                var activeClient = new NatsROS.Core.Communication.RosServiceClient<NatsROS.Messages.RMS.GetActiveRecipeReq, NatsROS.Messages.RMS.GetActiveRecipeRes>(nats, "rms.get_active");
                var activeRes = await activeClient.CallAsync(new NatsROS.Messages.RMS.GetActiveRecipeReq(), TimeSpan.FromSeconds(2));

                if (activeRes != null && activeRes.ActiveRecipe != null)
                {
                    var recipe = activeRes.ActiveRecipe;

                    // 【核心重构】：抛弃了 Formula 字典，直接动态解析 PayloadJson！
                    if (!string.IsNullOrEmpty(recipe.PayloadJson))
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(recipe.PayloadJson);

                        // 遍历配方 JSON 第一层的所有属性
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            // 如果这个属性是一个对象，并且里面带有我们设定的多态标签 "$type"
                            if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Object &&
                                prop.Value.TryGetProperty("$type", out var typeElement))
                            {
                                string typeStr = typeElement.GetString() ?? "";

                                if (typeStr == "Single") // 这是一个离散单点！
                                {
                                    double x = prop.Value.TryGetProperty("X", out var xProp) ? xProp.GetDouble() : 0;
                                    double y = prop.Value.TryGetProperty("Y", out var yProp) ? yProp.GetDouble() : 0;
                                    double z = prop.Value.TryGetProperty("Z", out var zProp) ? zProp.GetDouble() : 0;
                                    string name = prop.Value.TryGetProperty("Name", out var nProp) ? nProp.GetString() ?? prop.Name : prop.Name;

                                    Dispatcher.Invoke(() => {
                                        PointList.Add(new PointEntryViewModel
                                        {
                                            Source = "📦 当前配方",
                                            PointName = name,
                                            Coordinates = $"X:{x:F1}, Y:{y:F1}, Z:{z:F1}",
                                            Description = $"所属配方: {recipe.RecipeName}"
                                        });
                                    });
                                }
                                else if (typeStr == "Trajectory") // 这是一个连续轨迹！
                                {
                                    string name = prop.Value.TryGetProperty("Name", out var nProp) ? nProp.GetString() ?? prop.Name : prop.Name;

                                    Dispatcher.Invoke(() => {
                                        PointList.Add(new PointEntryViewModel
                                        {
                                            Source = "📦 当前配方",
                                            PointName = name,
                                            Coordinates = "[连续轨迹]",
                                            Description = $"所属配方: {recipe.RecipeName}"
                                        });
                                    });
                                }
                            }
                        }
                    }
                }
            }
            catch { /* 忽略 RMS 断线异常 */ }

            try
            {
                // ==========================================
                // 2. 全局作用域：机器物理公共点 (保持不变)
                // ==========================================
                var paramClient = new NatsROS.Core.Parameters.RosParameterClient(nats, "brain_dispenser");
                var keys = await paramClient.ListAsync();

                foreach (var key in keys)
                {
                    if (key.StartsWith("MachinePoint_"))
                    {
                        string pureName = key.Substring(13);
                        string json = await paramClient.GetAsync(key) ?? "";
                        string coords = "未知";

                        try
                        {
                            var pt = System.Text.Json.JsonSerializer.Deserialize<NamedPoint>(json);
                            if (pt != null) coords = $"X:{pt.X:F1}, Y:{pt.Y:F1}, Z:{pt.Z:F1}";
                        }
                        catch { }

                        Dispatcher.Invoke(() => {
                            PointList.Add(new PointEntryViewModel
                            {
                                Source = "⚙️ 机器全局",
                                PointName = pureName,
                                Coordinates = coords,
                                Description = "机台固有物理基准点"
                            });
                        });
                    }
                }
            }
            catch { }

            // 3. 高亮历史选项 (保持不变)
            Dispatcher.Invoke(() =>
            {
                if (!string.IsNullOrEmpty(_initialPointName))
                {
                    var targetItem = PointList.FirstOrDefault(p => p.PointName == _initialPointName);
                    if (targetItem != null)
                    {
                        GridPoints.SelectedItem = targetItem;
                        GridPoints.View.ScrollIntoView(targetItem);
                    }
                }
            });
        }

        private void GridPoints_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => ConfirmSelection();
        private void BtnOk_Click(object sender, RoutedEventArgs e) => ConfirmSelection();
        private void BtnCancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }

        private void ConfirmSelection()
        {
            if (GridPoints.SelectedItem is PointEntryViewModel selected)
            {
                SelectedPointName = selected.PointName;
                DialogResult = true;
                Close();
            }
        }
    }
}