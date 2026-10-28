namespace SaveForge.Core.Formats;

/// <summary>
/// SVDT 存档容器：部分 Unity 游戏使用的加密/压缩外壳。
///
/// 结构：
///   4 字节魔数 "SVDT" + 1 字节版本 + 高熵负载
///   负载长度通常按 16 字节对齐（AES 分组加密的特征），熵接近 8 bit/byte。
///
/// 没有该游戏自己的密钥/解码逻辑就无法还原内容，因此这里只做：
///   - 明确识别，避免被误判成 Unreal 的 .sav
///   - 提供只读的字节预览与说明
/// 绝不写回，避免把原存档写坏。
/// </summary>
public sealed class SdtFormat : ISaveFormat
{
    public string Id => "svdt";
    public string DisplayName => "SVDT 加密存档（Unity，只读）";
    public string[] Extensions => ["sav"];
    public bool CanWrite => false;

    private static ReadOnlySpan<byte> Magic => "SVDT"u8;

    public float Detect(ReadOnlySpan<byte> data, string? fileName)
    {
        if (data.Length < 8) return 0f;
        return data[..4].SequenceEqual(Magic) ? 0.96f : 0f;
    }

    public SaveDocument Read(byte[] data, string? filePath)
    {
        var document = new SaveDocument
        {
            FilePath = filePath,
            FormatId = Id,
            FormatName = DisplayName,
            OriginalBytes = data,
            PayloadBytes = data,
            IsReadOnly = true,
        };

        var root = new SaveNode
        {
            Name = "SVDT 存档容器",
            Kind = ValueKind.Object,
            IsEditable = false,
        };

        int payloadSize = Math.Max(0, data.Length - 5);
        string alignment = payloadSize > 0 && payloadSize % 16 == 0
            ? "，长度按 16 字节对齐"
            : string.Empty;

        AddInfo(root, "识别结果", "SVDT 容器（Unity 游戏常见）");
        AddInfo(root, "容器版本", data.Length > 4 ? data[4].ToString() : "未知");
        AddInfo(root, "文件大小", $"{data.Length:N0} 字节");
        AddInfo(root, "负载大小", $"{payloadSize:N0} 字节{alignment}");
        AddInfo(root, "说明",
            "负载是高熵二进制（疑似 AES 加密或压缩），需要该游戏自身的密钥才能解码。" +
            "本工具只做只读预览，不会写回文件。");

        // 复用十六进制视图，让用户仍能检查原始字节
        var raw = new RawFormat().Read(data, filePath);
        var preview = raw.Roots.FirstOrDefault();
        if (preview is not null)
        {
            foreach (var child in preview.Children)
                root.Children.Add(child);
        }

        document.Roots.Add(root);
        document.AcceptChanges();

        return document;
    }

    public byte[] Write(SaveDocument document) =>
        throw new NotSupportedException("SVDT 加密存档暂不支持写回，需要该游戏专用的解码逻辑");

    private static void AddInfo(SaveNode parent, string name, string value)
    {
        var node = new SaveNode { Name = name, IsEditable = false };
        node.InitValue(value, ValueKind.String);
        parent.Children.Add(node);
    }
}
