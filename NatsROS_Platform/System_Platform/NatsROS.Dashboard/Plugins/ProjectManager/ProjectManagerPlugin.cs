using System;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NatsROS.Dashboard.Infrastructure;

namespace NatsROS.Dashboard.Plugins.ProjectManager
{
    public class ProjectManagerPlugin : IDashboardPlugin
    {
        public string RibbonCategory => "系统核心 (System Core)";
        public string DisplayName => "方案与工程管家 (Solutions)";
        // 使用一个代表打包/箱子的图标
        public string GlyphPath => "SvgImages/Spreadsheet/PrintEntireWorkbook.svg";

        public object CreateView(IServiceProvider serviceProvider) => new ProjectManagerView(serviceProvider.GetRequiredService<INatsClient>());
    }
}
