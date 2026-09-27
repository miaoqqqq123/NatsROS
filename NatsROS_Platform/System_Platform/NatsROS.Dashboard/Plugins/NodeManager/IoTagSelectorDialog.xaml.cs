using NATS.Client.Core;
using NatsROS.Core.SystemMessages;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using NatsROS.Messages.Hardware;
using MessageBox = System.Windows.MessageBox;

namespace NatsROS.Dashboard.Plugins.NodeManager
{
    public class IoTagModel 
    { 
        public string TagName { get; set; } = "";
        public string PhysicalPin { get; set; } = ""; 
    }

    public partial class IoTagSelectorDialog : DevExpress.Xpf.Core.ThemedWindow
    {
        public string SelectedTag { get; private set; } = "";
        public List<IoTagModel> TagList { get; set; } = new();

        public IoTagSelectorDialog(INatsClient nats)
        {
            InitializeComponent();
            _ = LoadTagsAsync(nats);
        }

        private async Task LoadTagsAsync(INatsClient nats)
        {
            try
            {
                // 1. 寻找网络上活着的 IO 板卡节点
                var listRes = await nats.RequestAsync<ListNodesReq, ListNodesRes>("container.list_nodes", new ListNodesReq(), replyOpts: new NatsSubOpts { Timeout = TimeSpan.FromSeconds(2) });

                if (listRes.Data?.Nodes != null)
                {
                    foreach (var n in listRes.Data.Nodes.Where(x => x.TypeName.Contains("IoBoard")))
                    {
                        // 2. 【核心重构】：不再读取配置字符串，直接向它发起 RPC 索要强类型字典！
                        var dictRes = await nats.RequestAsync<GetIoDictReq, GetIoDictRes>($"{n.NodeName}.io.get_dict", new GetIoDictReq(), replyOpts: new NatsSubOpts { Timeout = TimeSpan.FromSeconds(2) });

                        if (dictRes.Data != null && dictRes.Data.Success)
                        {
                            foreach (var point in dictRes.Data.IoPoints)
                            {
                                string typeIcon = point.Type == NatsROS.Messages.Hardware.IoType.Input ? "📥 IN" : "📤 OUT";
                                TagList.Add(new IoTagModel { TagName = point.TagName, PhysicalPin = $"Pin {point.PhysicalPin} ({typeIcon})" });
                            }
                            break; // 拿到一个就够了
                        }
                    }
                }

                Dispatcher.Invoke(() => GridTags.ItemsSource = TagList);
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => MessageBox.Show($"加载 IO 标签失败: {ex.Message}"));
            }
        }

        private void GridTags_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => Confirm();
        private void BtnOk_Click(object sender, RoutedEventArgs e) => Confirm();
        private void BtnCancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }

        private void Confirm()
        {
            if (GridTags.SelectedItem is IoTagModel selected)
            {
                SelectedTag = selected.TagName;
                DialogResult = true;
                Close();
            }
        }
    }
}