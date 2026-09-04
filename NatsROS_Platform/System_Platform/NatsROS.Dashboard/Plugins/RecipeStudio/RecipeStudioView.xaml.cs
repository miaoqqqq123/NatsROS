using DevExpress.Office.Utils;
using NATS.Client.Core;
using NatsROS.Core.Communication;
using NatsROS.Messages.RMS;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MessageBox = System.Windows.MessageBox;
using UserControl = System.Windows.Controls.UserControl;

namespace NatsROS.Dashboard.Plugins.RecipeStudio
{
    public class ParamItem { public string Key { get; set; } = ""; public string Value { get; set; } = ""; }

    public partial class RecipeStudioView : UserControl, IDisposable
    {
        private readonly RosServiceClient<GetRecipesReq, GetRecipesRes> _getRecipesClient;
        private readonly RosServiceClient<SaveRecipeReq, SaveRecipeRes> _saveClient;
        private readonly RosServiceClient<ChangeRecipeStateReq, ChangeRecipeStateRes> _stateClient;
        private readonly RosServiceClient<GetAuditLogsReq, GetAuditLogsRes> _auditClient;
        private readonly RosServiceClient<ActivateRecipeReq, ActivateRecipeRes> _activateClient;
        private readonly CancellationTokenSource _cts = new();

        private ObservableCollection<RecipeModel> _recipes = new();
        private ObservableCollection<ParamItem> _currentParams = new();
        private RecipeModel? _currentRecipe;
        private string _operatorName = "工程师_Admin"; // 演示用，真实场景读取登录名

        public RecipeStudioView(INatsClient nats)
        {
            InitializeComponent();
            _getRecipesClient = new(nats, "rms.get_recipes");
            _saveClient = new(nats, "rms.save");
            _stateClient = new(nats, "rms.change_state");
            _auditClient = new(nats, "rms.get_audits");
            _activateClient = new(nats, "rms.activate");

            GridRecipes.ItemsSource = _recipes;
            GridParams.ItemsSource = _currentParams;

            Loaded += async (s, e) => await RefreshDataAsync();


            // 【新增】：永远监听全网的配方更新广播！
            // 这里使用普通的 SensorData Qos 即可，因为纯粹是 UI 刷新信号
            _ = Task.Run(async () =>
            {
                try
                {
                    var updateSub = new RosSubscriber<RecipeUpdatedEvent>(nats, "rms.event.recipe_updated", RosQosProfile.SensorData);
                    await foreach (var msg in updateSub.SubscribeAsync(_cts.Token))
                    {
                        if (msg != null)
                        {
                            // 收到任何节点修改了配方，立刻触发本界面的智能刷新！
                            await RefreshDataAsync();
                        }
                    }
                }
                catch (OperationCanceledException) { }
            });
        }


        private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await RefreshDataAsync();

        private async Task RefreshDataAsync()
        {
            try
            {
                // 1. 记住当前正在看的是哪个配方
                string selectedId = _currentRecipe?.RecipeId ?? "";

                // 2. 去大管家拉取最新数据
                var res = await _getRecipesClient.CallAsync(new GetRecipesReq(), TimeSpan.FromSeconds(2));
                if (res != null)
                {
                    Dispatcher.Invoke(() =>
                    {
                        // 3. 全量替换内存
                        _recipes.Clear();
                        foreach (var r in res.Recipes) _recipes.Add(r);

                        // 4. 恢复选中状态（视觉防抖：让用户感觉不到表格被清空过）
                        if (!string.IsNullOrEmpty(selectedId))
                        {
                            var target = _recipes.FirstOrDefault(r => r.RecipeId == selectedId);
                            if (target != null)
                            {
                                GridRecipes.SelectedItem = target;
                            }
                        }
                    });
                }
            }
            catch (Exception ex) { MessageBox.Show($"获取配方失败: {ex.Message}"); }
        }

        private async void GridRecipes_SelectedItemChanged(object sender, DevExpress.Xpf.Grid.SelectedItemChangedEventArgs e)
        {
            _currentRecipe = e.NewItem as RecipeModel;
            if (_currentRecipe == null) return;

            TxtId.Text = _currentRecipe.RecipeId;
            TxtName.Text = _currentRecipe.RecipeName;
            TxtVersion.Text = _currentRecipe.Version;

            _currentParams.Clear();
            foreach (var kvp in _currentRecipe.Formula) _currentParams.Add(new ParamItem { Key = kvp.Key, Value = kvp.Value });

            // 统一刷新按钮与界面的权限状态
            UpdateUiState(_currentRecipe.State);

            // 拉取针对该配方的审计追踪日志
            var auditRes = await _auditClient.CallAsync(new GetAuditLogsReq(_currentRecipe.RecipeId), TimeSpan.FromSeconds(2));
            if (auditRes != null)
                GridAudits.ItemsSource = auditRes.Logs.
                    OrderByDescending(t => t.Timestamp)
                    .Select(t => new
                    {
                        // 转换为本地时区，并格式化为 yyyy-MM-dd HH:mm:ss.fff
                        FormattedTime = DateTimeOffset.FromUnixTimeMilliseconds(t.Timestamp).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"),
                        t.Operator,
                        t.Action,
                        t.Details
                    }).ToList();
        }

        // ==========================================
        // 核心：状态机与界面读写锁
        // ==========================================
        private void UpdateUiState(RecipeState state)
        {
            bool isDraft = state == RecipeState.Draft;

            // 只有草稿能保存、能审批、能改参数
            BtnSave.IsEnabled = isDraft;
            BtnApprove.IsEnabled = isDraft;
            GridParams.View.AllowEditing = isDraft;
            TxtId.IsReadOnly = !isDraft;
            TxtName.IsReadOnly = !isDraft;
            TxtVersion.IsReadOnly = !isDraft;

            // 只有草稿或批准的可以作废
            BtnObsolete.IsEnabled = state == RecipeState.Approved || state == RecipeState.Draft;

            // 只有作废的可以退回草稿重新启用
            BtnToDraft.IsEnabled = state == RecipeState.Obsolete;

            // 只要选中了任何配方，都可以克隆
            BtnClone.IsEnabled = _currentRecipe != null;
        }

