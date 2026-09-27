using System;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NatsROS.Core.UI;

namespace NatsROS.Dashboard.Plugins.AlarmHud
{
    public class AlarmHudPlugin : IDashboardPlugin
    {
        public string RibbonPage => "系统核心 (System Core)";
        public string RibbonGroup => "平台管家 (Platform Managers)";
        public string DisplayName => "头条报警 HUD (Alarm HUD)";
        // 使用一个类似广播/喇叭的图标
        public string GlyphPath => "SvgImages/Icon Builder/Security_Warning.svg";

        public object CreateView(IServiceProvider serviceProvider)
        {
            var nats = serviceProvider.GetRequiredService<INatsClient>();
            return new AlarmHudView(nats);
        }
    }
}