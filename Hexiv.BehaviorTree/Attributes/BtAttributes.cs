using System;

namespace Hexiv.BehaviorTree.Attributes
{
    // 用于标识这是一个可以被拖拽的行为树组件
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public class BtNodeAttribute : Attribute
    {
        public string DisplayName { get; set; } = "未知动作";
        public string Category { get; set; } = "默认";
        public string Description { get; set; } = "";
    }

    // 用于标识这是行为树节点上，可以在右侧属性栏里修改的参数[AttributeUsage(AttributeTargets.Property, Inherited = true)]
    public class BtPropAttribute : Attribute
    {
        public string DisplayName { get; set; } = "";
        public string DefaultValue { get; set; } = "";
        public string Description { get; set; } = "";
    }
}