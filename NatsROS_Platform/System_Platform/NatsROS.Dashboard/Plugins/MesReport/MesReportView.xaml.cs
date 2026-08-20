using NATS.Client.Core;
using NatsROS.Core.Communication;
using NatsROS.Messages.MES;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MessageBox = System.Windows.MessageBox;
using UserControl = System.Windows.Controls.UserControl;

namespace NatsROS.Dashboard.Plugins.MesReport
{
    public partial class MesReportView : UserControl, IDisposable
    {
        private readonly RosServiceClient<GetRecordsReq, GetRecordsRes> _queryClient;

        // 定义图表简单数据结构
        public class YieldData { public string Name { get; set; } = ""; public int Value { get; set; } }

        public MesReportView(INatsClient nats)
        {
            InitializeComponent();
            _queryClient = new RosServiceClient<GetRecordsReq, GetRecordsRes>(nats, "mockmesnode_1.query");
            Loaded += async (s, e) => await LoadDataAsync();
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await LoadDataAsync();

        private async System.Threading.Tasks.Task LoadDataAsync()
        {
            try
            {
                var res = await _queryClient.CallAsync(new GetRecordsReq { Limit = 5000 }, TimeSpan.FromSeconds(3));
                if (res != null && res.Records != null)
                {
                    // 1. 绑定底部表格
                    GridRecords.ItemsSource = res.Records;

                    // 2. 计算良率饼图
                    int passCount = res.Records.Count(r => r.IsPass);
                    int failCount = res.Records.Count - passCount;

                    var yieldStats = new System.Collections.Generic.List<YieldData>();
                    if (passCount > 0) yieldStats.Add(new YieldData { Name = "良品 (PASS)", Value = passCount });
                    if (failCount > 0) yieldStats.Add(new YieldData { Name = "不良品 (FAIL)", Value = failCount });
                    PieYield.DataSource = yieldStats;

                    // 3. 绑定散点图
                    ScatterOffset.DataSource = res.Records;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"拉取 MES 数据失败，请确保本地 MES 节点已启动。错误: {ex.Message}");
            }
        }

        public void Dispose()
        {
            //throw new NotImplementedException();
        }
    }
}