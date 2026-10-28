using System.Text.Json.Nodes;

namespace SaveForge.Core.Formats;

/// <summary>
/// RPG Maker MV / MZ 存档格式（.rpgsave / .rmmzsave）。
///
/// 存储链路：
///   游戏对象 → JsonEx.stringify → JSON 文本 → LZString.compressToBase64 → 文件
///
/// JsonEx 的编码特点（为了处理循环引用）：
///   所有对象/数组都会被放进一个 contents 数组，字段位置写成 {"@": 索引} 引用；
///   浮点数写成 {"@r": "1.5"} 以免被 JSON 解析成整数。
///
/// 写回策略：
///   不做"展开后重建"，而是直接在原始 contents 的 JsonNode 上就地改值，
///   这样对象之间的共享引用与循环引用完全保持原样，不会破坏游戏状态。
/// </summary>
public sealed class RpgMakerMvFormat : ISaveFormat
{
    public string Id => "rpgmaker-mv";
    public string DisplayName => "RPG Maker MV / MZ 存档";
    public string[] Extensions => ["rpgsave", "rmmzsave"];
    public bool CanWrite => true;

    private const int MaxDepth = 64;
    private const int MaxChildren = 2000;

    public float Detect(ReadOnlySpan<byte> data, string? fileName)
    {
        if (data.Length < 16) return 0f;

        bool knownExtension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant()
            is ".rpgsave" or ".rmmzsave";

        string text = DecodeText(data);

        // 明文 JsonEx 数组：只在扩展名匹配时接管，且分数要高于通用 JSON（0.95）
        if (text.StartsWith('['))
            return knownExtension ? 0.97f : 0f;

        // 只可能是 LZString 的 base64 输出
        foreach (char c in text)
        {
            if (!char.IsLetterOrDigit(c) && c is not ('+' or '/' or '=')) return 0f;
        }

        string decompressed;
        try
        {
            decompressed = LzString.DecompressFromBase64(text);
        }
        catch
        {
            return 0f;
        }

        if (string.IsNullOrEmpty(decompressed) || !decompressed.StartsWith('['))
            return 0f;

        return knownExtension ? 0.97f : 0.80f;
    }

