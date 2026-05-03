using Hexiv.Common.Listeners;
using MessagePack;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Net;
using NatsROS.Core;
using NatsROS.Core.Communication;
using NatsROS.Hosting;
using NatsROS.Messages.ATE;
using OpenTap;
using System;
using System;
using System.IO;
using System.Threading;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks;
using NatsROS.Core.Attributes;

namespace NatsROS.OpenTAP.Plugins
{
    // ==========================================
    // Layer 2: 柔性多智能体流程执行引擎 (无头节点)
    // 它可以被无限多开！每个实例负责一条独立的业务流。
    // ==========================================
    [RosNodeAttribute(DisplayName = "OpenTAP 流程引擎", Category = "业务执行层 (Layer 2)", Description = "加载 TapPlan 文件并在后台静默执行测试流程")]
    public class OpenTapEngineNode(INatsClient nats, string nodeName, ILogger<OpenTapEngineNode> logger)
        : HostedRosNode(nats, nodeName, logger)
    {
        [RosPropAttribute(DisplayName = "测试计划路径", DefaultValue = "C:\\temp\\default.TapPlan", Description = "需要执行的 .TapPlan 文件绝对路径")]
        public string plan_path { get; set; }

        [RosPropAttribute(DisplayName = "动作服务路由", DefaultValue = "engine.test.run", Description = "HMI将通过此地址呼叫本引擎")]
        public string action_name { get; set; }

        private RosActionServer<RunPlanGoal, RunPlanFeedback, RunPlanResult>? _actionServer;

        // 1. 资源配置阶段 (Unconfigured -> Inactive)
        protected override async Task OnConfigureAsync(CancellationToken ct)
        {
            // 从参数服务器读取配置
            plan_path = Parameters.GetLocal("plan_path", "");
            action_name = Parameters.GetLocal("action_name", $"engine.{Name}.run");

            if (string.IsNullOrEmpty(plan_path) || !File.Exists(plan_path))
            {
                // 如果文件不存在，直接抛出异常！
                // 基类的 StartAsync 捕获到后，会立刻将节点切为 Faulted 状态，触发母体的自愈策略！
                throw new FileNotFoundException($"引擎初始化失败！未找到指定的 TapPlan 文件: {plan_path}");
            }

            // 极其耗时的操作放在 Configure 阶段预热
            Logger.LogInformation("正在预热 OpenTAP 插件环境...");

            await Task.Run(() => { try { PluginManager.Search(); } catch { } }, ct);

            // 预先建立好 Action 服务端，但此时还没有进入 Execute，所以不监听网络
            _actionServer = CreateActionServer<RunPlanGoal, RunPlanFeedback, RunPlanResult>(action_name);

            Logger.LogInformation("⚙️ OpenTAP 无头引擎配置完毕。Action: [{Action}], 配方: [{Plan}]", action_name, Path.GetFileName(plan_path));
        }

        // 2. 核心执行阶段 (只有进入 Active 状态后，才真正开始接管网络流)
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (_actionServer == null) return;

            Logger.LogInformation("🚀 OpenTAP 引擎已激活，正式开始监听大脑调度指令...");

            await _actionServer.ServeAsync(async (goal, feedback, cancelToken) =>
            {
                Logger.LogInformation("🚀 收到调度指令！开始执行产品: {Barcode}...", goal.Barcode);
                var tcs = new TaskCompletionSource<TestPlanRun>();
                TestPlan? plan = null;

                try
                {
                    plan = TestPlan.Load(plan_path);
                }
                catch (Exception ex)
                {
                    return new RunPlanResult(false, "Error", $"加载失败: {ex.Message}");
                }

                var executionThread = TapThread.Start(() =>
                {
                    using var reg = cancelToken.Register(() =>
                    {
                        Logger.LogWarning("🛑 收到分布式急停指令！触发 TapThread.Abort()...");
                        TapThread.Current.Abort();
                    });

                    try
                    {
                        var natsListener = new NatsRosResultListener(feedback, Logger);
                        var finalResult = plan.Execute(new List<IResultListener> { natsListener });
                        tcs.TrySetResult(finalResult);
                    }
                    catch (ThreadAbortException) { tcs.TrySetCanceled(); }
                    catch (Exception ex)
                    {
                        if (ex is OperationCanceledException) tcs.TrySetCanceled();
                        else tcs.TrySetException(ex);
                    }
                });

                try
                {
                    var planRun = await tcs.Task;
                    bool isPassed = planRun.Verdict == Verdict.Pass;
                    Logger.LogInformation("🏁 流程执行结束。最终判定: {Verdict}", planRun.Verdict);
                    return new RunPlanResult(isPassed, planRun.Verdict.ToString(), "完成");
                }
                catch (OperationCanceledException) { return new RunPlanResult(false, "Canceled", "被中止"); }
                catch (Exception ex) { return new RunPlanResult(false, "Error", ex.Message); }

            }, stoppingToken);
        }

        // 3. 清理阶段 (Active/Faulted -> Unconfigured)
        protected override Task OnCleanupAsync(CancellationToken ct)
        {
            Logger.LogInformation("🧹 正在清理 OpenTAP 引擎占用的内存资源...");
            // 如果有需要释放的 COM 对象或者大内存，放在这里
            return Task.CompletedTask;
        }
    }
}
