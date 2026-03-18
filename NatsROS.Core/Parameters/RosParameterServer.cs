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

    // 当参数被外部修改时，触发此事件，方便节点内部做出响应
    public event Action<string, string>? OnParameterChanged;

    public RosParameterServer(INatsClient nats, string nodeName)
    {
        _setServer = new(nats, $"{nodeName}.param.set");
        _getServer = new(nats, $"{nodeName}.param.get");
    }

    public void Start(CancellationToken ct)
    {
        // 监听外部修改参数的请求
        _ = _setServer.ServeAsync(req =>
        {
            _store[req.Name] = req.Value;
            OnParameterChanged?.Invoke(req.Name, req.Value);
            return Task.FromResult(new SetParamRes(true));
        }, ct);

        // 监听外部读取参数的请求
        _ = _getServer.ServeAsync(req =>
        {
            bool exists = _store.TryGetValue(req.Name, out var val);
            return Task.FromResult(new GetParamRes(val ?? string.Empty, exists));
        }, ct);
    }

    // 节点自己读取本地参数的方法
    public string GetLocal(string name, string defaultValue = "") =>
        _store.TryGetValue(name, out var v) ? v : defaultValue;

    // 节点自己设置本地参数的方法
    public void SetLocal(string name, string value) => _store[name] = value;
}
