using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Dynamic;
using System.Runtime.CompilerServices;

namespace NatsROS.Dashboard.Models
{
    public class BagTopicInfo : INotifyPropertyChanged
    {
        private bool _isPlayEnabled = true;
        public string TopicName { get; set; } = "";
        public int MessageCount { get; set; }
        public bool IsPlayEnabled { get => _isPlayEnabled; set { _isPlayEnabled = value; OnPropertyChanged(); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class DynamicParameterObject : DynamicObject
    {
        public Dictionary<string, string> Properties { get; } = new();
        public override bool TryGetMember(GetMemberBinder binder, out object? result)
        {
            if (Properties.TryGetValue(binder.Name, out var val)) { result = val; return true; }
            result = null; return false;
        }
        public override bool TrySetMember(SetMemberBinder binder, object? value)
        {
            Properties[binder.Name] = value?.ToString() ?? ""; return true;
        }
        public override IEnumerable<string> GetDynamicMemberNames() => Properties.Keys;
    }

    public class NodeItem { public string NodeName { get; set; } = ""; }

    public class TopicMonitorItem : INotifyPropertyChanged
    {
        private double _hz; private int _lastSize; private DateTime _lastActive; private long _totalMessages;
        private bool _isMonitored; private string _liveValue = ""; private string _messageTypeName = "未知 (Unknown)";

        public string TopicName { get; set; } = "";
        public string MessageTypeName { get => _messageTypeName; set { _messageTypeName = value; OnPropertyChanged(); } }
        internal Type? RealType { get; set; }
        public bool IsMonitored { get => _isMonitored; set { if (_isMonitored != value) { _isMonitored = value; OnPropertyChanged(); } } }
        public string LiveValue { get => _liveValue; set { _liveValue = value; OnPropertyChanged(); } }
        public long TotalMessages { get => _totalMessages; set { _totalMessages = value; OnPropertyChanged(); } }
        public double Hz { get => _hz; set { _hz = value; OnPropertyChanged(); } }
        public int LastSize { get => _lastSize; set { _lastSize = value; OnPropertyChanged(); } }
        public DateTime LastActive { get => _lastActive; set { _lastActive = value; OnPropertyChanged(); } }
        internal long MessagesInCurrentSecond { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class RosMessageTypeInfo
    {
        public string FullName { get; set; } = "";
        public Type? Type { get; set; }
    }

    // ==========================================
    // 用于动态注入下拉框绑定的数据模型
    // ==========================================
    public class AvailableNodeInfo
    {
        public string AssemblyName { get; set; } = "";
        public string TypeName { get; set; } = "";
        // 在 UI 下拉框里显示的漂亮名字
        public string DisplayName => $"{TypeName} [{AssemblyName}.dll]";
    }
}
