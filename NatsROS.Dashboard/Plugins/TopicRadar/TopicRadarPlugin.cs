using System;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NatsROS.Dashboard.Infrastructure;

namespace NatsROS.Dashboard.Plugins.TopicRadar
{
    // 这个类就是我们一直寻找的“标准插件”，它实现了 IDashboardPlugin！
    public class TopicRadarPlugin : IDashboardPlugin
    {
        // 挂载到 Ribbon 的哪个选项卡下？
        public string RibbonCategory => "全网监控 (Monitoring)";

        // 按钮的名字
        public string DisplayName => "话题雷达 (Topic Radar)";

        // 按钮的图标
        public string GlyphPath => "SvgImages/Icon Builder/Security_Visibility.svg";

        // 外壳要界面时，我们 new 一个 View 交出去
        public object CreateView(IServiceProvider serviceProvider)
        {
            // 从全局 DI 容器中获取 NATS 核心，注入给雷达界面
            var nats = serviceProvider.GetRequiredService<INatsClient>();
            return new TopicRadarView(nats);
        }
    }
}
