using System.Xml;
using System.Xml.Linq;

namespace SaveForge.Core.Formats;

/// <summary>
/// XML 存档格式。映射规则：
///   - 元素        → Object 节点
///   - 属性        → 以 "@" 开头的叶子
///   - 元素文本    → "#text" 叶子
///   - 同名子元素  → 保留全部，重名的第二个起加 "#2" "#3" 后缀
///
/// plist 本质也是 XML，但由 PlistFormat 优先接管（分数更高）。
/// </summary>
public sealed class XmlFormat : ISaveFormat
{
    public string Id => "xml";
    public string DisplayName => "XML 存档";
    public string[] Extensions => ["xml", "xsd", "config"];
    public bool CanWrite => true;

    public float Detect(ReadOnlySpan<byte> data, string? fileName)
    {
        int start = SkipBomAndSpace(data);
        if (start >= data.Length || data[start] != (byte)'<') return 0f;

        try
        {
            using var stream = new MemoryStream(data.ToArray());
            var document = XDocument.Load(stream);

            // plist 交给专门适配器
            if (string.Equals(document.Root?.Name.LocalName, "plist", StringComparison.OrdinalIgnoreCase))
                return 0f;

            return 0.80f;
        }
        catch
        {
            return fileName?.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) == true ? 0.15f : 0f;
        }
    }

    public SaveDocument Read(byte[] data, string? filePath)
    {
        XDocument xdoc;
        try
        {
            using var stream = new MemoryStream(data);
            xdoc = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"XML 解析失败：{ex.Message}", ex);
        }

        var rootElement = xdoc.Root
            ?? throw new InvalidDataException("XML 没有根元素");

        var document = new SaveDocument
        {
            FilePath = filePath,
            FormatId = Id,
            FormatName = DisplayName,
            OriginalBytes = data,
        };

        document.Roots.Add(BuildElement(rootElement));
        document.AcceptChanges();
        return document;
    }

    public byte[] Write(SaveDocument document)
    {
        var root = document.Roots.FirstOrDefault()
            ?? throw new InvalidOperationException("文档没有根节点");

        var xdoc = new XDocument(new XDeclaration("1.0", "utf-8", null));
        xdoc.Add(BuildXElement(root));

        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            Encoding = new UTF8Encoding(false),
        };

        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, settings))
            xdoc.Save(writer);

        return stream.ToArray();
    }

    // ====================================================================
    // XML → 节点树
    // ====================================================================

    private static SaveNode BuildElement(XElement element)
    {
        var node = new SaveNode
        {
            Name = element.Name.LocalName,
            Kind = ValueKind.Object,
        };

        foreach (var attribute in element.Attributes())
        {
            if (attribute.IsNamespaceDeclaration) continue;
            var attrNode = new SaveNode { Name = "@" + attribute.Name.LocalName };
            attrNode.InitValue(attribute.Value, ValueKind.String);
            node.Children.Add(attrNode);
        }

        // 纯文本元素（无子元素）直接作为叶子值
        bool hasChildElements = element.Elements().Any();
        string directText = string.Concat(element.Nodes().OfType<XText>().Select(t => t.Value)).Trim();

        if (!hasChildElements)
        {
            node.Kind = ValueKind.String;
            node.InitValue(directText, ValueKind.String);
            return node;
        }

        if (!string.IsNullOrWhiteSpace(directText))
        {
            var textNode = new SaveNode { Name = "#text" };
            textNode.InitValue(directText, ValueKind.String);
            node.Children.Add(textNode);
        }

        var nameCount = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var child in element.Elements())
        {
            var childNode = BuildElement(child);
            string baseName = child.Name.LocalName;

            if (nameCount.TryGetValue(baseName, out int count))
            {
                count++;
                nameCount[baseName] = count;
                childNode.Name = $"{baseName}#{count}";
            }
            else
            {
                nameCount[baseName] = 1;
            }

            node.Children.Add(childNode);
        }

        return node;
    }

    // ====================================================================
    // 节点树 → XML
    // ====================================================================

    private static XElement BuildXElement(SaveNode node)
    {
        string elementName = StripOrdinalSuffix(node.Name);
        var element = new XElement(elementName);

        if (node.Kind != ValueKind.Object)
        {
            string text = node.Value?.ToString() ?? string.Empty;
            if (text.Length > 0) element.Add(new XText(text));
            return element;
        }

        bool hasTextChild = false;

        foreach (var child in node.Children)
        {
            if (child.Name.StartsWith('@'))
            {
                element.SetAttributeValue(child.Name[1..], child.Value?.ToString() ?? string.Empty);
                continue;
            }

            if (child.Name == "#text")
            {
                element.Add(new XText(child.Value?.ToString() ?? string.Empty));
                hasTextChild = true;
                continue;
            }

            element.Add(BuildXElement(child));
        }

        if (!hasTextChild && !node.Children.Any(c => !c.Name.StartsWith('@') && c.Name != "#text"))
        {
            // 空对象：保留为自闭合空元素
        }

        return element;
    }

    private static string StripOrdinalSuffix(string name)
    {
        int hash = name.LastIndexOf('#');
        if (hash > 0 && int.TryParse(name[(hash + 1)..], out _))
            return name[..hash];
        return name;
    }

    private static int SkipBomAndSpace(ReadOnlySpan<byte> data)
    {
        int i = 0;
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) i = 3;

        while (i < data.Length && (data[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')) i++;
        return i;
    }
}
