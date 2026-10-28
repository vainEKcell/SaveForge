using System.Text.Encodings.Web;
using System.Text.Json.Nodes;

namespace SaveForge.Core.Formats;

/// <summary>
/// JSON 存档格式。覆盖 Unity（JsonUtility / Newtonsoft.Json）及大多数独立游戏的文本存档。
///
/// 设计要点：
/// - 整个 JSON 根统一映射为一个 SaveNode 根节点，UI 层再做扁平化展示；
/// - 解析保序（JsonObject 枚举顺序即键顺序）；
/// - 写回从节点树重建 JSON，保证"改了什么就是什么"，不残留原始字符串。
/// </summary>
public sealed class JsonFormat : ISaveFormat
{
    public string Id => "json";
    public string DisplayName => "JSON 存档";
    public string[] Extensions => ["json", "dat", "save", "sav", "txt", "cfg"];
    public bool CanWrite => true;

    public float Detect(ReadOnlySpan<byte> data, string? fileName)
    {
        int start = SkipBom(data);
        int i = start;
        while (i < data.Length && IsJsonWhitespace(data[i])) i++;
        if (i >= data.Length) return 0f;

        byte first = data[i];
        if (first != (byte)'{' && first != (byte)'[' && first != (byte)'"')
            return fileName?.EndsWith(".json", StringComparison.OrdinalIgnoreCase) == true ? 0.15f : 0f;

        // 真正解析一次，确认结构合法
        try
        {
            JsonNode.Parse(data[start..].ToArray());
            return 0.95f;
        }
        catch
        {
            // 形似但解析失败：可能是加密或损坏，交给十六进制兜底更诚实
            return 0f;
        }
    }

    public SaveDocument Read(byte[] data, string? filePath)
    {
        int start = SkipBom(data);
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(data.AsSpan(start).ToArray());
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"JSON 解析失败：{ex.Message}", ex);
        }

        var document = new SaveDocument
        {
            FilePath = filePath,
            FormatId = Id,
            FormatName = DisplayName,
            OriginalBytes = data,
        };

        var rootNode = BuildNode(root, "$");
        rootNode.Locator = "$";
        document.Roots.Add(rootNode);
        document.AcceptChanges();

        return document;
    }

    public byte[] Write(SaveDocument document)
    {
        var root = document.Roots.FirstOrDefault()
            ?? throw new InvalidOperationException("文档没有根节点，无法写出 JSON");

        JsonNode? node = BuildJson(root);
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        string json = node?.ToJsonString(options) ?? "null";
        return Encoding.UTF8.GetBytes(json);
    }

    // ====================================================================
    // JSON -> 节点树
    // ====================================================================

    private static SaveNode BuildNode(JsonNode? json, string name)
    {
        if (json is null)
        {
            var nullNode = new SaveNode { Name = name, Kind = ValueKind.Null };
            nullNode.InitValue(null, ValueKind.Null);
            return nullNode;
        }

        switch (json)
        {
            case JsonObject obj:
            {
                var node = new SaveNode { Name = name, Kind = ValueKind.Object };
                foreach (var (key, child) in obj)
                {
                    var childNode = BuildNode(child, key);
                    childNode.Locator = child?.GetPath();
                    node.Children.Add(childNode);
                }
                return node;
            }

            case JsonArray arr:
            {
                var node = new SaveNode { Name = name, Kind = ValueKind.Array };
                for (int i = 0; i < arr.Count; i++)
                {
                    var childNode = BuildNode(arr[i], $"[{i}]");
                    childNode.Locator = arr[i]?.GetPath();
                    node.Children.Add(childNode);
                }
                return node;
            }

            case JsonValue value:
                return BuildLeaf(value, name);

            default:
            {
                var fallback = new SaveNode { Name = name, Kind = ValueKind.String };
                fallback.InitValue(json.ToJsonString(), ValueKind.String);
                return fallback;
            }
        }
    }

    private static SaveNode BuildLeaf(JsonValue value, string name)
    {
        var node = new SaveNode { Name = name };

        if (value.TryGetValue<string>(out var text))
        {
            node.InitValue(text, ValueKind.String);
        }
        else if (value.TryGetValue<bool>(out var flag))
        {
            node.InitValue(flag, ValueKind.Boolean);
        }
        else if (value.TryGetValue<long>(out var integer))
        {
            node.InitValue(integer, ValueKind.Integer);
        }
        else if (value.TryGetValue<double>(out var number))
        {
            node.InitValue(number, ValueKind.Float);
        }
        else
        {
            // 极少数情况（如超大整数）：保留原始 JSON 文本
            node.InitValue(value.ToJsonString(), ValueKind.String);
        }

        return node;
    }

    // ====================================================================
    // 节点树 -> JSON
    // ====================================================================

    private static JsonNode? BuildJson(SaveNode node)
    {
        switch (node.Kind)
        {
            case ValueKind.Object:
            {
                var obj = new JsonObject();
                foreach (var child in node.Children)
                    obj[child.Name] = BuildJson(child);
                return obj;
            }

            case ValueKind.Array:
            {
                var arr = new JsonArray();
                foreach (var child in node.Children)
                    arr.Add(BuildJson(child));
                return arr;
            }

            case ValueKind.Null:
                return null;

            case ValueKind.Boolean:
                return JsonValue.Create(node.Value is true);

            case ValueKind.Integer:
                return JsonValue.Create(ToInt64(node.Value));

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

    private static long ToInt64(object? value)
    {
        try { return value is null ? 0L : Convert.ToInt64(value); }
        catch { return 0L; }
    }

    private static double ToDouble(object? value)
    {
        try { return value is null ? 0d : Convert.ToDouble(value); }
        catch { return 0d; }
    }

    // ====================================================================
    // 工具
    // ====================================================================

    private static int SkipBom(ReadOnlySpan<byte> data) =>
        data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;

    private static bool IsJsonWhitespace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';
}
