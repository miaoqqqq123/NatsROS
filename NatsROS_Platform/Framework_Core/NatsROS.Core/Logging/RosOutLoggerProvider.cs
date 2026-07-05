using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Net;
using NatsROS.Core.Communication;
using NatsROS.Core.SystemMessages;


namespace NatsROS.Core.Logging;

/// <summary>
/// 这是一个无缝潜入 .NET 底层日志系统的拦截器。
/// 它负责把局部的 ILogger 日志，偷取出来并打包成标准的 LogMsg 发送到 NATS。
/// </summary>
public class RosOutLoggerProvider(INatsClient nats) : ILoggerProvider
{
    private readonly RosPublisher<LogMsg> _rosoutPublisher = new(nats, "rosout", RosQosProfile.SensorData);

    public ILogger CreateLogger(string categoryName)
    {
        return new RosOutLogger(categoryName, _rosoutPublisher);
    }

    public void Dispose() { }

    private class RosOutLogger(string categoryName, RosPublisher<LogMsg> publisher) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            // 将 .NET 的日志级别映射为 ROS 标准级别
            byte rosLevel = logLevel switch
            {
                LogLevel.Trace or LogLevel.Debug => RosLogLevels.Debug,
                LogLevel.Information => RosLogLevels.Info,
                LogLevel.Warning => RosLogLevels.Warn,
                LogLevel.Error => RosLogLevels.Error,
                LogLevel.Critical => RosLogLevels.Fatal,
                _ => RosLogLevels.Info
            };

            var message = formatter(state, exception);

            // ==========================================
            // 1. 本地终端兜底打印 (恢复屏幕输出)
            // ==========================================
            ConsoleColor color = rosLevel switch
            {
                RosLogLevels.Warn => ConsoleColor.Yellow,
                RosLogLevels.Error or RosLogLevels.Fatal => ConsoleColor.Red,
                _ => ConsoleColor.Gray
            };
            Console.ForegroundColor = color;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [{categoryName}] {message}");
            Console.ResetColor();

            // ==========================================
            // 2. 网络全局广播
            // ==========================================
            var logMsg = new LogMsg(
                Stamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Level: rosLevel,
                Name: categoryName,
                Msg: message
            );

            // 丢给后台发送，不阻塞正常的业务流
            _ = publisher.PublishAsync(logMsg).AsTask().ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[严重错误] rosout 广播失败: {t.Exception?.InnerException?.Message}");
                    Console.ResetColor();
                }
            });
        }
    }
}
