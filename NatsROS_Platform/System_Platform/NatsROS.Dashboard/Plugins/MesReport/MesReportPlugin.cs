using System;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NatsROS.Dashboard.Infrastructure;

namespace NatsROS.Dashboard.Plugins.MesReport
{
    public class MesReportPlugin : IDashboardPlugin
    {
        public string RibbonCategory => "全网监控 (Monitoring)";
        public string DisplayName => "MES 生产报表大屏";
        public string GlyphPath => "SvgImages/Dashboards/Chart.svg";

        public object CreateView(IServiceProvider serviceProvider) => new MesReportView(serviceProvider.GetRequiredService<INatsClient>());
    }
}