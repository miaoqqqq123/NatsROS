using System.Collections.Concurrent;

namespace Hexiv.BehaviorTree.Core
{
    public class Blackboard
    {
        private readonly ConcurrentDictionary<string, object> _storage = new();

        public void Set<T>(string key, T value)
        {
            if (value == null) return;
            _storage[key] = value;
        }

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
    }
}