namespace SaveForge.Core.Formats;

/// <summary>
/// INI / CFG 格式。Godot 的存档与配置、大量独立游戏的设置文件都是这个结构：
///
///   [Section]
///   key=value
///   ; 注释
///
/// 映射规则：
///   section  → Object 节点
///   key=value → String 叶子（值保留原样，写回时按字符串输出）
///   无 section 的键 → 放入 "全局" 节点
/// </summary>
public sealed class IniFormat : ISaveFormat
{
    public string Id => "ini";
    public string DisplayName => "INI / CFG 配置存档";
    public string[] Extensions => ["ini", "cfg", "conf", "properties"];
    public bool CanWrite => true;

    private const string GlobalSection = "全局";

    public float Detect(ReadOnlySpan<byte> data, string? fileName)
    {
        string text = DecodeText(data);
        if (string.IsNullOrWhiteSpace(text)) return 0f;

        // 排除 JSON / XML（注意：INI 的 [Section] 也以 "[" 开头，需区分）
        string trimmed = text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (trimmed.StartsWith('{')) return 0f;
        if (trimmed.StartsWith("[\"") || trimmed.StartsWith("[{") || trimmed.StartsWith("[]")) return 0f;
        if (trimmed.StartsWith('<')) return 0f;

        int sectionCount = 0;
        int pairCount = 0;

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#') || line.StartsWith("//")) continue;

            if (line.StartsWith('[') && line.EndsWith(']') && line.Length > 2)
            {
                sectionCount++;
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq > 0 && eq < line.Length - 1) pairCount++;
            else if (eq == 0) return 0f;
        }

        if (sectionCount == 0 && pairCount == 0) return 0f;
        if (sectionCount > 0 && pairCount > 0) return 0.62f;
        if (sectionCount > 0) return 0.35f;
        return pairCount >= 2 ? 0.30f : 0.12f;
    }

    public SaveDocument Read(byte[] data, string? filePath)
    {
        string text = DecodeText(data);
        var document = new SaveDocument
        {
            FilePath = filePath,
            FormatId = Id,
            FormatName = DisplayName,
            OriginalBytes = data,
        };

        var root = new SaveNode { Name = "配置", Kind = ValueKind.Object };
        SaveNode current = root;
        var sectionMap = new Dictionary<string, SaveNode>(StringComparer.Ordinal);
        int lineNumber = 0;

        foreach (string rawLine in text.Split('\n'))
        {
            lineNumber++;
            string line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#') || line.StartsWith("//"))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']') && line.Length > 2)
            {
                string sectionName = line[1..^1].Trim();
                if (!sectionMap.TryGetValue(sectionName, out var section))
                {
                    section = new SaveNode
                    {
                        Name = sectionName,
                        Kind = ValueKind.Object,
                        Locator = $"第 {lineNumber} 行",
                    };
                    sectionMap[sectionName] = section;
                    root.Children.Add(section);
                }
                current = section;
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq <= 0) continue;

            string key = line[..eq].Trim();
            string value = line[(eq + 1)..].Trim();

            var leaf = new SaveNode
            {
                Name = key,
                Locator = $"第 {lineNumber} 行",
            };
            leaf.InitValue(value, ValueKind.String);

            // 同名键保留（INI 允许重复），用序号后缀区分
            if (current.Children.Any(c => c.Name == key))
            {
                int index = 2;
                while (current.Children.Any(c => c.Name == $"{key}#{index}")) index++;
                leaf.Name = $"{key}#{index}";
            }

            current.Children.Add(leaf);
        }

        // 若存在顶层无 section 的键，把它们收进"全局"
        var globals = root.Children.Where(c => c.Kind != ValueKind.Object).ToList();
        if (globals.Count > 0 || root.Children.Count == 0)
        {
            var globalNode = new SaveNode { Name = GlobalSection, Kind = ValueKind.Object };
            foreach (var g in globals)
            {
                root.Children.Remove(g);
                globalNode.Children.Add(g);
            }
            root.Children.Insert(0, globalNode);
        }

        document.Roots.Add(root);
        document.AcceptChanges();
        return document;
    }

    public byte[] Write(SaveDocument document)
    {
        var root = document.Roots.FirstOrDefault()
            ?? throw new InvalidOperationException("文档没有根节点");

        var builder = new StringBuilder();

        foreach (var section in root.Children)
        {
            if (section.Kind != ValueKind.Object)
            {
                // 兜底：非对象节点直接输出 key=value
                builder.Append(section.Name).Append('=').Append(section.Value?.ToString()).AppendLine();
                continue;
            }

            if (section.Name != GlobalSection)
                builder.Append('[').Append(section.Name).AppendLine("]");

            foreach (var pair in section.Children)
            {
                string key = pair.Name;
                int hashIndex = key.LastIndexOf('#');
                if (hashIndex > 0 && int.TryParse(key[(hashIndex + 1)..], out _))
                    key = key[..hashIndex];

                builder.Append(key).Append('=').Append(pair.Value?.ToString() ?? string.Empty).AppendLine();
            }

            if (section.Name != GlobalSection || root.Children.Count > 1)
                builder.AppendLine();
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static string DecodeText(ReadOnlySpan<byte> data)
    {
        // UTF-8 BOM / UTF-16 BOM 探测
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            return Encoding.UTF8.GetString(data[3..]);

        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
            return Encoding.Unicode.GetString(data[2..]);

        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(data[2..]);

        // 简单判定：若含大量 0 字节则视为二进制，返回空让 Detect 放弃
        int zeroCount = 0;
        int sample = Math.Min(data.Length, 512);
        for (int i = 0; i < sample; i++)
            if (data[i] == 0) zeroCount++;

        if (sample > 0 && zeroCount > sample / 4) return string.Empty;

        return Encoding.UTF8.GetString(data);
    }
}
