using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NatsROS.Core.UI;

namespace ScrewMachine.Dashboard.Plugins.MesReport
{
    public class MesReportPlugin : IDashboardPlugin
    {
        public string RibbonPage => "设备应用 (Device Apps)";
        public string RibbonGroup => "操作与监控 (Operation)";
        public string DisplayName => "MES 生产报表大屏";
        public string GlyphPath => "SvgImages/Dashboards/Chart.svg";

        public object CreateView(IServiceProvider serviceProvider) => new MesReportView(serviceProvider.GetRequiredService<INatsClient>());
    }
}