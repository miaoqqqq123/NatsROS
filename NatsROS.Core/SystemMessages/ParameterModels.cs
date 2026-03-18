using MessagePack;

namespace NatsROS.Core.SystemMessages;

// 修改参数请求与响应
[MessagePackObject]
public record SetParamReq([property: Key(0)] string Name, [property: Key(1)] string Value) : IRosMessage;

[MessagePackObject]
public record SetParamRes([property: Key(0)] bool Success) : IRosMessage;

// 获取参数请求与响应
[MessagePackObject]
public record GetParamReq([property: Key(0)] string Name) : IRosMessage;

[MessagePackObject]
public record GetParamRes([property: Key(0)] string Value, [property: Key(1)] bool Exists) : IRosMessage;
