using System.Collections.Generic;

namespace NatsROS.Launcher
{
    public class SystemLaunchConfig
    {
        public string SystemName { get; set; } = "NatsROS System";

        // 关键：指明哪个进程是主界面进程（它关闭代表整个系统关闭）
        public string MainProcessId { get; set; } = "";

        public List<ProcessConfig> Processes { get; set; } = new();
    }

    public class ProcessConfig
    {
        public string Id { get; set; } = "";
        public string ExePath { get; set; } = "";
        public string Arguments { get; set; } = "";
        public bool Hidden { get; set; } = true;
        public bool AutoRestart { get; set; } = false;

        // 启动延迟（毫秒），例如母体需要等 NATS 先启动，HMI 需要等母体先启动
        public int BootDelayMs { get; set; } = 0;
    }
}