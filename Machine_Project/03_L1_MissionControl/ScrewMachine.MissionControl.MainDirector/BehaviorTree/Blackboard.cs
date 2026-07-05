using System.Collections.Concurrent;

namespace ScrewMachine.MissionControl.MainDirector.BehaviorTree
{
    // ==========================================
    // 行为树的全局内存：黑板 (Blackboard)
    // ==========================================
    public class Blackboard
    {
        private readonly ConcurrentDictionary<string, object> _data = new();

        public void Set<T>(string key, T value) where T : notnull => _data[key] = value;

        public T? Get<T>(string key)
        {
            if (_data.TryGetValue(key, out var val) && val is T typedVal)
                return typedVal;
            return default;
        }

        public bool HasKey(string key) => _data.ContainsKey(key);
    }


}