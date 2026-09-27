using System;

namespace NatsROS.Core.Attributes;

// ==========================================
// 【新增】：参数作用域定义
// ==========================================
public enum RosPropScope : byte
{
    Both = 0,    // 混合参数：两端都显示
    Launch = 1,  // 拓扑与身份：仅开机配方显示，绝对禁止热更
    Runtime = 2  // 物理与机电标定：仅运行时大盘显示，不污染开机文件
}

// ==========================================
// 1. 节点类级别的描述标签
// ==========================================
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public class RosNodeAttribute : Attribute
{
    public string DisplayName { get; set; } = "未知节点";
    public string Description { get; set; } = "暂无描述";
    public string Category { get; set; } = "默认分类";
}

// ==========================================
// 2. 参数属性级别的描述标签 (用于未来 UI 自动生成表单)
// ==========================================
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public class RosPropAttribute : Attribute
{
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public string DefaultValue { get; set; } = "";
    public string Category { get; set; } = "Common"; 
    public double Min { get; set; } = double.MinValue;
    public double Max { get; set; } = double.MaxValue;

    public RosPropScope Scope { get; set; } = RosPropScope.Both;
}

[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public class ParameterScopeAttribute : Attribute
{
    public RosPropScope Scope { get; }
    public ParameterScopeAttribute(RosPropScope scope) => Scope = scope;
}

// ==========================================
// UI 渲染提示标签：告诉 Dashboard 这个字符串是一个文件路径
// ==========================================
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public class FilePathAttribute : Attribute
{
    // 文件过滤器，比如 "XML Files (*.xml)|*.xml"
    public string Filter { get; set; }

    public FilePathAttribute(string filter = "All Files (*.*)|*.*")
    {
        Filter = filter;
    }
}


// ==========================================
// 用于 DLL 级别的极速过滤安检标签
// ==========================================
[AttributeUsage(AttributeTargets.Assembly)]
public class ContainsNatsRosAlarmsAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Assembly)]
public class ContainsNatsRosPermissionsAttribute : Attribute { }


// ==========================================
// 用于标识这是一个需要弹出 IO 标签选择器的属性
// ==========================================
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public class IoTagSelectorAttribute : Attribute { }