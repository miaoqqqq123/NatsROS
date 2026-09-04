namespace NatsROS.Core.UI
{
    /// <summary>
    /// 标准 Dashboard 插件契约
    /// </summary>
    public interface IDashboardPlugin
    {
        /// <summary>
        /// Ribbon 菜单的页面名 (例如: "诊断工具", "控制面板")
        /// </summary>
        string RibbonPage { get; }

        /// <summary>
        /// Ribbon 菜单的分组名 (例如: "话题雷达", "拓扑图")
        /// </summary>
        string RibbonGroup { get; }

        /// <summary>
        /// 插件的显示名称 (例如: "话题雷达", "拓扑图")
        /// </summary>
        string DisplayName { get; }

        /// <summary>
        /// 插件在 Ribbon 上的图标 (例如: "SvgImages/Icon Builder/Security_Visibility.svg")
        /// </summary>
        string GlyphPath { get; }

        /// <summary>
        /// 是否允许在同一时间打开多个实例 (例如: "话题雷达" 只允许一个, "拓扑图" 可以开多个)
        /// </summary>
        bool AllowMultiple => false;

        /// <summary>
        /// 工厂方法：当用户点击菜单时，外壳会调用此方法，要求插件生成自己的 UI 界面
        /// </summary>
        /// <param name="serviceProvider"></param>
        /// <returns></returns>
        object CreateView(IServiceProvider serviceProvider);
    }

    /// <summary>
    /// 状态备忘录接口 (Stateful View)
    /// 任何想要在软件重启后记住自己参数的 UserControl，只需实现此接口！
    /// </summary>
    public interface IStatefulView
    {
        /// <summary>
        /// 当系统保存布局时调用，插件将自己的核心状态打包成字符串（推荐用 JSON）交出
        /// </summary>
        string SaveState();

        /// <summary>
        /// 当系统恢复布局时调用，插件拿到当年的字符串，自行恢复界面
        /// </summary>
        void RestoreState(string state);
    }
}
