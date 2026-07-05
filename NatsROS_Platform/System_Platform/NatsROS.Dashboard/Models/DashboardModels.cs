using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Dynamic;
using System.Runtime.CompilerServices;

namespace NatsROS.Dashboard.Models
{
    // ==========================================
    // 用于“录制面板”的动态话题模型
    // ==========================================
    public class RecordTopicItem : INotifyPropertyChanged
    {
        private bool _isRecordEnabled = true;
        private int _recordedCount = 0;
        private string _topicType = "未知";
        public string TopicType { get => _topicType; set { _topicType = value; OnPropertyChanged(); } } // 【新增】
        public string TopicName { get; set; } = "";

        // 是否允许录制入盘
        public bool IsRecordEnabled
        {
            get => _isRecordEnabled;
            set { if (_isRecordEnabled != value) { _isRecordEnabled = value; OnPropertyChanged(); } }
        }

        // 实时录制帧数
        public int RecordedCount
        {
            get => _recordedCount;
            set { _recordedCount = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class BagTopicInfo : INotifyPropertyChanged
    {
        private bool _isPlayEnabled = true;
        private string _topicType = "未知";
        public string TopicType { get => _topicType; set { _topicType = value; OnPropertyChanged(); } } // 【新增】
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

    public class NodeItem : INotifyPropertyChanged
    {
        private string _stateStr = "";
        public string NodeName { get; set; } = "";
        public byte StateCode { get; set; } // 0:Uncfg, 1:Inactive, 2:Active, 3:Faulted
        public string StateStr { get => _stateStr; set { _stateStr = value; OnPropertyChanged(); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

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
        public string DisplayName { get; set; } = ""; // 从标签读取
        public string Category { get; set; } = "";    // 从标签读取
        public string Description { get; set; } = ""; // 从标签读取

        public string FullDisplayName => string.IsNullOrEmpty(DisplayName) ? TypeName : DisplayName;
    }

    // 2. 动态参数的 Key-Value 模型 (用于支持 UI 动态增删改)
    public class RecipeParamItem : INotifyPropertyChanged
    {
        private string _value = "";
        public string Key { get; set; } = "";
        public string Value { get => _value; set { _value = value; OnPropertyChanged(); } }
        public string Description { get; set; } = ""; // 【新增】描述
        public string Category { get; set; } = "Misc"; // 【新增】：参数分类

        public bool IsFilePath { get; set; }
        public string FileFilter { get; set; } = "All Files (*.*)|*.*";

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ==========================================
    // 用于“可视化编排器”的配方模型
    // ==========================================
    public class RecipeNodeItem : INotifyPropertyChanged
    {
        private byte _restartPolicy = 1; // 默认 1=OnFailure
        private int _maxRetries = 3;
        private int _restartDelaySeconds = 5;

        public string NodeName { get; set; } = "";
        public string AssemblyName { get; set; } = "";
        public string TypeName { get; set; } = "";
        public Dictionary<string, string> Parameters { get; set; } = new();

        public byte RestartPolicy { get => _restartPolicy; set { _restartPolicy = value; OnPropertyChanged(); } }
        public int MaxRetries { get => _maxRetries; set { _maxRetries = value; OnPropertyChanged(); } }
        public int RestartDelaySeconds { get => _restartDelaySeconds; set { _restartDelaySeconds = value; OnPropertyChanged(); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class LaunchProfile
    {
        public List<RecipeNodeItem> Nodes { get; set; } = new();
    }
}
