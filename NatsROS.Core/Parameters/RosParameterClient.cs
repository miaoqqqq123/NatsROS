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
}
