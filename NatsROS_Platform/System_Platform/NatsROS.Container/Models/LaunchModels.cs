using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NatsROS.Core.Lifecycle;


namespace NatsROS.Container.Models;

// ==========================================
// 用于解析 launch.json 的配置模型
// ==========================================
public class LaunchProfile
{
    public List<NodeLaunchConfig> Nodes { get; set; } = new();
}

public class NodeLaunchConfig
{
    public string NodeName { get; set; } = "";
    public string AssemblyName { get; set; } = "";
    public string TypeName { get; set; } = "";

    // 启动时需要灌入的默认参数
    public Dictionary<string, string> Parameters { get; set; } = new();

    // 节点的自愈策略 (0=Never, 1=OnFailure, 2=Always)
    public NodeRestartPolicy RestartPolicy { get; set; } = NodeRestartPolicy.Never;

    public int MaxRetries { get; set; } = 3;
    public int RestartDelaySeconds { get; set; } = 5;
}
