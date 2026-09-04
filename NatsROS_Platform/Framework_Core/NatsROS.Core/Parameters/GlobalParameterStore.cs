using NatsROS.Core.Environment;
using NatsROS.Core.Serialization;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace NatsROS.Core.Parameters
{
    /// <summary>
    /// 全局参数持久化引擎，负责线程安全的文件读写与节点命名空间隔离
    /// </summary>
    public static class GlobalParameterStore
    {
        // 内存中的全量参数树: NodeName -> (ParamKey -> ParamValue)
        private static ConcurrentDictionary<string, Dictionary<string, string>> _globalParams = new();

        // 文件读写锁，防止多节点并发存盘时发生 I/O 冲突
        private static readonly object _ioLock = new object();

        private static string FilePath => WorkspaceManager.GetConfigPath("node_params.json");
        private static bool _isLoaded = false;

        // 1. 开机懒加载 (Lazy Load)
        private static void EnsureLoaded()
        {
            if (_isLoaded) return;
            lock (_ioLock)
            {
                if (_isLoaded) return;
                if (File.Exists(FilePath))
                {
                    try
                    {
                        var json = File.ReadAllText(FilePath);

                        // 读取时也必须挂载透明解包器！
                        // 这样它遇到嵌套的 Object 节点时，就会自动把它压扁回 String 存进内存字典
                        var options = new JsonSerializerOptions
                        {
                            WriteIndented = true,
                            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                            Converters = { new NatsROS.Core.Serialization.RawJsonDictionaryConverter() }
                        };

                        var loaded = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json, options);
                        if (loaded != null)
                        {
                            _globalParams = new ConcurrentDictionary<string, Dictionary<string, string>>(loaded);
                        }
                    }
                    catch { /* 文件损坏则使用空字典 */ }
                }
                _isLoaded = true;
            }
        }

        // 2. 供某个具体节点获取它自己的专属参数
        public static Dictionary<string, string> GetNodeParameters(string nodeName)
        {
            EnsureLoaded();
            if (_globalParams.TryGetValue(nodeName, out var nodeDict))
            {
                // 返回一个副本，防止外界直接修改字典导致并发报错
                return new Dictionary<string, string>(nodeDict);
            }
            return new Dictionary<string, string>();
        }

        // 3. 供某个具体节点保存参数，并异步落盘
        public static void SaveNodeParameter(string nodeName, string key, string value)
        {
            EnsureLoaded();

            // 获取或创建属于该节点的参数字典
            var nodeDict = _globalParams.GetOrAdd(nodeName, _ => new Dictionary<string, string>());
            nodeDict[key] = value; // 更新内存

            // 触发异步落盘，绝不阻塞调用方 (如 NATS 消息线程)
            Task.Run(() => FlushToDisk());
        }

        // 4. 线程安全的写盘操作
        private static void FlushToDisk()
        {
            lock (_ioLock)
            {
                try
                {
                    var options = new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    };

                    // 挂载透明解包器！
                    options.Converters.Add(new RawJsonDictionaryConverter());

                    // 序列化整个宏观的命名空间树
                    string json = JsonSerializer.Serialize(_globalParams, options);
                    File.WriteAllText(FilePath, json);
                }
                catch { /* 忽略瞬时的 I/O 报错 */ }
            }
        }
    }
}