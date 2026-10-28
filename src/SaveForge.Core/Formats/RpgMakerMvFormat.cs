using System.Text.Json.Nodes;

namespace SaveForge.Core.Formats;

/// <summary>
/// RPG Maker MV / MZ 存档格式（.rpgsave / .rmmzsave）。
///
/// 存储链路：
///   游戏对象 → JsonEx.stringify → JSON 文本 → LZString.compressToBase64 → 文件
///
/// 不同版本的外层不一样：
///   - MV 常见：LZString base64 文本
///   - MV 明文 / 部分插件：直接是 JSON
///   - MZ 常见：JSON 被 pako(zlib) 压缩后再按 UTF-8 写入（由压缩层处理）
///
/// JsonEx 的编码特点（为了处理循环引用）：
///   对象/数组会被放进一个 contents 数组，字段位置写成 {"@": 索引} 引用；
///   浮点数写成 {"@r": "1.5"} 以免被 JSON 解析成整数。
///
/// 写回策略：
///   不做"展开后重建"，而是直接在原始 JsonNode 上就地改值，
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

        // 明文 JSON（数组或对象）：只在扩展名匹配时接管，且分数要高于通用 JSON（0.95）
        if (text.StartsWith('[') || text.StartsWith('{'))
        {
            if (!knownExtension) return 0f;

            try { return JsonNode.Parse(text) is JsonArray or JsonObject ? 0.97f : 0f; }
            catch { return 0f; }
        }

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

        if (string.IsNullOrEmpty(decompressed)) return 0f;
        if (!decompressed.StartsWith('[') && !decompressed.StartsWith('{')) return 0f;

        return knownExtension ? 0.97f : 0.80f;
    }

    public SaveDocument Read(byte[] data, string? filePath)
    {
        string text = DecodeText(data);

        bool plainJson = text.StartsWith('[') || text.StartsWith('{');
        string json = plainJson ? text : LzString.DecompressFromBase64(text);

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

        JsonNode root = parsed switch
        {
            JsonArray arrayRoot => arrayRoot,
            JsonObject objectRoot => objectRoot,
            _ => throw new InvalidDataException("JsonEx 根节点应为数组或对象"),
        };

        // 引用表：
        //   - 数组根：根数组本身就是 JsonEx 的对象表（沿用 MZ 的布局）
        //   - 对象根：按 JsonEx.stringify 的遍历顺序重建对象表
        var references = root is JsonObject
            ? BuildReferenceTable(root)
            : ((JsonArray)root).Cast<JsonNode>().ToList();

        if (root is JsonArray && references.Count == 0)
            throw new InvalidDataException("JsonEx 内容为空");

        var document = new SaveDocument
        {
            FilePath = filePath,
            FormatId = Id,
            FormatName = DisplayName,
            OriginalBytes = data,
            Tag = new MvContext
            {
                Root = root,
                References = references,
                WasCompressed = !plainJson,
            },
        };

        int budget = 300_000;

        document.Roots.Add(root is JsonArray array
            ? BuildNode(array[0], references, "存档", 0, null, ref budget)
            : BuildNode(root, references, "存档", 0, null, ref budget));

        document.AcceptChanges();

        return document;
    }

    public byte[] Write(SaveDocument document)
    {
        if (document.Tag is not MvContext context)
            throw new InvalidOperationException("文档缺少 RPG Maker MV/MZ 存档上下文，无法写回");

        ApplyChanges(document);

        // JsonEx 要求紧凑 JSON（无缩进），压缩后体积才正常
        string json = context.Root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        // 原文件是明文就写回明文；是 LZString 就保持 LZString。
        // 外层 pako 压缩由压缩层负责，不在这里重复处理。
        if (!context.WasCompressed) return Encoding.UTF8.GetBytes(json);

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

    private static SaveNode BuildNode(
        JsonNode? node,
        IReadOnlyList<JsonNode> references,
        string name,
        int depth,
        MvSlot? slot,
        ref int budget)
    {
        var result = new SaveNode { Name = name };

        if (depth > MaxDepth)
        {
            result.InitValue("…（层级过深，已截断）", ValueKind.String);
            result.IsEditable = false;
            return result;
        }

        // JsonEx 的共享引用可能形成环，靠总节点预算兜底，避免展开成无限树
        if (budget-- <= 0)
        {
            result.InitValue("…（节点过多，已截断）", ValueKind.String);
            result.IsEditable = false;
            return result;
        }

        JsonNode? resolved = Resolve(node, references);

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
                        references,
                        pair.Key,
                        depth + 1,
                        new MvSlot { Object = obj, Key = pair.Key },
                        ref budget));
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
                        references,
                        $"[{i}]",
                        depth + 1,
                        new MvSlot { Array = array, Index = i },
                        ref budget));
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

    /// <summary>
    /// 按 JsonEx.stringify 的遍历顺序重建对象引用表。
    /// 只有"真实对象"会入表，{"@": n} 引用本身与 {"@r": ...} 浮点包装不入表。
    /// </summary>
    private static List<JsonNode> BuildReferenceTable(JsonNode root)
    {
        var table = new List<JsonNode>();

        void Walk(JsonNode? node)
        {
            if (node is null) return;

            if (node is JsonObject obj)
            {
                if (obj.ContainsKey("@") || obj.ContainsKey("@r")) return;

                table.Add(obj);

                foreach (var pair in obj)
                {
                    if (pair.Key is "@" or "@r") continue;
                    Walk(pair.Value);
                }

                return;
            }

            if (node is JsonArray array)
            {
                table.Add(array);
                foreach (var item in array) Walk(item);
            }
        }

        Walk(root);
        return table;
    }

    /// <summary>递归解引用 {"@": 索引}，直到拿到实际对象。</summary>
    private static JsonNode? Resolve(JsonNode? node, IReadOnlyList<JsonNode> references, int depth = 0)
    {
        if (node is null || depth > MaxDepth) return node;
        if (node is not JsonObject obj) return node;

        if (!obj.TryGetPropertyValue("@", out JsonNode? idNode)) return obj;

        int id = ToInt(idNode);
        if (id < 0 || id >= references.Count) return obj;

        return Resolve(references[id], references, depth + 1);
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
        /// <summary>完整 JsonEx 根节点（数组或对象）。写回时整体序列化。</summary>
        public JsonNode Root { get; init; } = new JsonArray();

        /// <summary>引用表，对应 JsonEx 的对象表。</summary>
        public IReadOnlyList<JsonNode> References { get; init; } = Array.Empty<JsonNode>();

        /// <summary>原文件是否是 LZString 压缩文本。</summary>
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
