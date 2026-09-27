using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NatsROS.Core.Serialization
{
    /// <summary>
    /// 黑魔法：纯字符串的透明 JSON 解包器。
    /// 专门负责将包含 JSON 格式的 string，在写盘时展开为真实树状结构；读盘时重新折叠为 string。
    /// </summary>
    public class RawJsonStringConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            // 如果硬盘里存的是一个真实的 JSON 对象或数组，我们把它重新压扁成字符串，塞回内存！
            if (doc.RootElement.ValueKind == JsonValueKind.Object || doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                return doc.RootElement.GetRawText();
            }
            return doc.RootElement.GetString() ?? "";
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            // 魔法拦截：如果发现这个字符串本身就是一个合法的 JSON，就拆掉转义符，直接当对象原生态写入！
            if (!string.IsNullOrEmpty(value) &&
                ((value.StartsWith("{") && value.EndsWith("}")) || (value.StartsWith("[") && value.EndsWith("]"))))
            {
                try
                {
                    using var doc = JsonDocument.Parse(value);
                    doc.WriteTo(writer); // 这里的 WriteTo 会完美继承外部的缩进排版 (WriteIndented=true)！
                    return;
                }
                catch { /* 解析失败说明不是真正的JSON，当成普通字符串处理 */ }
            }
            writer.WriteStringValue(value);
        }
    }
}