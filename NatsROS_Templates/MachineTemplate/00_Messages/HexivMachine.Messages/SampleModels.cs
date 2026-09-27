using MessagePack;
using NatsROS.Core;

namespace HexivMachine.Messages;

// 定义一个简单的请求响应服务
[MessagePackObject]
public record SampleEchoReq(
    [property: Key(0)] string Text
) : IRosRequest<SampleEchoRes>;

[MessagePackObject]
public record SampleEchoRes(
    [property: Key(0)] string ReplyText
) : IRosMessage;
