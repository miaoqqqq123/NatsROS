using NATS.Client.Core;
using NATS.Net;
using NatsROS.Core.Communication;
using NatsROS.Core.SystemMessages;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NatsROS.Core.Parameters;

public class RosParameterClient(INatsClient nats, string targetNodeName)
{
    private readonly RosServiceClient<SetParamReq, SetParamRes> _setClient = new(nats, $"{targetNodeName}.param.set");
    private readonly RosServiceClient<GetParamReq, GetParamRes> _getClient = new(nats, $"{targetNodeName}.param.get");
    // 在类顶部追加字段
    private readonly RosServiceClient<ListParamsReq, ListParamsRes> _listClient = new(nats, $"{targetNodeName}.param.list");


    public async Task<bool> SetAsync(string name, string value, CancellationToken ct = default)
    {
        var res = await _setClient.CallAsync(new SetParamReq(name, value), TimeSpan.FromSeconds(2), ct);
        return res?.Success ?? false;
    }

    public async Task<string?> GetAsync(string name, CancellationToken ct = default)
    {
        var res = await _getClient.CallAsync(new GetParamReq(name), TimeSpan.FromSeconds(2), ct);
        return res != null && res.Exists ? res.Value : null;
    }

    // 在类底部追加方法
    public async Task<string[]> ListAsync(CancellationToken ct = default)
    {
        var res = await _listClient.CallAsync(new ListParamsReq(), TimeSpan.FromSeconds(2), ct);
        return res?.Names ?? Array.Empty<string>();
    }
}
