using NatsROS.Core.SystemMessages;
using MessagePack;
using Microsoft.VisualBasic;
using NATS.Client.Core;
using NATS.Net;
using NatsROS.Core;
using NatsROS.Core.Serialization;
using System;
using System.Threading;
using System.Threading.Tasks;
using NatsROS.Messages.StdMsgs;


namespace NatsROS.Cli
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            // 如果没有参数，打印帮助信息
            if (args.Length == 0)
            {
                PrintHelp();
                return;
            }

            // 初始化 NATS 客户端
            var options = NatsOpts.Default with
            {
                Url = "nats://127.0.0.1:4222",
                SerializerRegistry = new NatsRosSerializerRegistry()
            };

            await using var nats = new NatsClient(options);
            try
            {
                await nats.ConnectAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] 无法连接到 NATS 服务器: {ex.Message}");
                return;
            }

            // 解析命令行参数 (类似 ros2 node list)
            var command = args[0].ToLower();
            var subCommand = args.Length > 1 ? args[1].ToLower() : "";

            if (command == "node" && subCommand == "list")
            {
                await ExecuteNodeListAsync(nats);
            }
            else if (command == "topic" && subCommand == "echo" && args.Length > 2)
            {
                var topicName = args[2].ToLower();
                await ExecuteTopicEchoAsync(nats, topicName);
            }
            else if (command == "container" && subCommand == "list")
            {
                await ExecuteContainerListAsync(nats);
            }
            else if (command == "container" && subCommand == "load" && args.Length == 5)
            {
                await ExecuteContainerLoadAsync(nats, args[2], args[3], args[4]);
            }  
            else
            {
                Console.WriteLine($"未知的命令或参数不足: {string.Join(" ", args)}");
                PrintHelp();
            }
        }

        // ==========================================
        // 远程控制母体的方法
        // ==========================================
        private static async Task ExecuteContainerLoadAsync(INatsClient nats, string assembly, string type, string name)
        {
            Console.WriteLine($"正在请求母体加载节点: [{name}]...");
            try
            {
                // 我们创建一个临时的 ServiceClient 去呼叫 "container.load_node"
                var req = new LoadNodeReq(assembly, type, name);
                var res = await nats.RequestAsync<LoadNodeReq, LoadNodeRes>("container.load_node", req, replyOpts: new NatsSubOpts { Timeout = TimeSpan.FromSeconds(5) });

                if (res.Data != null && res.Data.Success)
                    Console.WriteLine($"✅ 母体回复: {res.Data.Message}");
                else
                    Console.WriteLine($"❌ 母体报错: {res.Data?.Message ?? "无响应"}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"RPC 调用失败 (母体可能未启动): {ex.Message}");
            }
        }

        private static async Task ExecuteContainerListAsync(INatsClient nats)
        {
            Console.WriteLine("正在查询母体内运行的节点列表...");
            try
            {
                var res = await nats.RequestAsync<ListNodesReq, ListNodesRes>("container.list_nodes", new ListNodesReq(), replyOpts: new NatsSubOpts { Timeout = TimeSpan.FromSeconds(3) });
                if (res.Data != null)
                {
                    Console.WriteLine($"\n母体当前承载了 {res.Data.NodeNames.Length} 个节点：");
                    foreach (var n in res.Data.NodeNames)
                    {
                        Console.WriteLine($"  - {n}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"RPC 调用失败: {ex.Message}");
            }
        }

        private static async Task ExecuteNodeListAsync(INatsClient nats)
        {
            Console.WriteLine("正在扫描网络中的活跃节点...\n");

            // 设置 1 秒的超时时间收集所有节点的响应
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

            int count = 0;
            try
            {
                // NATS.Net 的杀手锏：RequestManyAsync (一对多请求)
                // 向 natsros.discovery.ping 广播，所有活跃节点都会回复
                var replies = nats.Connection.RequestManyAsync<PingReq, NodeInfo>(
                    "natsros.discovery.ping",
                    new PingReq(Guid.NewGuid().ToString()),
                    cancellationToken: cts.Token);


                await foreach (var reply in replies)
                {
                    if (reply.Data != null)
                    {
                        Console.WriteLine($"  /natsros/node/{reply.Data.NodeName}");
                        count++;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 超时到达，结束收集
            }

            Console.WriteLine($"\n扫描完毕。共发现 {count} 个活跃节点。");
        }
        private static async Task ExecuteTopicEchoAsync(INatsClient nats, string topicName)
        {
            Console.WriteLine($"正在监听话题: '{topicName}' ... (按 Ctrl+C 退出)\n");

            try
            {
                if (topicName.Equals("rosout", StringComparison.OrdinalIgnoreCase))
                {
                    // 专为 rosout 开启的强类型通道
                    await foreach (var msg in nats.SubscribeAsync<NatsROS.Core.SystemMessages.LogMsg>(topicName))
                    {
                        if (msg.Data is not null)
                        {
                            PrintLogMsg(msg.Data);
                        }
                        else
                        {
                            Console.WriteLine("[警告] 收到了 rosout 数据，但反序列化为空！");
                        }
                    }
                }
                else
                {
                    // 其他话题默认用 StringMsg 尝试
                    await foreach (var msg in nats.SubscribeAsync<NatsROS.Messages.StdMsgs.StringMsg>(topicName))
                    {
                        if (msg.Data is not null)
                        {
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] data: '{msg.Data.Data}'");
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 优雅退出
            }
        }

        // 渲染标准日志的辅助方法 (确保你 CLI 里有这个方法)
        private static void PrintLogMsg(NatsROS.Core.SystemMessages.LogMsg log)
        {
            var time = DateTimeOffset.FromUnixTimeMilliseconds(log.Stamp).ToLocalTime();
            ConsoleColor color = log.Level switch
            {
                NatsROS.Core.SystemMessages.RosLogLevels.Debug => ConsoleColor.DarkGray,
                NatsROS.Core.SystemMessages.RosLogLevels.Info => ConsoleColor.Green,
                NatsROS.Core.SystemMessages.RosLogLevels.Warn => ConsoleColor.Yellow,
                NatsROS.Core.SystemMessages.RosLogLevels.Error => ConsoleColor.Red,
                NatsROS.Core.SystemMessages.RosLogLevels.Fatal => ConsoleColor.DarkRed,
                _ => ConsoleColor.White
            };
            Console.ForegroundColor = color;
            Console.WriteLine($"[{time:HH:mm:ss.fff}] [{(log.Level)}] [{log.Name}] {log.Msg}");
            Console.ResetColor();
        }

        private static void PrintHelp()
        {
            Console.WriteLine("NatsROS 命令行工具 (v2.0)");
            Console.WriteLine("用法: natsros <command> <subcommand> [args]");
            Console.WriteLine("\n可用命令:");
            Console.WriteLine("  node list               列出全网发现的节点");
            Console.WriteLine("  topic echo <topic>      实时打印话题的数据");
            Console.WriteLine("  container list          列出母体内正在运行的节点");
            Console.WriteLine("  container load <dll> <class> <name>  命令母体加载一个新节点");
        }


    }
}
