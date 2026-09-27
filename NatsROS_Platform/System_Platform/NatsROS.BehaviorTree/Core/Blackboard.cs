using System.Collections.Concurrent;

namespace NatsROS.BehaviorTree.Core
{
    public class Blackboard
    {
        private readonly ConcurrentDictionary<string, object> _storage = new();

        public void Set<T>(string key, T value)
        {
            if (value == null) return;
            _storage[key] = value;
        }

        // ==========================================
        // 模式 1：安全尝试获取 (TryGet 模式，原有逻辑保持不变)
        // 适合获取数值，例如坐标、角度等
        // ==========================================
        public bool Get<T>(string key, out T value)
        {
            if (_storage.TryGetValue(key, out var rawValue) && rawValue is T typedValue)
            {
                value = typedValue;
                return true;
            }
            value = default!;
            return false;
        }

        // ==========================================
        // 模式 2：直接获取引用 (流畅模式 Fluent Pattern) 【新增】
        // 适合获取 NATS 客户端等全局服务，方便使用 ?? throw
        // ==========================================
        public T? Get<T>(string key)
        {
            if (_storage.TryGetValue(key, out var rawValue) && rawValue is T typedValue)
            {
                return typedValue;
            }
            return default;
        }

        // ==========================================
        // 模式 3：检查键是否存在 【顺手新增】
        // ==========================================
        public bool HasKey(string key) => _storage.ContainsKey(key);
    }
}