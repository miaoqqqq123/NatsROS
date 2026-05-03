using System;
using System.Windows;
using System.Windows.Media;
using NatsROS.Dashboard.Models;
using MessageBox = System.Windows.MessageBox;

namespace NatsROS.Dashboard
{
    public partial class MainWindow
    {
        private async void GridNodes_SelectedItemChanged(object sender, DevExpress.Xpf.Grid.SelectedItemChangedEventArgs e)
        {
            var selectedNode = e.NewItem as NodeItem;
            if (selectedNode == null || _nats == null)
            {
                GrpNodeParams.Header = "节点参数热更新 (未选择)";
                PropGridParams.SelectedObject = null;
                BtnApplyParams.IsEnabled = false;
                return;
            }

            _currentEditingNodeName = selectedNode.NodeName;
            GrpNodeParams.Header = $"读取 [{_currentEditingNodeName}] 的参数中...";
            BtnApplyParams.IsEnabled = false;

            try
            {
                var paramClient = new NatsROS.Core.Parameters.RosParameterClient(_nats, _currentEditingNodeName);
                var keys = await paramClient.ListAsync();
                _currentParams = new DynamicParameterObject();

                foreach (var key in keys)
                {
                    var val = await paramClient.GetAsync(key);
                    if (val != null) _currentParams.Properties[key] = val;
                }

                GrpNodeParams.Header = $"参数配置: {_currentEditingNodeName}";
                PropGridParams.SelectedObject = _currentParams;
                BtnApplyParams.IsEnabled = _currentParams.Properties.Count > 0;
            }
            catch (Exception ex) { GrpNodeParams.Header = "读取参数失败"; }
        }

        private async void BtnApplyParams_Click(object sender, RoutedEventArgs e)
        {
            if (_nats == null || string.IsNullOrEmpty(_currentEditingNodeName) || _currentParams == null) return;
            BtnApplyParams.IsEnabled = false; BtnApplyParams.Content = "⏳ 正在下发...";
            try
            {
                var paramClient = new NatsROS.Core.Parameters.RosParameterClient(_nats, _currentEditingNodeName);
                int successCount = 0;
                foreach (var kvp in _currentParams.Properties)
                {
                    if (await paramClient.SetAsync(kvp.Key, kvp.Value)) successCount++;
                }
                AppendLog("SYSTEM", $"✅ 向 {_currentEditingNodeName} 热更新了 {successCount} 个参数！", Colors.LimeGreen);
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
            finally { BtnApplyParams.IsEnabled = true; BtnApplyParams.Content = "💾 应用参数更改 (Apply)"; }
        }
    }
}
