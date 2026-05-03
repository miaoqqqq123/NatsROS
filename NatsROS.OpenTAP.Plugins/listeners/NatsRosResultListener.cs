using Microsoft.Extensions.Logging;
using NatsROS.Messages.ATE;
using OpenTap;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Hexiv.Common.Listeners
{
    /// <summary>
    /// 一个上下文感知的执行监听器。
    /// 它的实例应该为单次TestPlan.Execute调用而创建。
    /// </summary>
    [Display("OpenTAP 专用的 NatsROS 结果监听器")]
    [Browsable(false)] // 不在OpenTAP的设置对话框中显示
    public class NatsRosResultListener : ResultListener
    {
        private readonly Action<RunPlanFeedback> _feedbackPublisher;
        private readonly ILogger _logger;

        /// <summary>
        /// 获取此监听器实例所关联的执行上下文。
        /// </summary>
        public ExecutionContext Context { get; }

        /// <summary>
        /// 构造函数，在创建时必须指定其上下文。
        /// </summary>
        public NatsRosResultListener(Action<RunPlanFeedback> feedbackPublisher, ILogger logger)
        {
            _feedbackPublisher = feedbackPublisher;
            _logger = logger;
            Name = "NatsROS_Feedback_Listener";
        }

        #region IResultListener Overrides (事件捕获与转发)

        public override void OnTestStepRunStart(TestStepRun stepRun)
        {
            _logger.LogDebug("➡️ 步骤开始: {StepName}", stepRun.TestStepName);
            // 推送进度到 NATS 网络
            _feedbackPublisher(new RunPlanFeedback(stepRun.TestStepName, "Running", 0.0));
            base.OnTestStepRunStart(stepRun);
        }

        public override void OnTestStepRunCompleted(TestStepRun stepRun)
        {
            _logger.LogDebug("✔️ 步骤结束: {StepName} -> {Verdict}", stepRun.TestStepName, stepRun.Verdict);
            // 推送进度到 NATS 网络
            _feedbackPublisher(new RunPlanFeedback(stepRun.TestStepName, stepRun.Verdict.ToString(), 100.0));
            base.OnTestStepRunCompleted(stepRun);
        }
        #endregion

    }
}
