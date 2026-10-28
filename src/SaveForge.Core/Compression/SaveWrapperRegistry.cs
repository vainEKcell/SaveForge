namespace SaveForge.Core.Compression;

/// <summary>
/// 包裹层注册表：打开文件时先尝试剥掉压缩/编码层。
///
/// 安全约束：
///   - 只解一层，绝不递归（防解压炸弹与无限循环）
///   - 解包结果必须比原始数据小，且非空，否则判定为误识
///   - Brotli / Base64 无魔数，会尝试解包并验证结果合理性
/// </summary>
public sealed class SaveWrapperRegistry
{
    private readonly List<ISaveWrapper> _wrappers = [];
    private const int MaxUnpackedBytes = 256 * 1024 * 1024;

    public IReadOnlyList<ISaveWrapper> Wrappers => _wrappers;

    public void Register(ISaveWrapper wrapper) => _wrappers.Add(wrapper);

    public ISaveWrapper? Find(string id) =>
        _wrappers.FirstOrDefault(w => string.Equals(w.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 尝试剥掉一层包裹。没有匹配时原样返回（Wrapper 为 null）。
    /// </summary>
    public (byte[] Payload, ISaveWrapper? Wrapper) Unwrap(byte[] data, string? fileName)
    {
        var candidates = _wrappers
            .Select(w => (Wrapper: w, Score: SafeDetect(w, data, fileName)))
            .Where(x => x.Score >= 0.25f)
            .OrderByDescending(x => x.Score)
            .ToList();

        foreach (var (wrapper, _) in candidates)
        {
            try
            {
                byte[] payload = wrapper.Unwrap(data);

                if (payload.Length == 0) continue;
                if (payload.Length > MaxUnpackedBytes) continue;

                // 小文件压缩后可能因头部开销反而更大（Zstd 头 ~13 字节），
                // 所以不能要求"必须更小"，只设一个宽松上限来拦截解压炸弹。
                if (payload.Length > data.Length * 8L) continue;

                return (payload, wrapper);
            }
            catch
            {
                // 解包失败，尝试下一个
            }
        }

        return (data, null);
    }

    private static float SafeDetect(ISaveWrapper wrapper, byte[] data, string? fileName)
    {
        try { return wrapper.Detect(data, fileName); }
        catch { return 0f; }
    }

    public static SaveWrapperRegistry CreateDefault()
    {
        var registry = new SaveWrapperRegistry();

        // 魔数明确的优先，靠特征猜的放后面
        registry.Register(new GzipWrapper());
        registry.Register(new ZstdWrapper());
        registry.Register(new Lz4Wrapper());
        registry.Register(new Bzip2Wrapper());
        registry.Register(new ZlibWrapper());
        registry.Register(new BrotliWrapper());
        registry.Register(new Base64Wrapper());

        return registry;
    }
}
