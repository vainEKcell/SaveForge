namespace SaveForge.Core.Formats;

/// <summary>
/// 兜底格式：任何无法识别的文件都用十六进制视图打开，只读。
/// 保证"打不开的游戏存档"至少能看一眼原始内容。
/// </summary>
public sealed class RawFormat : ISaveFormat
{
    public string Id => "raw";
    public string DisplayName => "未知格式（十六进制视图）";
    public string[] Extensions => [];
    public bool CanWrite => false;

    private const int PreviewLimit = 64 * 1024;

    public float Detect(ReadOnlySpan<byte> data, string? fileName) => 0.01f;

    public SaveDocument Read(byte[] data, string? filePath)
    {
        var document = new SaveDocument
        {
            FilePath = filePath,
            FormatId = Id,
            FormatName = DisplayName,
            OriginalBytes = data,
            IsReadOnly = true,
        };

        var root = new SaveNode { Name = "十六进制视图", Kind = ValueKind.Object, IsEditable = false };

        int limit = Math.Min(data.Length, PreviewLimit);
        for (int offset = 0; offset < limit; offset += 16)
        {
            int count = Math.Min(16, limit - offset);
            var slice = data.AsSpan(offset, count);

            string hex = string.Join(' ', slice.ToArray().Select(b => b.ToString("X2")));
            string ascii = new(slice.ToArray().Select(b => b is >= 32 and < 127 ? (char)b : '.').ToArray());

            var line = new SaveNode { Name = $"0x{offset:X6}", IsEditable = false };
            line.InitValue($"{hex,-48}  {ascii}", ValueKind.String);
            root.Children.Add(line);
        }

        if (data.Length > PreviewLimit)
        {
            var note = new SaveNode { Name = "提示", IsEditable = false };
            note.InitValue($"文件共 {data.Length:N0} 字节，此处仅预览前 {PreviewLimit / 1024} KB", ValueKind.String);
            root.Children.Insert(0, note);
        }

        document.Roots.Add(root);
        document.AcceptChanges();
        return document;
    }

    public byte[] Write(SaveDocument document) =>
        throw new NotSupportedException("未知格式不支持写回");
}
