using NATS.Client.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NatsROS.Core.Communication;

public class RosServiceClient<TReq, TRes>(INatsClient nats, string serviceName)
    where TReq : IRosMessage
    where TRes : IRosMessage
{
    public string ServiceName { get; } = serviceName;

    public async Task<TRes?> CallAsync(TReq request, TimeSpan timeout = default, CancellationToken ct = default)
    {
        var actualTimeout = timeout == default ? TimeSpan.FromSeconds(5) : timeout;
        var reply = await nats.RequestAsync<TReq, TRes>(
            subject: ServiceName,
            data: request,
            requestOpts: new NatsPubOpts { WaitUntilSent = true },
            replyOpts: new NatsSubOpts { Timeout = actualTimeout },
            cancellationToken: ct);

        return reply.Data;
    }
}

public class RosServiceServer<TReq, TRes>(INatsClient nats, string serviceName)
    where TReq : IRosMessage
    where TRes : IRosMessage
{
    public string ServiceName { get; } = serviceName;

    public async Task ServeAsync(Func<TReq, Task<TRes>> requestHandler, CancellationToken ct = default)
    {
        await foreach (var msg in nats.SubscribeAsync<TReq>(ServiceName, cancellationToken: ct))
        {
            if (msg.Data is not null)
            {
                var response = await requestHandler(msg.Data);
                await msg.ReplyAsync(response, cancellationToken: ct);
            }
        }
    }
}
