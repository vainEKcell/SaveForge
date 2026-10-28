using System.Xml;
using System.Xml.Linq;

namespace SaveForge.Core.Formats;

/// <summary>
/// Apple plist 格式，两种变体：
///   - XML plist：文本，直接读写
///   - Binary plist（bplist00）：二进制，由 BinaryPlistReader / BinaryPlistWriter 处理
///
/// 两种变体都支持写回。binary 变体保存时会把修改写回对象图再整体重新编码，
/// 因为 bplist 的偏移表与对象索引强耦合，无法原地打补丁。
/// </summary>
public sealed class PlistFormat : ISaveFormat
{
    public string Id => "plist";
    public string DisplayName => "plist 存档";
    public string[] Extensions => ["plist", "bplist"];
    public bool CanWrite => true;

    private static ReadOnlySpan<byte> BinaryMagic => "bplist00"u8;

    private const int MaxDepth = 64;
    private const int MaxChildren = 5000;

    public float Detect(ReadOnlySpan<byte> data, string? fileName)
    {
        if (data.Length >= 8 && data[..8].SequenceEqual(BinaryMagic))
            return 0.93f;

        int start = SkipBomAndSpace(data);
        if (start >= data.Length || data[start] != (byte)'<') return 0f;

        try
        {
            using var stream = new MemoryStream(data.ToArray());
            var xdoc = XDocument.Load(stream);
            if (string.Equals(xdoc.Root?.Name.LocalName, "plist", StringComparison.OrdinalIgnoreCase))
                return 0.93f;
        }
        catch { /* 不是合法 XML */ }

        return fileName?.EndsWith(".plist", StringComparison.OrdinalIgnoreCase) == true ? 0.2f : 0f;
    }

    public SaveDocument Read(byte[] data, string? filePath)
    {
        var document = new SaveDocument
        {
            FilePath = filePath,
            FormatId = Id,
            FormatName = DisplayName,
            OriginalBytes = data,
        };

        if (data.Length >= 8 && data.AsSpan(0, 8).SequenceEqual(BinaryMagic))
        {
            var reader = new BinaryPlistReader(data);
            object? value = reader.Parse();

            document.Tag = new PlistContext { IsBinary = true, BinaryRoot = value };
            document.Roots.Add(BuildBinaryNode(value, "plist", 0, null));
        }
        else
        {
            XDocument xdoc;
            try
            {
                using var stream = new MemoryStream(data);
                xdoc = XDocument.Load(stream);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException($"plist 解析失败：{ex.Message}", ex);
            }

            var plistRoot = xdoc.Root
                ?? throw new InvalidDataException("plist 没有根元素");

            var valueElement = plistRoot.Elements().FirstOrDefault()
                ?? throw new InvalidDataException("plist 内没有值节点");

            document.Tag = new PlistContext { IsBinary = false };
            document.Roots.Add(ConvertXmlElement(valueElement, "plist"));
        }

        document.AcceptChanges();
        return document;
    }

