namespace SaveForge.Core.Formats;

/// <summary>
/// 存档格式适配器契约。每种格式实现"能不能识别"、"怎么读成树"、"怎么从树写回"三件事。
/// </summary>
public interface ISaveFormat
{
    /// <summary>格式唯一 Id，如 "json" / "ue-sav" / "raw"。</summary>
    string Id { get; }

    /// <summary>界面显示名。</summary>
    string DisplayName { get; }

    /// <summary>支持的扩展名（不含点）。</summary>
    string[] Extensions { get; }

    /// <summary>该格式是否支持把修改写回文件。只读格式（如第一版 UE）返回 false。</summary>
    bool CanWrite { get; }

    /// <summary>
    /// 识别置信度 0-1。给一段字节和可选文件名，判断"我是不是这种格式"。
    /// 注册表会取最高分的格式。
    /// </summary>
    float Detect(ReadOnlySpan<byte> data, string? fileName);

    /// <summary>把原始字节解析成文档树。实现须自行处理内部结构，不抛未处理异常。</summary>
    SaveDocument Read(byte[] data, string? filePath);

    /// <summary>把文档树写回字节。CanWrite=false 的格式不应被调用。</summary>
    byte[] Write(SaveDocument document);
}
