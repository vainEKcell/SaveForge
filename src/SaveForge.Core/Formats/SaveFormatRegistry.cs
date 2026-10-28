namespace SaveForge.Core.Formats;

/// <summary>
/// 格式注册表。负责持有所有已注册格式，并在打开文件时选出置信度最高的一个。
/// </summary>
public sealed class SaveFormatRegistry
{
    private readonly List<ISaveFormat> _formats = new();

    public IReadOnlyList<ISaveFormat> Formats => _formats;

    public void Register(ISaveFormat format) => _formats.Add(format);

    public ISaveFormat? Find(string id) =>
        _formats.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 对所有格式跑一次 Detect，返回最高分。最低分由 RawFormat 兜底，因此必有结果。
    /// </summary>
    public (ISaveFormat Format, float Score) Detect(byte[] data, string? fileName)
    {
        ISaveFormat? best = null;
        float bestScore = float.MinValue;

        foreach (var format in _formats)
        {
            float score;
            try { score = format.Detect(data, fileName); }
            catch { score = 0f; }

            if (score > bestScore)
            {
                bestScore = score;
                best = format;
            }
        }

        best ??= new RawFormat();
        return (best, bestScore);
    }

    /// <summary>默认注册表：按"魔数明确优先、通用格式次之、兜底最后"的顺序注册。</summary>
    public static SaveFormatRegistry CreateDefault()
    {
        var registry = new SaveFormatRegistry();
        registry.Register(new UnrealFormat()); // GVAS 魔数
        registry.Register(new RenpyFormat());  // ZIP 且含 log 条目
        registry.Register(new RpgMakerFormat()); // .rxdata/.rvdata/.rvdata2 + Marshal 4.8
        registry.Register(new RpgMakerMvFormat()); // .rpgsave/.rmmzsave + LZString
        registry.Register(new MessagePackFormat()); // 无魔数，靠结构验证
        registry.Register(new CborFormat());   // 无魔数，靠结构验证
        registry.Register(new PlistFormat());  // bplist00 / <plist>
        registry.Register(new NbtFormat());    // Compound(10) 根标签
        registry.Register(new JsonFormat());   // 合法 JSON
        registry.Register(new XmlFormat());    // 通用 XML
        registry.Register(new IniFormat());    // INI / CFG
        registry.Register(new RawFormat());    // 兜底十六进制
        return registry;
    }
}
