using System.Text.Encodings.Web;
using System.Text.Json.Nodes;

namespace SaveForge.Core;

/// <summary>
/// 存档树 ↔ 通用 JSON 的双向转换。
///
/// 用途：
///   - 导出：把任意格式的存档转成 JSON，便于查看、备份或外部编辑
///   - 导入：把外部编辑过的 JSON 值按路径回写到当前存档（只改值，不改结构）
///
/// 结构映射：Object → JSON 对象，Array → JSON 数组，叶子 → 标量。
/// 导入时按名称/下标对齐，只更新能匹配上的叶子，不会新增或删除字段。
/// </summary>
public static class SaveDocumentJson
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ====================================================================
    // 导出
    // ====================================================================

    public static string Export(SaveDocument document)
    {
        var root = new JsonObject();

        foreach (var node in document.Roots)
            root[node.Name] = ToJson(node);

        return root.ToJsonString(WriteOptions);
    }

    private static JsonNode? ToJson(SaveNode node)
    {
        switch (node.Kind)
        {
            case ValueKind.Object:
            {
                var obj = new JsonObject();

                foreach (var child in node.Children)
                {
                    // 同名键（极少见）加序号后缀，保证 JSON 合法
                    string key = child.Name;
                    int suffix = 2;
                    while (obj.ContainsKey(key)) key = $"{child.Name}#{suffix++}";

                    obj[key] = ToJson(child);
                }

                return obj;
            }

            case ValueKind.Array:
            {
                var array = new JsonArray();
                foreach (var child in node.Children) array.Add(ToJson(child));
                return array;
            }

            case ValueKind.Null:
                return null;

            case ValueKind.Boolean:
                return JsonValue.Create(node.Value is true);

            case ValueKind.Integer:
                return JsonValue.Create(ToLong(node.Value));

            case ValueKind.Float:
                return JsonValue.Create(ToDouble(node.Value));

            case ValueKind.Binary:
                return JsonValue.Create(node.Value is byte[] bytes
                    ? Convert.ToBase64String(bytes)
                    : string.Empty);

            default:
                return JsonValue.Create(node.Value?.ToString() ?? string.Empty);
        }
    }

    // ====================================================================
    // 导入
    // ====================================================================

    /// <summary>
    /// 把 JSON 中的值回写到文档。返回实际发生变化的叶子数量。
    /// </summary>
    public static int Apply(SaveDocument document, string json)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(json);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"JSON 解析失败：{ex.Message}", ex);
        }

        if (parsed is not JsonObject root)
            throw new InvalidDataException("JSON 根必须是对象，且键为存档根节点名");

        int applied = 0;

        foreach (var node in document.Roots)
        {
            if (root.TryGetPropertyValue(node.Name, out JsonNode? jsonNode))
                applied += ApplyNode(node, jsonNode);
        }

        return applied;
    }

    private static int ApplyNode(SaveNode node, JsonNode? json)
    {
        if (json is null) return 0;

        if (node.Kind == ValueKind.Object && json is JsonObject obj)
        {
            int count = 0;

            foreach (var child in node.Children)
            {
                if (obj.TryGetPropertyValue(child.Name, out JsonNode? childJson))
                    count += ApplyNode(child, childJson);
            }

            return count;
        }

        if (node.Kind == ValueKind.Array && json is JsonArray array)
        {
            int count = 0;
            int limit = Math.Min(node.Children.Count, array.Count);

            for (int i = 0; i < limit; i++)
                count += ApplyNode(node.Children[i], array[i]);

            return count;
        }

        // 只处理可编辑叶子
        if (node.IsContainer || !node.IsEditable) return 0;

        // 转换失败（如把文本填进数值字段）视为"不适用"，跳过而不是写成 null
        if (!TryFromJson(json, node.Kind, out object? value)) return 0;

        if (ValuesEqual(node.Value, value)) return 0;

        node.SetValue(value);
        return 1;
    }

    /// <summary>把 JSON 标量转成目标类型。返回 false 表示类型不匹配，应跳过。</summary>
    private static bool TryFromJson(JsonNode? json, ValueKind kind, out object? value)
    {
        value = null;

        if (json is null) return kind == ValueKind.Null;
        if (json is not JsonValue scalar) return false;

        switch (kind)
        {
            case ValueKind.String:
                if (scalar.TryGetValue<string>(out string? text))
                {
                    value = text;
                    return true;
                }
                value = scalar.ToJsonString();
                return true;

            case ValueKind.Integer:
                if (scalar.TryGetValue<long>(out long integer))
                {
                    value = integer;
                    return true;
                }
                if (scalar.TryGetValue<double>(out double number))
                {
                    value = (long)number;
                    return true;
                }
                return false;

            case ValueKind.Float:
                if (scalar.TryGetValue<double>(out double real))
                {
                    value = real;
                    return true;
                }
                if (scalar.TryGetValue<long>(out long intValue))
                {
                    value = (double)intValue;
                    return true;
                }
                return false;

            case ValueKind.Boolean:
                if (scalar.TryGetValue<bool>(out bool flag))
                {
                    value = flag;
                    return true;
                }
                return false;

            case ValueKind.Binary:
                if (scalar.TryGetValue<string>(out string? base64))
                {
                    try
                    {
                        value = Convert.FromBase64String(base64);
                        return true;
                    }
                    catch
                    {
                        return false;
                    }
                }
                return false;

            case ValueKind.Null:
                return true;

            default:
                if (scalar.TryGetValue<string>(out string? plain))
                {
                    value = plain;
                    return true;
                }
                return false;
        }
    }

    // ====================================================================
    // 辅助
    // ====================================================================

    private static bool ValuesEqual(object? left, object? right)
    {
        if (left is null && right is null) return true;
        if (left is null || right is null) return false;

        if (left is byte[] a && right is byte[] b)
            return a.AsSpan().SequenceEqual(b);

        if (IsNumeric(left) && IsNumeric(right))
        {
            try { return Convert.ToDouble(left) == Convert.ToDouble(right); }
            catch { return false; }
        }

        return left.Equals(right);
    }

    private static bool IsNumeric(object value) =>
        value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double;

    private static long ToLong(object? value)
    {
        try { return value is null ? 0L : Convert.ToInt64(value); }
        catch { return 0L; }
    }

    private static double ToDouble(object? value)
    {
        try { return value is null ? 0d : Convert.ToDouble(value); }
        catch { return 0d; }
    }
}
