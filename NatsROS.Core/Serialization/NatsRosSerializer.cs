using MessagePack;
using NATS.Client.Core;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NatsROS.Core.Serialization;

public class MessagePackNatsSerializer<T> : INatsSerialize<T>, INatsDeserialize<T>
{
    public static readonly MessagePackNatsSerializer<T> Default = new();

    public void Serialize(IBufferWriter<byte> buffer, T value)
    {
        MessagePackSerializer.Serialize(buffer, value, MessagePack.Resolvers.ContractlessStandardResolver.Options);
    }
    public T? Deserialize(in ReadOnlySequence<byte> buffer)
    {
        return MessagePackSerializer.Deserialize<T>(buffer, MessagePack.Resolvers.ContractlessStandardResolver.Options);
    }
}

public class NatsRosSerializerRegistry : INatsSerializerRegistry
{
    public INatsSerialize<T> GetSerializer<T>() => MessagePackNatsSerializer<T>.Default;
    public INatsDeserialize<T> GetDeserializer<T>() => MessagePackNatsSerializer<T>.Default;
}
