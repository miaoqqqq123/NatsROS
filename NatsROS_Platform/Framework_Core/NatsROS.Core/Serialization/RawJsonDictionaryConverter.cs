using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NatsROS.Core.Serialization
{
    /// <summary>
    /// 黑魔法：透明 JSON 解包器。
    /// 专门负责将 Dictionary<string, string> 中保存的 JSON 字符串，在写盘时转换为真正的 JSON 树状结构。
    /// </summary>
    public class RawJsonDictionaryConverter : JsonConverter<Dictionary<string, string>>
    {
        public override Dictionary<string, string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var dict = new Dictionary<string, string>();
            using var doc = JsonDocument.ParseValue(ref reader);

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                // 如果硬盘里存的是一个真实的 JSON 对象或数组
                if (prop.Value.ValueKind == JsonValueKind.Object || prop.Value.ValueKind == JsonValueKind.Array)
                {
                    // 把它强行“压扁”成原生的 JSON 字符串，塞回 C# 内存的 Dictionary 中
                    dict[prop.Name] = prop.Value.GetRawText();
                }
                else
                {
                    // 普通字符串或数字 (比如 "Velocity": "300")
                    dict[prop.Name] = prop.Value.GetString() ?? "";
                }
            }
            return dict;
        }

        public override void Write(Utf8JsonWriter writer, Dictionary<string, string> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (var kvp in value)
            {
                writer.WritePropertyName(kvp.Key);
                string val = kvp.Value ?? "";

                // 魔法拦截：如果发现这个字符串本身就是一个合法的 JSON，就拆掉引号直接当对象写入！
                if (!string.IsNullOrEmpty(val) &&
                    ((val.StartsWith("{") && val.EndsWith("}")) || (val.StartsWith("[") && val.EndsWith("]"))))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(val);
                        doc.WriteTo(writer);
                        continue; // 写入成功，跳过下方普通字符串的写入逻辑
                    }
                    catch { /* 解析失败说明不是真正的JSON，当成普通字符串处理 */ }
                }

                writer.WriteStringValue(val);
            }
            writer.WriteEndObject();
        }
    }
}