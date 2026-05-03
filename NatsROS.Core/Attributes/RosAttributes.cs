using System;

namespace NatsROS.Core.Attributes;

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
// ==========================================[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public class RosPropAttribute : Attribute
{
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public string DefaultValue { get; set; } = "";
    public string Category { get; set; } = "Common"; 
    public double Min { get; set; } = double.MinValue;
    public double Max { get; set; } = double.MaxValue;
}

// ==========================================
// UI 渲染提示标签：告诉 Dashboard 这个字符串是一个文件路径
// ==========================================[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public class FilePathAttribute : Attribute
{
    // 文件过滤器，比如 "XML Files (*.xml)|*.xml"
    public string Filter { get; set; }

    public FilePathAttribute(string filter = "All Files (*.*)|*.*")
    {
        Filter = filter;
    }
}