    public SaveDocument Read(byte[] data, string? filePath)
    {
        string text = DecodeText(data);

        string json = text.StartsWith('[') || text.StartsWith('{')
            ? text
            : LzString.DecompressFromBase64(text);

        if (string.IsNullOrEmpty(json))
            throw new InvalidDataException("LZString 解压失败，可能不是 MV/MZ 存档");

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(json);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"JsonEx 解析失败：{ex.Message}", ex);
        }

        var contents = parsed as JsonArray
            ?? throw new InvalidDataException("JsonEx 根节点应为数组");

        if (contents.Count == 0)
            throw new InvalidDataException("JsonEx 内容为空");

        var document = new SaveDocument
        {
            FilePath = filePath,
            FormatId = Id,
            FormatName = DisplayName,
            OriginalBytes = data,
            Tag = new MvContext { Contents = contents, WasCompressed = !text.StartsWith('[') },
        };

        document.Roots.Add(BuildNode(contents[0], contents, "存档", 0, null));
        document.AcceptChanges();

        return document;
    }

    public byte[] Write(SaveDocument document)
    {
        if (document.Tag is not MvContext context)
            throw new InvalidOperationException("文档缺少 RPG Maker MV/MZ 存档上下文，无法写回");

        ApplyChanges(document);

        // JsonEx 要求紧凑 JSON（无缩进），压缩后体积才正常
        string json = context.Contents.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        string encoded = LzString.CompressToBase64(json);
        return Encoding.UTF8.GetBytes(encoded);
    }

    // ====================================================================
    // 修改写回
    // ====================================================================

    private static void ApplyChanges(SaveDocument document)
    {
        foreach (var root in document.Roots)
        {
            foreach (var node in root.Descendants())
            {
                if (!node.IsModified) continue;
                if (node.Tag is not MvSlot slot) continue;

                slot.Set(node.Value);
            }
        }
    }

    // ====================================================================
    // JsonEx → 节点树
    // ====================================================================

    private static SaveNode BuildNode(JsonNode? node, JsonArray contents, string name, int depth, MvSlot? slot)
    {
        var result = new SaveNode { Name = name };

        if (depth > MaxDepth)
        {
            result.InitValue("…（层级过深，已截断）", ValueKind.String);
            result.IsEditable = false;
            return result;
        }

        JsonNode? resolved = Resolve(node, contents);

        // 浮点特殊表示：{"@r": "1.5"}
        if (resolved is JsonObject floatWrapper && floatWrapper.ContainsKey("@r"))
        {
            string text = floatWrapper["@r"]?.ToString() ?? "0";
            result.InitValue(
                double.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double number) ? number : 0d,
                ValueKind.Float);
            result.TypeHint = "float";
            result.Tag = new MvSlot { Object = floatWrapper, Key = "@r", IsFloat = true };
            return result;
        }

        switch (resolved)
        {
            case null:
                result.InitValue(null, ValueKind.Null);
                break;

            case JsonObject obj:
            {
                result.InitValue(null, ValueKind.Object);

                int count = 0;
                foreach (var pair in obj)
                {
                    if (pair.Key == "@") continue;   // 内部引用标记，不展示

                    if (count++ >= MaxChildren)
                    {
                        result.Children.Add(Truncated());
                        break;
                    }

                    result.Children.Add(BuildNode(
                        pair.Value,
                        contents,
                        pair.Key,
                        depth + 1,
                        new MvSlot { Object = obj, Key = pair.Key }));
                }

                break;
            }

            case JsonArray array:
            {
                result.InitValue(null, ValueKind.Array);

                for (int i = 0; i < array.Count; i++)
                {
                    if (i >= MaxChildren)
                    {
                        result.Children.Add(Truncated());
                        break;
                    }

                    result.Children.Add(BuildNode(
                        array[i],
                        contents,
                        $"[{i}]",
                        depth + 1,
                        new MvSlot { Array = array, Index = i }));
                }

                break;
            }

            case JsonValue value:
            {
                if (value.TryGetValue<bool>(out bool flag))
                {
                    result.InitValue(flag, ValueKind.Boolean);
                }
                else if (value.TryGetValue<long>(out long integer))
                {
                    result.InitValue(integer, ValueKind.Integer);
                }
                else if (value.TryGetValue<double>(out double real))
                {
                    result.InitValue(real, ValueKind.Float);
                }
                else if (value.TryGetValue<string>(out string? text))
                {
                    result.InitValue(text, ValueKind.String);
                }
                else
                {
                    result.InitValue(value.ToJsonString(), ValueKind.String);
                }

                result.Tag = slot;
                break;
            }

            default:
                result.InitValue(node?.ToJsonString() ?? "null", ValueKind.String);
                result.IsEditable = false;
                break;
        }

        return result;
    }

    /// <summary>递归解引用 {"@": 索引}，直到拿到实际对象。</summary>
    private static JsonNode? Resolve(JsonNode? node, JsonArray contents, int depth = 0)
    {
        if (node is null || depth > MaxDepth) return node;
        if (node is not JsonObject obj) return node;

        if (!obj.TryGetPropertyValue("@", out JsonNode? idNode)) return obj;

        int id = ToInt(idNode);
        if (id < 0 || id >= contents.Count) return obj;

        return Resolve(contents[id], contents, depth + 1);
    }

    private static int ToInt(JsonNode? node)
    {
        if (node is not JsonValue value) return -1;

        if (value.TryGetValue<int>(out int integer)) return integer;
        if (value.TryGetValue<long>(out long longValue) && longValue is >= int.MinValue and <= int.MaxValue)
            return (int)longValue;

        return -1;
    }

    private static SaveNode Truncated()
    {
        var node = new SaveNode { Name = "…", IsEditable = false };
        node.InitValue("超出单节点上限，未继续展开", ValueKind.String);
        return node;
    }

    /// <summary>解码为文本并去掉 UTF-8 BOM 与首尾空白。</summary>
    private static string DecodeText(ReadOnlySpan<byte> data)
    {
        string text = Encoding.UTF8.GetString(data);
        return text.TrimStart('\uFEFF').Trim();
    }

    // ====================================================================
    // 上下文与槽位
    // ====================================================================

    private sealed class MvContext
    {
        public JsonArray Contents { get; init; } = new();
        public bool WasCompressed { get; init; }
    }

    /// <summary>指向 JsonEx 结构中具体位置，保存时就地改值。</summary>
    private sealed class MvSlot
    {
        public JsonObject? Object { get; init; }
        public string? Key { get; init; }
        public JsonArray? Array { get; init; }
        public int Index { get; init; } = -1;
        public bool IsFloat { get; init; }

        public void Set(object? value)
        {
            if (Object is not null && Key is not null)
            {
                Object[Key] = ToJsonValue(value, IsFloat);
                return;
            }

            if (Array is not null && Index >= 0 && Index < Array.Count)
                Array[Index] = ToJsonValue(value, IsFloat);
        }

        private static JsonNode? ToJsonValue(object? value, bool asFloatString)
        {
            if (asFloatString)
            {
                double number = value switch
                {
                    double d => d,
                    float f => f,
                    _ => 0d,
                };

                return JsonValue.Create(number.ToString("R",
                    System.Globalization.CultureInfo.InvariantCulture));
            }

            return value switch
            {
                null => null,
                bool flag => JsonValue.Create(flag),
                string text => JsonValue.Create(text),
                byte small => JsonValue.Create((long)small),
                short shortValue => JsonValue.Create((long)shortValue),
                int integer => JsonValue.Create((long)integer),
                long longValue => JsonValue.Create(longValue),
                float single => JsonValue.Create((double)single),
                double number => JsonValue.Create(number),
                _ => JsonValue.Create(value.ToString()),
            };
        }
    }
}
