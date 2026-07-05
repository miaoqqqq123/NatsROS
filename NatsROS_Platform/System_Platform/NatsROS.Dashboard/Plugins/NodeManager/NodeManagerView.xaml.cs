using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using NATS.Client.Core;
using NatsROS.Core.Attributes;
using NatsROS.Core.SystemMessages;
using NatsROS.Dashboard.Models;
using MessageBox = System.Windows.MessageBox;
using UserControl = System.Windows.Controls.UserControl;

namespace NatsROS.Dashboard.Plugins.NodeManager
{
    public partial class NodeManagerView : UserControl
    {
        private readonly INatsClient _nats;
        public ObservableCollection<AvailableNodeInfo> AvailableNodes { get; set; } = new();
        public ObservableCollection<NodeItem> RunningNodes { get; set; } = new();
        private string? _currentEditingNodeName;
        private DynamicParameterObject? _currentParams;

        public NodeManagerView(INatsClient nats)
        {
            InitializeComponent();
            _nats = nats;
            GridAvailableNodes.ItemsSource = AvailableNodes;
            GridNodes.ItemsSource = RunningNodes;

            ScanAvailableNodesWithAttributes();
            _ = RefreshNodeListAsync();
        }

        private void ScanAvailableNodesWithAttributes()
        {
            try
            {
                var nodeTypes = AppDomain.CurrentDomain.GetAssemblies().SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } }).Where(t => typeof(NatsROS.Core.RosNode).IsAssignableFrom(t) && !t.IsAbstract).Where(n => !n.FullName!.Contains("ContainerManagerNode"));
                foreach (var t in nodeTypes)
                {
                    var attr = t.GetCustomAttribute<RosNodeAttribute>();
                    AvailableNodes.Add(new AvailableNodeInfo { AssemblyName = t.Assembly.GetName().Name ?? "", TypeName = t.FullName ?? "", DisplayName = attr?.DisplayName ?? t.Name, Category = attr?.Category ?? "默认" });
                }
            }
            catch { }
        }

        // 双击立刻拉起一个节点
        private async void GridAvailableNodes_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (GridAvailableNodes.SelectedItem is not AvailableNodeInfo selectedInfo) return;
            string nodeName = $"{selectedInfo.TypeName.Split('.').Last().ToLower()}_{RunningNodes.Count + 1}";
            try
            {
                var req = new LoadNodeReq(selectedInfo.AssemblyName, selectedInfo.TypeName, nodeName);
                await _nats.RequestAsync<LoadNodeReq, LoadNodeRes>("container.load_node", req, replyOpts: new NatsSubOpts { Timeout = TimeSpan.FromSeconds(3) });
                await RefreshNodeListAsync();
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await RefreshNodeListAsync();
        private async Task RefreshNodeListAsync()
        {
            if (_nats == null) return;
            try
            {
                var res = await _nats.RequestAsync<ListNodesReq, ListNodesRes>("container.list_nodes", new ListNodesReq(), replyOpts: new NatsSubOpts { Timeout = TimeSpan.FromSeconds(2) });
                if (res.Data != null)
                {
                    RunningNodes.Clear();
                    foreach (var n in res.Data.Nodes)
                    {
                        string s = n.State switch { 0 => "⚪ Unconfigured", 1 => "🟡 Inactive", 2 => "🟢 Active", 3 => "🔴 Faulted", _ => "⚫ Unknown" };
                        RunningNodes.Add(new NodeItem { NodeName = n.NodeName, StateCode = n.State, StateStr = s });
                    }
                    GridNodes.RefreshData();
                }
            }
            catch { }
        }

        // 生命周期状态控制
        private async void BtnStateActive_Click(object sender, RoutedEventArgs e) => await ChangeStateAsync(2);
        private async void BtnStateInactive_Click(object sender, RoutedEventArgs e) => await ChangeStateAsync(1);
        private async Task ChangeStateAsync(byte targetState)
        {
            if (GridNodes.SelectedItem is not NodeItem selectedItem) return;
            try
            {
                await _nats.RequestAsync<ChangeStateReq, ChangeStateRes>("container.change_state", new ChangeStateReq(selectedItem.NodeName, targetState), replyOpts: new NatsSubOpts { Timeout = TimeSpan.FromSeconds(2) });
                await Task.Delay(200); // 稍微等一下状态流转
                await RefreshNodeListAsync();
            }
            catch { }
        }

        private async void BtnUnloadNode_Click(object sender, RoutedEventArgs e)
        {
            if (GridNodes.SelectedItem is not NodeItem selectedItem) return;
            try
            {
                await _nats.RequestAsync<UnloadNodeReq, UnloadNodeRes>("container.unload_node", new UnloadNodeReq(selectedItem.NodeName), replyOpts: new NatsSubOpts { Timeout = TimeSpan.FromSeconds(3) });
                await RefreshNodeListAsync();
            }
            catch { }
        }

        // 参数热更部分保持原样...
        private async void GridNodes_SelectedItemChanged(object sender, DevExpress.Xpf.Grid.SelectedItemChangedEventArgs e)
        {
            if (e.NewItem is not NodeItem selectedNode) { GrpNodeParams.Header = "未选择"; PropGridParams.SelectedObject = null; BtnApplyParams.IsEnabled = false; return; }
            _currentEditingNodeName = selectedNode.NodeName;
            try
            {
                var paramClient = new Core.Parameters.RosParameterClient(_nats, _currentEditingNodeName);
                var keys = await paramClient.ListAsync();
                _currentParams = new DynamicParameterObject();
                foreach (var key in keys) { var val = await paramClient.GetAsync(key); if (val != null) _currentParams.Properties[key] = val; }
                GrpNodeParams.Header = $"热更参数: {_currentEditingNodeName}";
                PropGridParams.SelectedObject = _currentParams;
                BtnApplyParams.IsEnabled = _currentParams.Properties.Count > 0;
            }
            catch { }
        }
        private async void BtnApplyParams_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentEditingNodeName) || _currentParams == null) return;
            BtnApplyParams.IsEnabled = false;
            try
            {
                var paramClient = new Core.Parameters.RosParameterClient(_nats, _currentEditingNodeName);
                foreach (var kvp in _currentParams.Properties) await paramClient.SetAsync(kvp.Key, kvp.Value);
                MessageBox.Show("参数更新成功！");
            }
            catch { }
            finally { BtnApplyParams.IsEnabled = true; }
        }
    }
}