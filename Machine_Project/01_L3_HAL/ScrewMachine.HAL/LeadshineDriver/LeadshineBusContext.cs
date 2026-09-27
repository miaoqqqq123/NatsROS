using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using NatsROS.Messages.Motion;

namespace ScrewMachine.HAL.LeadshineDriver
{
    // ==========================================
    // 1. 业务级硬件异常
    // ==========================================
    public class HardwareException : Exception
    {
        public int ErrorCode { get; }
        public HardwareException(string message, int errorCode = 0) : base(message) { ErrorCode = errorCode; }
    }

    // ==========================================
    // 2. 极致榨取性能的非托管 P/Invoke 声明区
    // ==========================================
    // 【核心魔法 1】：安全验证抑制！
    // 告诉 .NET 引擎：跳过运行时的栈权限安全检查，将 C# 调 C++ 的开销从微秒级降至纳秒级！
    [SuppressUnmanagedCodeSecurity]
    internal static class NativeMethods
    {
        private const string DLL_NAME = "LTDMC.dll";

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.StdCall)]
        public static extern short dmc_board_init();

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.StdCall)]
        public static extern short dmc_board_close();

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.StdCall)]
        public static extern short dmc_pmove(ushort CardNo, ushort axis, int Dist, ushort posi_mode);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.StdCall)]
        public static extern int dmc_get_position(ushort CardNo, ushort axis);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.StdCall)]
        public static extern double dmc_read_current_speed(ushort CardNo, ushort axis);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.StdCall)]
        public static extern uint dmc_axis_io_status(ushort CardNo, ushort axis);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.StdCall)]
        public static extern short dmc_read_sevon_pin(ushort CardNo, ushort axis);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.StdCall)]
        public static extern short dmc_check_done(ushort CardNo, ushort axis);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.StdCall)]
        public static extern short dmc_write_outbit(ushort CardNo, ushort ioBit, ushort value);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.StdCall)]
        public static extern ushort dmc_read_inbit(ushort CardNo, ushort ioBit);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.StdCall)]
        public static extern short dmc_set_axis_enable(ushort CardNo, ushort axis);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.StdCall)]
        public static extern short dmc_set_axis_disable(ushort CardNo, ushort axis);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.StdCall)]
        public static extern short dmc_stop(ushort CardNo, ushort axis, ushort stop_mode);
    }

    // ==========================================
    // 3. 防撞机金牌：SafeHandle 生命周期托管
    // ==========================================
    // 【核心魔法 2】：如果 NatsROS 进程遭遇内存溢出等崩溃，
    // CLR 在杀死进程前，会强行召唤这个类的 ReleaseHandle 释放底层板卡！绝对防死锁！
    internal sealed class LeadshineBoardSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public LeadshineBoardSafeHandle() : base(true) { }

        protected override bool ReleaseHandle()
        {
            // 这是救命的一步：关闭底层驱动
            return NativeMethods.dmc_board_close() == 0;
        }

        // 用于强行伪造一个有效的句柄欺骗 GC
        public void MarkAsInitialized() => SetHandle(new IntPtr(1));
    }

    // ==========================================
    // 4. 雷赛 EtherCAT 总线上下文 (全局单例)
    // ==========================================
    public class LeadshineBusContext : IDisposable
    {
        private readonly ILogger _logger;
        private readonly ushort _cardId;
        private LeadshineBoardSafeHandle? _boardHandle; // 受 GC 保护的句柄
        private Task? _statePumpTask;
        private CancellationTokenSource _cts = new();

        public ConcurrentDictionary<ushort, AxisStateMsg> AxisStates { get; } = new();
        public ConcurrentDictionary<int, bool> IoStates { get; } = new();

        // 用于保护 C++ 调用的信号量
        public readonly SemaphoreSlim HardwareAccessLock = new(1, 1);

        public bool IsBusOpen => _boardHandle != null && !_boardHandle.IsClosed;

        public LeadshineBusContext(ILogger logger, ushort cardId = 0)
        {
            _logger = logger;
            _cardId = cardId;
        }

        public string OpenBus()
        {
            if (IsBusOpen) return "";

            try
            {
                HardwareAccessLock.Wait();
                try
                {
                    if (IsBusOpen) return "";

                    _boardHandle = new LeadshineBoardSafeHandle();

                    // 调用 C++ 初始化
                    short cardCount = NativeMethods.dmc_board_init();
                    if (cardCount <= 0)
                        throw new HardwareException($"雷赛板卡初始化失败或未找到板卡！返回: {cardCount}", cardCount);

                    // 初始化成功，注入免死金牌
                    _boardHandle.MarkAsInitialized();
                    _logger.LogInformation("✅ 雷赛 EtherCAT 总线 (检测到 {Count} 张卡，当前绑定卡号: {CardId}) 初始化成功。", cardCount, _cardId);

                    // 【核心魔法 3】：启动 .NET 8 专属高精度状态泵
                    _cts = new CancellationTokenSource();
                    _statePumpTask = Task.Run(() => StatePumpLoopAsync(_cts.Token));
                }
                finally
                {
                    HardwareAccessLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 雷赛 EtherCAT 总线打开失败");
                return ex.Message;
            }
            return "";
        }

        public string CloseBus()
        {
            if (!IsBusOpen) return "";

            try
            {
                HardwareAccessLock.Wait();
                try
                {
                    if (!IsBusOpen) return "";

                    _cts.Cancel();
                    _statePumpTask?.Wait(1000); // 优雅等待状态泵结束

                    // 调用 SafeHandle 的主动释放，它会安全地调用 dmc_board_close
                    _boardHandle?.Dispose();
                    _boardHandle = null;

                    _logger.LogInformation("⏹️ 雷赛 EtherCAT 总线已安全关闭并释放内存。");
                }
                finally
                {
                    HardwareAccessLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 雷赛 EtherCAT 总线关闭时异常");
                return ex.Message;
            }
            return "";
        }

        // ==========================================
        // 极致性能：无阻塞高精度状态泵
        // ==========================================
        private async Task StatePumpLoopAsync(CancellationToken token)
        {
            ushort maxAxes = 8;
            int maxIoPoints = 32;

            // 【核心革命】：摒弃 Thread.Sleep！使用 .NET 8 的高精度异步定时器
            // 2ms 一个轮回（500Hz），性能极强且不阻塞线程池！
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(2));

            try
            {
                while (await timer.WaitForNextTickAsync(token))
                {
                    await HardwareAccessLock.WaitAsync(token);
                    try
                    {
                        // 1. 扫描所有轴状态 (极限压榨 P/Invoke 速度)
                        for (ushort axis = 0; axis < maxAxes; axis++)
                        {
                            double pos = NativeMethods.dmc_get_position(_cardId, axis) / 10000.0;
                            double vel = NativeMethods.dmc_read_current_speed(_cardId, axis) / 10000.0;
                            uint ioStatus = NativeMethods.dmc_axis_io_status(_cardId, axis);

                            bool alm = (ioStatus & (0x01 << 0)) != 0; // MIO_ALM
                            bool svon = NativeMethods.dmc_read_sevon_pin(_cardId, axis) == 0;
                            bool isMoving = NativeMethods.dmc_check_done(_cardId, axis) == 0;

                            AxisStates[axis] = new AxisStateMsg(
                                ActualPosition: pos,
                                ActualVelocity: vel,
                                IsServoOn: svon,
                                IsMoving: isMoving,
                                IsAlarm: alm,
                                LimitPositive: (ioStatus & (0x01 << 1)) != 0, // MIO_PEL
                                LimitNegative: (ioStatus & (0x01 << 2)) != 0, // MIO_NEL
                                ErrorCode: alm ? (int)ioStatus : 0
                            );
                        }

                        // 2. 扫描所有 IO 状态
                        for (int ioBit = 0; ioBit < maxIoPoints; ioBit++)
                        {
                            IoStates[ioBit] = NativeMethods.dmc_read_inbit(_cardId, (ushort)ioBit) == 0;
                        }
                    }
                    finally
                    {
                        HardwareAccessLock.Release();
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 雷赛状态泵发生异常: {Msg}", ex.Message);
            }

            _logger.LogInformation("⏹️ 雷赛状态泵已停止运行。");
        }

        public void Dispose()
        {
            CloseBus();
        }
    }
}