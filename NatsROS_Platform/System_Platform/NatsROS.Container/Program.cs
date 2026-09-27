using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NatsROS.Container.Models;
using NatsROS.Hosting;
using NatsROS.KernelNodes;
using NLog.Extensions.Logging;
using System.Reflection;
using System.Text.Json;

namespace NatsROS.Container
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            // ==========================================
            // 【核心修复】：全局器官预加载 (Organ Preloading)
            // ==========================================
            try
            {
                string binPath = AppDomain.CurrentDomain.BaseDirectory;
                // 【绝杀】：使用 WorkspaceManager 获取当前沙盒的真实插件路径
                string workspacePluginsDir = Path.Combine(NatsROS.Core.Environment.WorkspaceManager.CurrentWorkspacePath, "Plugins");

                string[] searchDirs = {
                    binPath,
                    NatsROS.Core.Environment.WorkspaceManager.GetPluginsMessagesPath(), // 吃进跨端契约
                    NatsROS.Core.Environment.WorkspaceManager.GetPluginsNodesPath()     // 吃进后端驱动和动作
                };

                foreach (var dir in searchDirs)
                {
                    if (!Directory.Exists(dir)) continue;

                    // 只要不是微软、第三方基础库的 DLL，我们全部吸入内存！
                    var dllFiles = Directory.GetFiles(dir, "*.dll", SearchOption.TopDirectoryOnly);

                    foreach (var dll in dllFiles)
                    {
                        try
                        {
                            Assembly.LoadFrom(dll);
                        }
                        catch
                        {
                            /* 忽略无法加载的非托管或冲突 DLL */
                        }
                    }
                }
            }
            catch
            {
                Console.WriteLine("⚠️ 深度扫盘预热失败，将使用默认目录。");
            }

            // 1. 创建现代化的后台主机构建器
            var builder = Host.CreateApplicationBuilder(args);

            //// 【核心】：将其注册为 Windows 服务！(在 Linux 下可以无缝忽略，或者改为 Systemd)
            //builder.Services.AddWindowsService(options =>
            //{
            //    options.ServiceName = "NatsROS_Container_Engine";
            //});

            // ==========================================
            // 【核心修改】：接管微软的日志系统，替换为 NLog
            // ==========================================
            builder.Logging.ClearProviders(); // 清除微软默认的控制台输出，防止重复打印
            builder.Logging.AddNLog();        // 挂载 NLog (负责写本地文件和彩色屏幕)


            //builder.Logging.SetMinimumLevel(LogLevel.Debug);

            // 2. 注入 NatsROS 核心
            builder.AddNatsRos("nats://127.0.0.1:4222");

            // ==========================================
            // 3. 注册系统核心微服务 (Kernel Daemons)
            // 【架构升级】：将核心管家固化进母体 DNA，开机绝对自启，彻底告别手动修改 launch.json！
            // ==========================================
            builder.Services.AddSingleton<DynamicNodeManager>();

            // 母体控制器
            builder.AddRosNode<ContainerManagerNode>();

            // 身份与安全大管家 (不启动它，UI 无法登录)
            builder.AddRosNode<SecurityManagerNode>("auth");

            // 中央报警管理器 (不启动它，机器没有痛觉)
            builder.AddRosNode<AlarmManagerNode>("aem");

            // 配方与审计系统 (不启动它，无法一键换型)
            builder.AddRosNode<RecipeManagerNode>("rms");

            // MES 数据边缘网关 (默认常驻，收集生产数据)
            builder.AddRosNode<MesDatabaseNode>("mockmesnode");

            // 机台崩溃黑匣子 (永远在后台防爆窃听)
            builder.AddRosNode<BlackboxNode>("blackbox");

            // 4. 构建并启动！
            var host = builder.Build();

            Console.WriteLine("======================================================");
            Console.WriteLine("   NatsROS Container Engine (无头母体) 初始化完成     ");
            Console.WriteLine("   NatsROS Container Engine (带 NLog 本地持久化)      ");
            Console.WriteLine("   NatsROS Container Engine (带自动化配方启动)        ");
            Console.WriteLine("   NatsROS Container Engine (架构重组 V1.0)           ");
            Console.WriteLine("======================================================");

            // ==========================================
            // 【核心魔法】：开机自动读取 launch.json 并拉起产线环境！
            // ==========================================
            var nodeManager = host.Services.GetRequiredService<DynamicNodeManager>();
            var logger = host.Services.GetRequiredService<ILogger<Program>>();
            string launchFile = NatsROS.Core.Environment.WorkspaceManager.GetConfigPath("launch.json");

            if (File.Exists(launchFile))
            {
                logger.LogInformation("🚀 发现 launch.json 配方文件，正在自动组装产线环境...");
                try
                {
                    string json = File.ReadAllText(launchFile);
                    // 忽略大小写解析 JSON
                    var profile = JsonSerializer.Deserialize<LaunchProfile>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    if (profile != null)
                    {
                        foreach (var nodeConfig in profile.Nodes)
                        {
                            // 后台并发拉起每一个节点
                            _ = nodeManager.LoadNodeFromConfigAsync(nodeConfig);
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "❌ 解析 launch.json 失败！");
                }
            }
            else
            {
                logger.LogWarning("ℹ️ 未找到 launch.json，母体进入空载待命模式。");
            }

            await host.RunAsync();
        }
    }
}
