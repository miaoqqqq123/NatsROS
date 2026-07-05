using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NatsROS.Dashboard;

public class TopicMonitorItem : INotifyPropertyChanged
{
    private double _hz;
    private int _lastSize;
    private DateTime _lastActive;
    private long _totalMessages;

    public string TopicName { get; set; } = "";

    // 总消息数
    public long TotalMessages
    {
        get => _totalMessages;
        set { _totalMessages = value; OnPropertyChanged(); }
    }

    // 实时频率 (Hz)
    public double Hz
    {
        get => _hz;
        set { _hz = value; OnPropertyChanged(); }
    }

    // 最后一帧大小 (Bytes)
    public int LastSize
    {
        get => _lastSize;
        set { _lastSize = value; OnPropertyChanged(); }
    }

    // 最后活跃时间
    public DateTime LastActive
    {
        get => _lastActive;
        set { _lastActive = value; OnPropertyChanged(); }
    }

    // 内部计算辅助字段（不绑定到 UI）
    internal long MessagesInCurrentSecond { get; set; }

    public event PropertyPropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
