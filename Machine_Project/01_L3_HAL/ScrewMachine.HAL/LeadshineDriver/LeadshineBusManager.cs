using Microsoft.Extensions.Logging;

namespace ScrewMachine.HAL.LeadshineDriver
{
    /// <summary>
    /// 沙盒内的局部单例总线管家（母体对其一无所知）
    /// </summary>
    public static class LeadshineBusManager
    {
        private static LeadshineBusContext? _instance;
        private static int _referenceCount = 0;
        private static readonly object _lock = new();

        public static LeadshineBusContext GetContext() => _instance ?? throw new Exception("总线尚未初始化！");

        public static void RequestOpen(ILogger logger)
        {
            lock (_lock)
            {
                if (_instance == null) _instance = new LeadshineBusContext(logger, 0); // 假定卡号为0
                //if (_referenceCount == 0) _instance.OpenBus();
                _referenceCount++;
            }
        }

        public static void RequestClose()
        {
            lock (_lock)
            {
                _referenceCount--;
                if (_referenceCount <= 0 && _instance != null)
                {
                    //_instance.CloseBus();
                    _referenceCount = 0;
                }
            }
        }
    }
}