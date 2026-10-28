using SaveForge.Core.Compression;
using SaveForge.Core.Formats;

namespace SaveForge.Core;

/// <summary>
/// 存档文件服务：打开、保存、备份。上层 UI 只需和这个类打交道。
///
/// 打开流程：
///   磁盘字节 → 尝试剥掉压缩/编码包裹 → 识别格式 → 解析成节点树
/// </summary>
public sealed class SaveFileService
{
    private readonly SaveFormatRegistry _registry;
    private readonly SaveWrapperRegistry _wrappers;

    public SaveFileService(SaveFormatRegistry? registry = null, SaveWrapperRegistry? wrappers = null)
    {
        _registry = registry ?? SaveFormatRegistry.CreateDefault();
        _wrappers = wrappers ?? SaveWrapperRegistry.CreateDefault();
    }

    public static SaveFileService Default { get; } = new();

    public SaveFormatRegistry Registry => _registry;
    public SaveWrapperRegistry Wrappers => _wrappers;

    /// <summary>
    /// 打开存档。专用格式解析失败时自动降级到十六进制视图，并保留失败原因。
    /// </summary>
    public SaveDocument Open(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"存档文件不存在：{filePath}", filePath);

        byte[] raw = File.ReadAllBytes(filePath);

        // 先直接识别一次。有些"看起来像 Base64"的文件本身就是最终格式
        // （例如 RPG Maker MV 的 LZString 输出），不能让压缩层抢先解包。
        var (directFormat, directScore) = _registry.Detect(raw, filePath);

        byte[] payload = raw;
        ISaveWrapper? wrapper = null;
        ISaveFormat format = directFormat;

        if (directScore < 0.5f)
        {
            var (unwrapped, detectedWrapper) = _wrappers.Unwrap(raw, filePath);

            if (detectedWrapper is not null)
            {
                var (innerFormat, _) = _registry.Detect(unwrapped, filePath);

                payload = unwrapped;
                wrapper = detectedWrapper;
                format = innerFormat;
            }
        }

        SaveDocument document;
        try
        {
            document = format.Read(payload, filePath);
        }
        catch (Exception ex) when (format.Id != "raw")
        {
            var fallback = _registry.Find("raw") ?? new RawFormat();
            document = fallback.Read(payload, filePath);
            document.FormatName = $"{format.DisplayName} 解析失败：{ex.Message}";
        }

        document.FilePath = filePath;
        document.OriginalBytes = raw;
        document.PayloadBytes = payload;
        document.Wrapper = wrapper;

        if (wrapper is not null)
            document.FormatName = $"{document.FormatName} · {wrapper.DisplayName}";

        return document;
    }

    /// <summary>
    /// 保存文档。写盘前自动生成 .bak 备份；写盘成功后清空修改标记。
    /// 若原文件带压缩包裹，会按原包裹重新压缩。
    /// </summary>
    public void Save(SaveDocument document, string? targetPath = null)
    {
        string target = targetPath ?? document.FilePath
            ?? throw new InvalidOperationException("没有保存路径");

        var format = _registry.Find(document.FormatId)
            ?? throw new InvalidOperationException($"未知格式：{document.FormatId}");

        if (!format.CanWrite || document.IsReadOnly)
            throw new NotSupportedException($"「{format.DisplayName}」当前为只读模式，不支持写回");

        byte[] payload = format.Write(document);
        byte[] output = document.Wrapper is null ? payload : document.Wrapper.Wrap(payload);

        if (File.Exists(target))
        {
            try { File.Copy(target, target + ".bak", overwrite: true); }
            catch { /* 备份失败不阻断保存，原文件仍在 */ }
        }

        File.WriteAllBytes(target, output);

        document.FilePath = target;
        document.OriginalBytes = output;
        document.PayloadBytes = payload;
        document.AcceptChanges();
    }

    /// <summary>手动生成一份 .bak 备份，返回备份路径。</summary>
    public string Backup(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"文件不存在：{filePath}", filePath);

        string backupPath = filePath + ".bak";
        File.Copy(filePath, backupPath, overwrite: true);
        return backupPath;
    }
}
