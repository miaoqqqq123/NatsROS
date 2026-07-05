using System;

namespace NatsROS.Dashboard.Infrastructure
{
    /// <summary>
    /// 标准 Dashboard 插件契约
    /// </summary>
    public interface IDashboardPlugin
    {
        /// <summary>
        /// 1. Ribbon 菜单的分类名 (例如: "诊断工具", "控制面板")
        /// </summary>
        string RibbonCategory { get; }

        /// <summary>
        /// 2. 插件的显示名称 (例如: "话题雷达", "拓扑图")
        /// </summary>
        string DisplayName { get; }

        /// <summary>
        /// 3. 插件在 Ribbon 上的图标 (例如: "SvgImages/Icon Builder/Security_Visibility.svg")
        /// </summary>
        string GlyphPath { get; }

        /// <summary>
        /// 4. 工厂方法：当用户点击菜单时，外壳会调用此方法，要求插件生成自己的 UI 界面
        /// </summary>
        /// <param name="serviceProvider"></param>
        /// <returns></returns>
        object CreateView(IServiceProvider serviceProvider);
    }
}