    public byte[] Write(SaveDocument document)
    {
        if (document.Tag is PlistContext { IsBinary: true, BinaryRoot: not null } binaryContext)
        {
            ApplyBinaryChanges(document);
            return BinaryPlistWriter.Write(binaryContext.BinaryRoot);
        }

        var root = document.Roots.FirstOrDefault()
            ?? throw new InvalidOperationException("文档没有根节点");

        var plist = new XElement("plist", new XAttribute("version", "1.0"));
        plist.Add(BuildXmlElement(root));

        var xdoc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN",
                "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            plist);

        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "\t",
            Encoding = new UTF8Encoding(false),
        };

        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, settings))
            xdoc.Save(writer);

        return stream.ToArray();
    }

    // ====================================================================
    // Binary 变体：修改写回对象图
    // ====================================================================

    private static void ApplyBinaryChanges(SaveDocument document)
    {
        foreach (var root in document.Roots)
        {
            foreach (var node in root.Descendants())
            {
                if (!node.IsModified) continue;
                if (node.Tag is not PlistSlot slot) continue;

                slot.Set(Coerce(node.Value, slot.Get()));
            }
        }
    }

    private static object? Coerce(object? value, object? current) => current switch
    {
        string => value?.ToString() ?? string.Empty,
        bool => value is bool flag && flag,
        long => ToInt64(value),
        int => unchecked((int)ToInt64(value)),
        short => unchecked((short)ToInt64(value)),
        byte => unchecked((byte)ToInt64(value)),
        double => ToDouble(value),
        float => (float)ToDouble(value),
        byte[] => value as byte[] ?? [],
        DateTime => current,
        _ => value,
    };

    private static SaveNode BuildBinaryNode(object? value, string name, int depth, PlistSlot? slot)
    {
        var node = new SaveNode { Name = name };

        if (depth > MaxDepth)
        {
            node.InitValue("…（层级过深，已截断）", ValueKind.String);
            node.IsEditable = false;
            return node;
        }

        switch (value)
        {
            case null:
                node.InitValue(null, ValueKind.Null);
                break;

            case bool flag:
                node.InitValue(flag, ValueKind.Boolean);
                break;

            case long longValue:
                node.InitValue(longValue, ValueKind.Integer);
                break;

            case int integer:
                node.InitValue((long)integer, ValueKind.Integer);
                break;

            case short shortValue:
                node.InitValue((long)shortValue, ValueKind.Integer);
                break;

            case byte small:
                node.InitValue((long)small, ValueKind.Integer);
                break;

            case double number:
                node.InitValue(number, ValueKind.Float);
                break;

            case float single:
                node.InitValue((double)single, ValueKind.Float);
                break;

            case string text:
                node.InitValue(text, ValueKind.String);
                break;

            case byte[] bytes:
                node.InitValue(bytes, ValueKind.Binary);
                node.IsEditable = false;
                break;

            case DateTime date:
                node.InitValue(date.ToString("yyyy-MM-dd HH:mm:ss"), ValueKind.String);
                node.TypeHint = "date";
                node.IsEditable = false;
                break;

            case List<KeyValuePair<string, object?>> dict:
            {
                node.InitValue(null, ValueKind.Object);
                node.TypeHint = "dict";

                int count = 0;
                foreach (var pair in dict)
                {
                    if (count++ >= MaxChildren)
                    {
                        node.Children.Add(Truncated());
                        break;
                    }

                    node.Children.Add(BuildBinaryNode(
                        pair.Value,
                        pair.Key,
                        depth + 1,
                        new PlistSlot { Dict = dict, Key = pair.Key }));
                }

                break;
            }

            case List<object?> list:
            {
                node.InitValue(null, ValueKind.Array);
                node.TypeHint = "array";

                for (int i = 0; i < list.Count; i++)
                {
                    if (i >= MaxChildren)
                    {
                        node.Children.Add(Truncated());
                        break;
                    }

                    node.Children.Add(BuildBinaryNode(
                        list[i],
                        $"[{i}]",
                        depth + 1,
                        new PlistSlot { Array = list, Index = i }));
                }

                break;
            }

            default:
                node.InitValue(value.ToString() ?? "?", ValueKind.String);
                node.IsEditable = false;
                break;
        }

        if (!node.IsContainer && node.IsEditable)
            node.Tag = slot;

        return node;
    }

    private static SaveNode Truncated()
    {
        var node = new SaveNode { Name = "…", IsEditable = false };
        node.InitValue("超出单节点上限，未继续展开", ValueKind.String);
        return node;
    }

    // ====================================================================
    // XML plist → 节点树
    // ====================================================================

    private static SaveNode ConvertXmlElement(XElement element, string name)
    {
        var node = new SaveNode { Name = name };

        switch (element.Name.LocalName)
        {
            case "dict":
            {
                node.InitValue(null, ValueKind.Object);
                var children = element.Elements().ToList();

                for (int i = 0; i + 1 < children.Count; i += 2)
                {
                    if (children[i].Name.LocalName != "key") continue;

                    string key = children[i].Value;
                    var valueNode = ConvertXmlElement(children[i + 1], key);
                    node.Children.Add(valueNode);
                }

                return node;
            }

            case "array":
            {
                node.InitValue(null, ValueKind.Array);
                int index = 0;
                foreach (var item in element.Elements())
                    node.Children.Add(ConvertXmlElement(item, $"[{index++}]"));
                return node;
            }

            case "string":
                node.TypeHint = "string";
                node.InitValue(element.Value, ValueKind.String);
                return node;

            case "integer":
            {
                node.TypeHint = "integer";
                if (long.TryParse(element.Value.Trim(), out long integer))
                    node.InitValue(integer, ValueKind.Integer);
                else
                    node.InitValue(element.Value, ValueKind.String);
                return node;
            }

            case "real":
            {
                node.TypeHint = "real";
                if (double.TryParse(element.Value.Trim(),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double real))
                    node.InitValue(real, ValueKind.Float);
                else
                    node.InitValue(element.Value, ValueKind.String);
                return node;
            }

            case "true":
                node.TypeHint = "bool";
                node.InitValue(true, ValueKind.Boolean);
                return node;

            case "false":
                node.TypeHint = "bool";
                node.InitValue(false, ValueKind.Boolean);
                return node;

            case "date":
                node.TypeHint = "date";
                node.InitValue(element.Value, ValueKind.String);
                return node;

            case "data":
            {
                node.TypeHint = "data";
                try
                {
                    node.InitValue(Convert.FromBase64String(element.Value.Trim()), ValueKind.Binary);
                }
                catch
                {
                    node.InitValue(element.Value, ValueKind.String);
                }
                return node;
            }

            default:
                node.InitValue(element.Value, ValueKind.String);
                return node;
        }
    }

    // ====================================================================
    // 节点树 → XML plist
    // ====================================================================

    private static XElement BuildXmlElement(SaveNode node)
    {
        switch (node.Kind)
        {
            case ValueKind.Object:
            {
                var dict = new XElement("dict");
                foreach (var child in node.Children)
                {
                    dict.Add(new XElement("key", child.Name));
                    dict.Add(BuildXmlElement(child));
                }
                return dict;
            }

            case ValueKind.Array:
            {
                var array = new XElement("array");
                foreach (var child in node.Children)
                    array.Add(BuildXmlElement(child));
                return array;
            }

            case ValueKind.Boolean:
                return new XElement(node.Value is true ? "true" : "false");

            case ValueKind.Integer:
                return new XElement("integer", Convert.ToInt64(SafeValue(node, 0L)));

            case ValueKind.Float:
                return new XElement("real",
                    Convert.ToDouble(SafeValue(node, 0d)).ToString("R",
                        System.Globalization.CultureInfo.InvariantCulture));

            case ValueKind.Binary:
                return new XElement("data",
                    Convert.ToBase64String(node.Value is byte[] bytes ? bytes : Array.Empty<byte>()));

            default:
                return new XElement("string", node.Value?.ToString() ?? string.Empty);
        }
    }

    private static object SafeValue(SaveNode node, object fallback)
    {
        try { return node.Value ?? fallback; }
        catch { return fallback; }
    }

    private static int SkipBomAndSpace(ReadOnlySpan<byte> data)
    {
        int i = 0;
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) i = 3;
        while (i < data.Length && (data[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')) i++;
        return i;
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
    // 上下文与槽位
    // ====================================================================

    private sealed class PlistContext
    {
        public bool IsBinary { get; init; }
        public object? BinaryRoot { get; init; }
    }

    /// <summary>指向 binary plist 对象图中的具体位置。</summary>
    private sealed class PlistSlot
    {
        public List<KeyValuePair<string, object?>>? Dict { get; init; }
        public string? Key { get; init; }
        public List<object?>? Array { get; init; }
        public int Index { get; init; } = -1;

        public object? Get()
        {
            if (Dict is not null)
            {
                foreach (var pair in Dict)
                    if (pair.Key == Key)
                        return pair.Value;
                return null;
            }

            if (Array is not null && Index >= 0 && Index < Array.Count)
                return Array[Index];

            return null;
        }

        public void Set(object? value)
        {
            if (Dict is not null)
            {
                for (int i = 0; i < Dict.Count; i++)
                {
                    if (Dict[i].Key != Key) continue;

                    Dict[i] = new KeyValuePair<string, object?>(Key!, value);
                    return;
                }

                Dict.Add(new KeyValuePair<string, object?>(Key ?? string.Empty, value));
                return;
            }

            if (Array is not null && Index >= 0 && Index < Array.Count)
                Array[Index] = value;
        }
    }
}
