using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using MessagePack;
using NatsROS.Core.Motion;

namespace NatsROS.Messages.Motion;


/// <summary>
/// 点位来源枚举：机器公共点 还是 产品工艺点
/// </summary>
public enum PointSource : byte
{
    Machine = 0, // 机台公共点 (从系统参数服务器获取，纯物理绝对坐标)
    Product = 1  // 产品工艺点 (从当前产品配方获取，自动叠加视觉仿射变换)
}

/// <summary>
/// 多态基类：所有空间特征数据类型的祖先
/// .NET 8 JSON 存盘多态支持 (SQLite / Parameter Server)
/// MessagePack 二进制传输多态支持
/// </summary>
[Union(0, typeof(SinglePointModel))]
[Union(1, typeof(TrajectoryModel))]
[JsonDerivedType(typeof(SinglePointModel), typeDiscriminator: "Single")]
[JsonDerivedType(typeof(TrajectoryModel), typeDiscriminator: "Trajectory")]
public abstract class PointFeatureBase : INotifyPropertyChanged
{
    private string _name = "NewPoint";
    private PointSource _source = PointSource.Product;

    [Key(0)]
    [Category("1. 基础信息")]
    [DisplayName("点位/轨迹名称")]
    public string Name { get => _name; set { _name = value; OnPropertyChanged(); } }

    [Key(1)]
    [Category("1. 基础信息")]
    [DisplayName("坐标参考系来源")]
    public PointSource Source { get => _source; set { _source = value; OnPropertyChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 子类 A：离散单点 (支持示教接口)
/// </summary>
[MessagePackObject]
public class SinglePointModel : PointFeatureBase, ITeachable
{
    private double _x, _y, _z, _speed = 50.0;

    [Key(2)]
    [Category("2. 空间坐标 (mm)")]
    public double X { get => _x; set { _x = value; OnPropertyChanged(); } }

    [Key(3)]
    [Category("2. 空间坐标 (mm)")]
    public double Y { get => _y; set { _y = value; OnPropertyChanged(); } }

    [Key(4)]
    [Category("2. 空间坐标 (mm)")] 
    public double Z { get => _z; set { _z = value; OnPropertyChanged(); } }

    [Key(5)]
    [Category("3. 运动参数")]
    [DisplayName("移动速度 (mm/s)")]
    public double Speed { get => _speed; set { _speed = value; OnPropertyChanged(); } }

    // 【核心】：实现示教接口
    public void Teach(params double[] machineCoords)
    {
        if (machineCoords.Length >= 3)
        {
            // 抓取传进来的 X, Y, Z，并四舍五入保留 3 位小数 (1微米精度)
            X = Math.Round(machineCoords[0], 3);
            Y = Math.Round(machineCoords[1], 3);
            Z = Math.Round(machineCoords[2], 3);
        }
    }
}

/// <summary>
/// 子类 B：连续轨迹节点 (Trajectory Node)
/// </summary>
[MessagePackObject]
public class TrajectoryNode : INotifyPropertyChanged
{
    private double _x, _y, _z, _speed = 50.0;
    private bool _isDispense;

    [Key(0)]
    [DisplayName("X (mm)")] 
    public double X { get => _x; set { _x = value; OnPropertyChanged(); } }

    [Key(1)]
    [DisplayName("Y (mm)")] 
    public double Y { get => _y; set { _y = value; OnPropertyChanged(); } }

    [Key(2)]
    [DisplayName("Z (mm)")] 
    public double Z { get => _z; set { _z = value; OnPropertyChanged(); } }

    [Key(3)]
    [DisplayName("段速度")] 
    public double Speed { get => _speed; set { _speed = value; OnPropertyChanged(); } }

    [Key(4)]
    [DisplayName("开启点胶")] 
    public bool IsDispense { get => _isDispense; set { _isDispense = value; OnPropertyChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 子类 B：连续轨迹集合
/// </summary>
[MessagePackObject]
public class TrajectoryModel : PointFeatureBase
{
    [Key(2)]
    [Category("2. 轨迹路径点集合")]
    [DisplayName("路径节点序列 (Nodes)")]
    // 采用 ObservableCollection，确保在大屏 PropertyGrid 里增删点位时 UI 实时刷新！
    public ObservableCollection<TrajectoryNode> Nodes { get; set; } = new();
}


/// <summary>
/// 离散命名单点 (用于锁螺丝、扫码、拍照、排胶)
/// </summary>
/// <param name="Name"></param>
/// <param name="X"></param>
/// <param name="Y"></param>
/// <param name="Z"></param>
/// <param name="Speed"></param>
[MessagePackObject]
public record NamedPoint(
    [property: Key(0)] string Name,
    [property: Key(1)] double X,
    [property: Key(2)] double Y,
    [property: Key(3)] double Z,
    [property: Key(4)] double Speed = 50.0
);


/// <summary>
/// 连续轨迹点 (用于涂胶、激光打标)
/// </summary>
/// <param name="X"></param>
/// <param name="Y"></param>
/// <param name="Z"></param>
/// <param name="Speed"></param>
/// <param name="IsDispense"></param>
[MessagePackObject]
public record TrajectoryPoint(
    [property: Key(0)] double X,
    [property: Key(1)] double Y,
    [property: Key(2)] double Z,
    [property: Key(3)] double Speed,
    [property: Key(4)] bool IsDispense // 走到这个点时，胶阀/激光是否开启
);

[MessagePackObject]
public record NamedTrajectory(
    [property: Key(0)] string Name,
    [property: Key(1)] TrajectoryPoint[] Points
);