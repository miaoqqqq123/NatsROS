using NATS.Client.Core;
using NATS.Net;
using NatsROS.Core.Communication;
using NatsROS.Core.SystemMessages;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace NatsROS.Core.Parameters;

public class RosParameterServer
{
    private readonly ConcurrentDictionary<string, string> _store = new();
    private readonly RosServiceServer<SetParamReq, SetParamRes> _setServer;
    private readonly RosServiceServer<GetParamReq, GetParamRes> _getServer;

    // 在类顶部追加字段
    private readonly RosServiceServer<ListParamsReq, ListParamsRes> _listServer;

    // 记录节点名，方便存盘时按命名空间隔离
    private readonly string _nodeName;

    // 当参数被外部修改时，触发此事件，方便节点内部做出响应
    public event Action<string, string>? OnParameterChanged;

    public RosParameterServer(INatsClient nats, string nodeName)
    {
        _nodeName = nodeName;
        _setServer = new(nats, $"{nodeName}.param.set");
        _getServer = new(nats, $"{nodeName}.param.get");
        _listServer = new(nats, $"{nodeName}.param.list"); // 【新增】


        // 在服务器实例化的一瞬间，去硬盘里把属于自己的记忆找回来！
        var savedParams = GlobalParameterStore.GetNodeParameters(_nodeName);
        foreach (var kvp in savedParams)
        {
            _store[kvp.Key] = kvp.Value;
        }
    }

    public void Start(CancellationToken ct)
    {
        // 监听外部修改参数的请求
        _ = _setServer.ServeAsync(req =>
        {
            SetLocal(req.Name, req.Value);
            return Task.FromResult(new SetParamRes(true));
        }, ct);

        // 监听外部读取参数的请求
        _ = _getServer.ServeAsync(req =>
        {
            bool exists = _store.TryGetValue(req.Name, out var val);
            return Task.FromResult(new GetParamRes(val ?? string.Empty, exists));
        }, ct);

        // 监听列出所有参数的请求
        _ = _listServer.ServeAsync(req =>
        {
            // 直接把字典里所有的 Key 提取成数组返回
            var keys = _store.Keys.ToArray();
            return Task.FromResult(new ListParamsRes(keys));
        }, ct);
    }

    /// <summary>
    /// 节点自己读取本地参数的方法
    /// </summary>
    /// <param name="name"></param>
    /// <param name="defaultValue"></param>
    /// <returns></returns>
    public string GetLocal(string name, string defaultValue = "")
    {
        if (_store.TryGetValue(name, out var v))
            return v;

        // 【修改 4】：智能暴露！如果没找到，就把默认值写进去并存盘，保证下次 JSON 里有记录
        SetLocal(name, defaultValue);
        return defaultValue;
    }


    /// <summary>
    /// 统一的参数修改与持久化入口
    /// </summary>
    /// <param name="name"></param>
    /// <param name="value"></param>
    public void SetLocal(string name, string value)
    {
        _store[name] = value;

        // 通知全局持久化大管家存盘！
        GlobalParameterStore.SaveNodeParameter(_nodeName, name, value);

        // 触发事件通知业务节点
        OnParameterChanged?.Invoke(name, value);
    }
}
