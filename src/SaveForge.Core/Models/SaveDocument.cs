using SaveForge.Core.Compression;

namespace SaveForge.Core;

/// <summary>
/// 一次"打开-编辑-保存"会话的文档对象。
/// </summary>
public sealed class SaveDocument
{
    /// <summary>源文件路径。未保存的新文档为 null。</summary>
    public string? FilePath { get; set; }

    /// <summary>识别出的格式 Id，如 "json" / "ue-sav" / "nbt" / "raw"。</summary>
    public string FormatId { get; set; } = "unknown";

    /// <summary>格式的人类可读名，显示在界面上。</summary>
    public string FormatName { get; set; } = "未知格式";

    /// <summary>磁盘上的原始字节（可能带压缩包裹）。用于大小显示与备份校验。</summary>
    public byte[] OriginalBytes { get; set; } = [];

    /// <summary>剥掉压缩包裹后的明文字节，格式适配器实际解析的内容。</summary>
    public byte[] PayloadBytes { get; set; } = [];

    /// <summary>外层压缩/编码包裹。为 null 表示文件本身即明文。</summary>
    public ISaveWrapper? Wrapper { get; set; }

    /// <summary>
    /// 格式私有的会话上下文。写回时格式适配器用它取回解析器状态
    /// （如 UE 的 SaveGame 对象、pickle 的根对象图）。
    /// </summary>
    public object? Tag { get; set; }

    /// <summary>顶层节点集合。</summary>
    public List<SaveNode> Roots { get; } = new();

    /// <summary>文档是否整体只读（如 UE 存档、binary plist 仅浏览）。</summary>
    public bool IsReadOnly { get; set; }

    /// <summary>
    /// 格式猜测失败时的原因。非 null 表示已降级为十六进制视图，
    /// 用于在界面上解释"为什么这里只有原始字节"。
    /// </summary>
    public string? ParseError { get; set; }

    /// <summary>是否存在用户修改。</summary>
    public bool HasChanges => Roots.Any(r => r.Descendants().Any(n => n.IsModified));

    /// <summary>当前被修改的节点数量。</summary>
    public int ModifiedCount => Roots.Sum(r => r.Descendants().Count(n => n.IsModified));

    /// <summary>保存成功后调用：清空所有修改标记。</summary>
    public void AcceptChanges()
    {
        foreach (var root in Roots) root.ClearModified();
    }

    /// <summary>按名称查找顶层节点。</summary>
    public SaveNode? FindRoot(string name) =>
        Roots.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal));
}