        // ==========================================
        // 问题 1 & 3：复制与升版 (Clone)
        // ==========================================
        private void BtnClone_Click(object sender, RoutedEventArgs e)
        {
            if (_currentRecipe == null) return;

            // 切断与原配方的关联，当做全新配方处理
            _currentRecipe = null;

            // ID 和名字自动加上 Copy 后缀，提醒用户修改
            TxtId.Text = TxtId.Text + "_COPY";
            TxtName.Text = TxtName.Text + " (副本)";

            // 赋予草稿状态的编辑权限
            UpdateUiState(RecipeState.Draft);
            GridAudits.ItemsSource = null; // 清空旧配方的审计日志

            MessageBox.Show("已成功复制当前配方参数！\n现在您可以修改参数或版本号，并作为新配方【保存草稿】。", "克隆成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnNew_Click(object sender, RoutedEventArgs e)
        {
            _currentRecipe = null;
            TxtId.Text = "RECIPE_NEW_001"; TxtName.Text = "新产品配方"; TxtVersion.Text = "V1.0";
            _currentParams.Clear();
            _currentParams.Add(new ParamItem { Key = "Velocity", Value = "30" });
            _currentParams.Add(new ParamItem { Key = "SafeZ", Value = "50" });
            BtnSave.IsEnabled = true; BtnApprove.IsEnabled = false; GridParams.View.AllowEditing = true;
            GridAudits.ItemsSource = null;
        }

        private void BtnAddParam_Click(object sender, RoutedEventArgs e) => _currentParams.Add(new ParamItem { Key = "NewParam", Value = "0" });
        private void BtnDelParam_Click(object sender, RoutedEventArgs e) { if (GridParams.SelectedItem is ParamItem p) _currentParams.Remove(p); }

        private async void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtChangeReason.Text))
            {
                MessageBox.Show("必须填写修改原因！(FDA合规要求)");
                return;
            }
            if (string.IsNullOrWhiteSpace(TxtId.Text))
            {
                MessageBox.Show("配方代码不能为空！");
                return;
            }

            // 【核心防呆】：找出有没有重名的参数 Key
            var duplicateKeys = _currentParams.GroupBy(p => p.Key).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (duplicateKeys.Count > 0)
            {
                MessageBox.Show($"保存失败！发现重复的参数名:\n{string.Join(", ", duplicateKeys)}\n请修改后再保存。", "参数冲突", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            BtnSave.IsEnabled = false;

            // 安全转换为字典
            var formula = _currentParams.ToDictionary(p => p.Key, p => p.Value);
            var recipe = new RecipeModel(TxtId.Text, TxtName.Text, TxtVersion.Text, RecipeState.Draft, formula, _operatorName, 0);

            try
            {
                var res = await _saveClient.CallAsync(new SaveRecipeReq(recipe, _operatorName, TxtChangeReason.Text), TimeSpan.FromSeconds(2));
                if (res != null && res.Success) { TxtChangeReason.Text = ""; await RefreshDataAsync(); }
                else MessageBox.Show(res?.Message ?? "保存超时");
            }
            catch (Exception ex)
            {
                MessageBox.Show("报错异常: " + ex.Message);
            }
            finally
            {
                BtnSave.IsEnabled = true;
            }
        }

        // ==========================================
        // 问题 4：作废 (Obsolete) 与 重新启用 (To Draft)
        // ==========================================
        private async void BtnApprove_Click(object sender, RoutedEventArgs e) => await ChangeStateAsync(RecipeState.Approved, "确认批准此配方？锁定后将无法修改！");
        private async void BtnObsolete_Click(object sender, RoutedEventArgs e) => await ChangeStateAsync(RecipeState.Obsolete, "确认将此配方作废？作废后产线将无法再选择该配方！");
        private async void BtnToDraft_Click(object sender, RoutedEventArgs e) => await ChangeStateAsync(RecipeState.Draft, "确认重新启用此配方？它将被退回草稿状态以供编辑！");

        private async Task ChangeStateAsync(RecipeState targetState, string prompt)
        {
            if (_currentRecipe == null) return;
            if (MessageBox.Show(prompt, "变更确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                try
                {
                    await _stateClient.CallAsync(new ChangeRecipeStateReq(_currentRecipe.RecipeId, targetState, _operatorName), TimeSpan.FromSeconds(2));
                    await RefreshDataAsync();
                }
                catch (Exception ex) { MessageBox.Show(ex.Message); }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            //throw new NotImplementedException();
        }

        // 【新增】：调用激活服务
        private async void BtnActivate_Click(object sender, RoutedEventArgs e)
        {
            if (_currentRecipe == null) return;

            // 真实工业场景中，通常只允许激活已批准(Approved)的配方，但为了调试方便，我们先不强制拦截
            try
            {

                var res = await _activateClient.CallAsync(new ActivateRecipeReq(_currentRecipe.RecipeId), TimeSpan.FromSeconds(2));

                if (res != null && res.Success)
                    MessageBox.Show($"✅ 配方 [{_currentRecipe.RecipeName}] 已下发至产线！\n全网节点已同步更新上下文。", "激活成功", MessageBoxButton.OK, MessageBoxImage.Information);
                else
                    MessageBox.Show(res?.Message ?? "激活超时");
            }
            catch (Exception ex) { MessageBox.Show("激活失败: " + ex.Message); }
        }
    }
}