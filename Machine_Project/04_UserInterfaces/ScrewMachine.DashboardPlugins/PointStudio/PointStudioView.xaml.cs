using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using DevExpress.Xpf.Bars;
using DevExpress.Xpf.Editors.Settings;
using NATS.Client.Core;
using NatsROS.Core.Communication;
using ScrewMachine.Messages.Motion;
using NatsROS.Messages.RMS;
using MessageBox = System.Windows.MessageBox;
using UserControl = System.Windows.Controls.UserControl;

namespace ScrewMachine.Dashboard.Plugins.PointStudio
{
    // ==========================================
    // 导航树数据模型 (保持不变)
    // ==========================================
    public class NavItemModel : INotifyPropertyChanged
    {
        private string _displayName = "";

        public string GroupCategory { get; set; } = ""; // 【新增】：分组类别
        public string Type { get; set; } = "";

        public string DisplayName 
        {
            get => _displayName; 
            set 
            { 
                _displayName = value; 
                OnPropertyChanged(); 
            } 
        }

        public PointFeatureBase? RefObject { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public partial class PointStudioView : UserControl, IDisposable
    {
        private readonly INatsClient _nats;
        private readonly CancellationTokenSource _cts = new();

        public ObservableCollection<NavItemModel> NavItems { get; set; } = new();

        private readonly ObservableCollection<SinglePointModel> _allSinglePoints = new();
        private readonly ObservableCollection<TrajectoryModel> _allTrajectories = new();

        private double _liveX = 0, _liveZ = 50, _liveY1 = 0, _liveY2 = 0;
        private RecipeModel? _currentRecipe;

        private readonly JsonSerializerOptions _innerJsonOpts = new()
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public PointStudioView(INatsClient nats)
        {
            InitializeComponent();
            _nats = nats;
            GridNav.ItemsSource = NavItems;

            // 默认选中机器全局
            EditContext.EditValue = "⚙️ 机器全局";

            _ = StartTelemetryListenerAsync(_cts.Token);
            _ = LoadMachinePointsAsync();
        }

        // ==========================================
        // 1. 监听坐标反馈与全局激活配方
        // ==========================================
        private async Task StartTelemetryListenerAsync(CancellationToken ct)
        {
            try
            {
                var dispSub = new RosSubscriber<ActionFeedback<DispenserMoveFeedback>>(_nats, "simulateddispensernode_1.move.feedback", RosQosProfile.SensorData);
                var y1Sub = new RosSubscriber<ActionFeedback<AxisMoveFeedback>>(_nats, "axis_y1.move.feedback", RosQosProfile.SensorData);
                var y2Sub = new RosSubscriber<ActionFeedback<AxisMoveFeedback>>(_nats, "axis_y2.move.feedback", RosQosProfile.SensorData);
                var activeSub = new RosSubscriber<RecipeActivatedEvent>(_nats, "rms.event.recipe_activated", RosQosProfile.Reliable);

                _ = Task.Run(async () => 
                {
                    await foreach (var msg in dispSub.SubscribeAsync(ct))
                    {
                        _liveX = msg.Data.CurrentX; _liveZ = msg.Data.CurrentZ;
                        Dispatcher.InvokeAsync(() => { TxtLiveX.Text = _liveX.ToString("F2"); TxtLiveZ.Text = _liveZ.ToString("F2"); });
                    }
                });

                _ = Task.Run(async () => 
                {
                    await foreach (var msg in y1Sub.SubscribeAsync(ct))
                    {
                        _liveY1 = msg.Data.CurrentPosition;
                        Dispatcher.InvokeAsync(() => { if (CboActiveY.Text == "Y1") TxtLiveY.Text = _liveY1.ToString("F2"); });
                    }
                });

                _ = Task.Run(async () => 
                {
                    await foreach (var msg in y2Sub.SubscribeAsync(ct))
                    {
                        _liveY2 = msg.Data.CurrentPosition;
                        Dispatcher.InvokeAsync(() => { if (CboActiveY.Text == "Y2") TxtLiveY.Text = _liveY2.ToString("F2"); });
                    }
                });

                var activeClient = new RosServiceClient<GetActiveRecipeReq, GetActiveRecipeRes>(_nats, "rms.get_active");
                var activeRes = await activeClient.CallAsync(new GetActiveRecipeReq(), TimeSpan.FromSeconds(2), ct);
                if (activeRes?.ActiveRecipe != null) SyncToRecipe(activeRes.ActiveRecipe.RecipeId);

                _ = Task.Run(async () => 
                {
                    await foreach (var msg in activeSub.SubscribeAsync(ct))
                    {
                        if (msg != null) SyncToRecipe(msg.Recipe.RecipeId);
                    }
                });
            }
            catch (OperationCanceledException) { }
        }

        // ==========================================
        // 2. 工作域切换逻辑 (BarItem 事件)
        // ==========================================
        private async void EditContext_EditValueChanged(object sender, RoutedEventArgs e)
        {
            NavItems.Clear(); _allSinglePoints.Clear(); _allTrajectories.Clear(); GridData.ItemsSource = null;

            bool isMachine = EditContext.EditValue?.ToString() == "⚙️ 机器全局";

            if (isMachine)
            {
                EditRecipe.IsEnabled = false; EditRecipe.EditValue = null; _currentRecipe = null;
                EnableEditing(true);
                await LoadMachinePointsAsync();
            }
            else
            {
                EditRecipe.IsEnabled = true;
                await ReloadRecipesToComboBoxAsync();
            }
        }

        private void EditRecipe_EditValueChanged(object sender, RoutedEventArgs e)
        {
            _currentRecipe = EditRecipe.EditValue as RecipeModel;
            if (_currentRecipe != null) EnableEditing(_currentRecipe.State == RecipeState.Draft);
            LoadRecipePoints();
        }

        private async Task ReloadRecipesToComboBoxAsync()
        {
            try
            {
                var rmsClient = new RosServiceClient<GetRecipesReq, GetRecipesRes>(_nats, "rms.get_recipes");
                var res = await rmsClient.CallAsync(new GetRecipesReq(), TimeSpan.FromSeconds(2));
                if (res != null)
                {
                    Dispatcher.Invoke(() => {
                        ((ComboBoxEditSettings)EditRecipe.EditSettings).ItemsSource = res.Recipes.ToList();
                    });
                }
            }
            catch { }
        }

        private void SyncToRecipe(string recipeId)
        {
            Dispatcher.InvokeAsync(async () =>
            {
                if (EditContext.EditValue?.ToString() != "📦 产品配方")
                {
                    EditContext.EditValue = "📦 产品配方";
                    await Task.Delay(200);
                }
                await ReloadRecipesToComboBoxAsync();

                var settings = (ComboBoxEditSettings)EditRecipe.EditSettings;
                if (settings.ItemsSource is System.Collections.Generic.List<RecipeModel> recipes)
                {
                    var target = recipes.FirstOrDefault(r => r.RecipeId == recipeId);
                    if (target != null) EditRecipe.EditValue = target;
                }
            });
        }

        private void EnableEditing(bool canEdit)
        {
            BtnSavePoints.IsEnabled = canEdit;
            TxtLockWarning.IsVisible = !canEdit; // BarItem 使用 IsVisible
            GridData.View.AllowEditing = canEdit;
            GridNav.IsEnabled = canEdit;
            ToolBarSingle.IsEnabled = canEdit;
            ToolBarTraj.IsEnabled = canEdit;
        }

        private void BtnRefresh_ItemClick(object sender, ItemClickEventArgs e) => EditContext_EditValueChanged(sender, null!);

        // ==========================================
        // 3. 数据加载与导航树构建
        // ==========================================
        private async Task LoadMachinePointsAsync()
        {
            try
            {
                var paramClient = new NatsROS.Core.Parameters.RosParameterClient(_nats, "brain_dispenser");
                var keys = await paramClient.ListAsync();

                foreach (var key in keys.Where(k => k.StartsWith("MachinePoint_") || k.StartsWith("MachineTraj_")))
                {
                    string json = await paramClient.GetAsync(key) ?? "";
                    try
                    {
                        var obj = JsonSerializer.Deserialize<PointFeatureBase>(json);
                        if (obj is SinglePointModel sp) _allSinglePoints.Add(sp);
                        else if (obj is TrajectoryModel tm) _allTrajectories.Add(tm);
                    }
                    catch 
                    { 
                    }
                }
                BuildNavigationTree();
            }
            catch 
            { 
            }
        }

        private void LoadRecipePoints()
        {
            _allSinglePoints.Clear(); _allTrajectories.Clear(); NavItems.Clear(); GridData.ItemsSource = null;
            if (_currentRecipe == null) return;

            foreach (var kvp in _currentRecipe.Formula.Where(k => k.Key.StartsWith("RecipePoint_") || k.Key.StartsWith("RecipeTraj_")))
            {
                try
                {
                    var obj = JsonSerializer.Deserialize<PointFeatureBase>(kvp.Value);
                    if (obj is SinglePointModel sp) _allSinglePoints.Add(sp);
                    else if (obj is TrajectoryModel tm) _allTrajectories.Add(tm);
                }
                catch 
                { 
                }
            }
            BuildNavigationTree();
        }

        private void BuildNavigationTree()
        {
            // 1. 添加单点集合组
            NavItems.Add(new NavItemModel { GroupCategory = "📍 离散单点库 (Discrete Points)", Type = "Single", DisplayName = "📝 所有单点集合" });

            // 2. 添加轨迹集合组
            foreach (var traj in _allTrajectories)
            {
                NavItems.Add(new NavItemModel { GroupCategory = "〽️ 连续轨迹库 (Trajectories)", Type = "Trajectory", DisplayName = $"〰️ {traj.Name}", RefObject = traj });
            }
            if (NavItems.Count > 0) GridNav.SelectedItem = NavItems[0];
        }

        private void GridNav_SelectedItemChanged(object sender, DevExpress.Xpf.Grid.SelectedItemChangedEventArgs e)
        {
            if (e.NewItem is NavItemModel navItem)
            {
                if (navItem.Type == "Single")
                {
                    GrpData.Caption = "📝 离散单点明细表";
                    ToolBarSingle.Visibility = Visibility.Visible;
                    ToolBarTraj.Visibility = Visibility.Collapsed;
                    ColName.Visible = true; ColDispense.Visible = false;
                    GridData.ItemsSource = _allSinglePoints;
                }
                else if (navItem.Type == "Trajectory" && navItem.RefObject is TrajectoryModel traj)
                {
                    GrpData.Caption = $"📝 轨迹节点明细表: {traj.Name}";
                    ToolBarSingle.Visibility = Visibility.Collapsed;
                    ToolBarTraj.Visibility = Visibility.Visible;
                    TxtTrajName.Text = traj.Name;
                    ColName.Visible = false; ColDispense.Visible = true;
                    GridData.ItemsSource = traj.Nodes;
                }
            }
            else GridData.ItemsSource = null;
        }

        // ==========================================
        // 【全新内联交互】：左侧大纲树的组头与行内操作
        // ==========================================

        /// <summary>
        /// 1. 点击组头右侧的 [➕] 时触发
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void BtnInlineAddTraj_Click(object sender, RoutedEventArgs e)
        {
            string newName = $"NewPath_{_allTrajectories.Count + 1}";
            var newTraj = new TrajectoryModel { Name = newName };
            _allTrajectories.Add(newTraj);

            var navItem = new NavItemModel
            {
                GroupCategory = "〽️ 连续轨迹库 (Trajectories)",
                Type = "Trajectory",
                DisplayName = $"〰️ {newName}",
                RefObject = newTraj
            };
            NavItems.Add(navItem);

            GridNav.SelectedItem = navItem; // 自动选中新建的轨迹
        }

        /// <summary>
        /// 2. 点击数据行右侧的 [❌] 时触发
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void BtnInlineDelTraj_Click(object sender, RoutedEventArgs e)
        {
            if (sender is DevExpress.Xpf.Core.SimpleButton btn && btn.Tag is NavItemModel navItem)
            {
                if (navItem.Type == "Trajectory" && navItem.RefObject is TrajectoryModel traj)
                {
                    // 【防呆加固】：因为删除整个轨迹是高危操作，最好加一个确认框
                    if (MessageBox.Show($"确定要彻底删除轨迹 [{traj.Name}] 吗？", "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                    {
                        _allTrajectories.Remove(traj);
                        NavItems.Remove(navItem);
                    }
                }
            }
        }

        private void TxtTrajName_EditValueChanged(object sender, DevExpress.Xpf.Editors.EditValueChangedEventArgs e)
        {
            if (GridNav.SelectedItem is NavItemModel navItem && navItem.Type == "Trajectory" && navItem.RefObject is TrajectoryModel traj)
            {
                traj.Name = TxtTrajName.Text;
                navItem.DisplayName = $"〰️ {traj.Name}"; // 触发 OnPropertyChanged，左边菜单名瞬间跟着变！
                GrpData.Caption = $"📝 轨迹节点明细表: {traj.Name}";
            }
        }

        private void BtnAddSinglePoint_Click(object sender, RoutedEventArgs e) => _allSinglePoints.Add(new SinglePointModel { Name = $"Point_{_allSinglePoints.Count + 1}", X = 0, Y = 0, Z = 50 });

        private void BtnAppendTrajNode_Click(object sender, RoutedEventArgs e)
        {
            if (GridNav.SelectedItem is NavItemModel navItem && navItem.Type == "Trajectory" && navItem.RefObject is TrajectoryModel traj)
            {
                double currentY = CboActiveY.Text == "Y1" ? _liveY1 : _liveY2;
                traj.Nodes.Add(new TrajectoryNode
                {
                    X = Math.Round(_liveX, 3),
                    Y = Math.Round(currentY, 3),
                    Z = Math.Round(_liveZ, 3),
                    Speed = 50,
                    IsDispense = false
                });
            }
        }

        // ==========================================
        // 5. 行内操作 (Inline Actions)
        // ==========================================
        private void BtnInlineTeach_Click(object sender, RoutedEventArgs e)
        {
            if (sender is DevExpress.Xpf.Core.SimpleButton btn)
            {
                double currentY = CboActiveY.Text == "Y1" ? _liveY1 : _liveY2;
                if (btn.Tag is SinglePointModel sp) { sp.Teach(_liveX, currentY, _liveZ); }
                else if (btn.Tag is TrajectoryNode tn) { tn.X = Math.Round(_liveX, 3); tn.Y = Math.Round(currentY, 3); tn.Z = Math.Round(_liveZ, 3); }
            }
        }

        private async void BtnInlineMove_Click(object sender, RoutedEventArgs e)
        {
            if (sender is DevExpress.Xpf.Core.SimpleButton btn)
            {
                double targetX = 0, targetY = 0, targetZ = 50, speed = 50;
                string activeY = CboActiveY.Text ?? "Y1";

                if (btn.Tag is SinglePointModel sp) { targetX = sp.X; targetY = sp.Y; targetZ = sp.Z; speed = sp.Speed; }
                else if (btn.Tag is TrajectoryNode tn) { targetX = tn.X; targetY = tn.Y; targetZ = tn.Z; speed = tn.Speed; }
                else return;

                try
                {
                    var moveClient = new RosActionClient<DispenserMoveGoal, DispenserMoveFeedback, DispenserMoveResult>(_nats, "simulateddispensernode_1.move");
                    await moveClient.SendGoalAsync(new DispenserMoveGoal(targetX, targetY, 50.0, 100.0, activeY));
                    await moveClient.SendGoalAsync(new DispenserMoveGoal(targetX, targetY, targetZ, speed, activeY));
                }
                catch (Exception ex) { MessageBox.Show($"移动失败: {ex.Message}"); }
            }
        }

        private void BtnInlineDel_Click(object sender, RoutedEventArgs e)
        {
            if (sender is DevExpress.Xpf.Core.SimpleButton btn)
            {
                if (btn.Tag is SinglePointModel sp) _allSinglePoints.Remove(sp);
                else if (btn.Tag is TrajectoryNode tn)
                {
                    if (GridNav.SelectedItem is NavItemModel navItem && navItem.Type == "Trajectory" && navItem.RefObject is TrajectoryModel traj)
                    {
                        traj.Nodes.Remove(tn);
                    }
                }
            }
        }

        // ==========================================
        // 6. 保存数据 (支持多态)
        // ==========================================
        private async void BtnSavePoints_ItemClick(object sender, ItemClickEventArgs e)
        {
            BtnSavePoints.IsEnabled = false;
            try
            {
                int count = 0;
                bool isMachine = EditContext.EditValue?.ToString() == "⚙️ 机器全局";

                if (isMachine) // 存系统参数
                {
                    var paramClient = new NatsROS.Core.Parameters.RosParameterClient(_nats, "brain_dispenser");
                    foreach (var sp in _allSinglePoints) { await paramClient.SetAsync($"MachinePoint_{sp.Name}", JsonSerializer.Serialize(sp, typeof(PointFeatureBase), _innerJsonOpts)); count++; }
                    foreach (var tr in _allTrajectories) { await paramClient.SetAsync($"MachineTraj_{tr.Name}", JsonSerializer.Serialize(tr, typeof(PointFeatureBase), _innerJsonOpts)); count++; }
                    MessageBox.Show($"✅ 已保存 {count} 个机台公共数据！");
                }
                else // 存 RMS配方
                {
                    if (_currentRecipe == null) return;
                    var keysToRemove = _currentRecipe.Formula.Keys.Where(k => k.StartsWith("RecipePoint_") || k.StartsWith("RecipeTraj_")).ToList();
                    foreach (var k in keysToRemove) _currentRecipe.Formula.Remove(k);

                    foreach (var sp in _allSinglePoints) { _currentRecipe.Formula[$"RecipePoint_{sp.Name}"] = JsonSerializer.Serialize(sp, typeof(PointFeatureBase), _innerJsonOpts); count++; }
                    foreach (var tr in _allTrajectories) { _currentRecipe.Formula[$"RecipeTraj_{tr.Name}"] = JsonSerializer.Serialize(tr, typeof(PointFeatureBase), _innerJsonOpts); count++; }

                    var saveClient = new RosServiceClient<SaveRecipeReq, SaveRecipeRes>(_nats, "rms.save");
                    string opName = NatsROS.Core.Security.RosSecurityContext.DisplayName ?? "在线示教员";
                    var res = await saveClient.CallAsync(new SaveRecipeReq(_currentRecipe, opName, $"更新了 {count} 个工艺数据"), TimeSpan.FromSeconds(3));
                    if (res != null && res.Success) MessageBox.Show($"✅ 成功保存 {count} 个工艺数据至配方！");
                }
            }
            catch (Exception ex) { MessageBox.Show($"保存失败: {ex.Message}"); }
            finally { BtnSavePoints.IsEnabled = true; }
        }

        // ==========================================
        // 7. JOG 遥控核心逻辑 (锁轴防撞)
        // ==========================================
        private async void BtnJog_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not DevExpress.Xpf.Core.SimpleButton btn || btn.Tag == null) return;
            string cmd = btn.Tag.ToString()!;
            double step = double.Parse(CboStep.Text);
            string activeY = CboActiveY.Text;
            double targetX = _liveX, targetY = activeY == "Y1" ? _liveY1 : _liveY2, targetZ = _liveZ;

            if (cmd == "X+") targetX += step;
            else if (cmd == "X-") targetX -= step;
            else if (cmd == "Z+") targetZ += step;
            else if (cmd == "Z-") targetZ -= step;
            else if (cmd == "Y+") targetY += step; else if (cmd == "Y-") targetY -= step;

            try
            {
                if (cmd.StartsWith("X") || cmd.StartsWith("Z"))
                {
                    var moveClient = new RosActionClient<DispenserMoveGoal, DispenserMoveFeedback, DispenserMoveResult>(_nats, "simulateddispensernode_1.move");
                    await moveClient.SendGoalAsync(new DispenserMoveGoal(targetX, 0, targetZ, 50.0, "None"));
                }
                else if (cmd.StartsWith("Y"))
                {
                    string axisNode = activeY == "Y1" ? "axis_y1" : "axis_y2";
                    var yClient = new RosActionClient<AxisMoveGoal, AxisMoveFeedback, AxisMoveResult>(_nats, $"{axisNode}.move");
                    await yClient.SendGoalAsync(new AxisMoveGoal(targetY, 50.0));
                }
            }
            catch { }
        }

        public void Dispose() => _cts.Cancel();
    }
}