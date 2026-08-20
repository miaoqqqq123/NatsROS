using System;

namespace NatsROS.Core.UI
{
    /// <summary>
    /// 标准操作员 HMI 插件契约
    /// </summary>
    public interface IHmiPlugin
    {
        /// <summary>
        /// 在 HMI 选项卡上显示的名称 (如 "主操作台", "IO 监控")
        /// </summary>
        string DisplayName { get; }

        /// <summary>
        /// 排序权重 (数字越小越靠左)
        /// </summary>
        int OrderIndex { get; }

        /// <summary>
        /// 实例化具体的 UI 控件 (返回 object 是为了完美兼容 WPF UserControl 和 WinForms Control)
        /// </summary>
        object CreateView(IServiceProvider serviceProvider);
    }
}