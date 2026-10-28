namespace SaveForge.Core;

/// <summary>
/// 存档字段的值类型。所有格式（JSON / UE / 二进制）解析后都映射到这套统一类型，
/// 编辑器只认这一套，不需要关心来源格式。
/// </summary>
public enum ValueKind
{
    /// <summary>对象/字典容器（JSON object、UE FStructProperty）。</summary>
    Object,

    /// <summary>数组/列表容器（JSON array、UE ArrayProperty）。</summary>
    Array,

    /// <summary>字符串。</summary>
    String,

    /// <summary>整数（int/long/short/byte 统一收敛）。</summary>
    Integer,

    /// <summary>浮点（float/double 统一收敛）。</summary>
    Float,

    /// <summary>布尔。</summary>
    Boolean,

    /// <summary>原始字节块（不可直接编辑）。</summary>
    Binary,

    /// <summary>空值（JSON null）。</summary>
    Null,
}

/// <summary>
/// 存档树节点。一个节点要么是容器（Object/Array，有 Children），要么是叶子（有 Value）。
/// </summary>
public sealed class SaveNode
{
    /// <summary>字段名 / JSON 键 / 数组下标显示的文本。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 可读别名。仅用于界面展示，不影响回写（回写始终用 Name）。
    /// 典型用途：RPG Maker 存档里的物品 ID "51" → "樱花鳟"。
    /// </summary>
    public string? Alias { get; set; }

    /// <summary>界面展示用的名称：有别名时显示「键 · 别名」。</summary>
    public string Label => string.IsNullOrWhiteSpace(Alias) ? Name : $"{Name} · {Alias}";

    /// <summary>值类型。</summary>
    public ValueKind Kind { get; set; }

    /// <summary>叶子节点的原始值。容器为 null。</summary>
    public object? Value { get; private set; }

    /// <summary>子节点。仅容器有。</summary>
    public List<SaveNode> Children { get; } = new();

    /// <summary>
    /// 定位信息：二进制格式记录字节偏移（如 "0x01A4"），
    /// JSON 格式记录 JsonPointer（如 "/Player/Level"）。保存时据此回写。
    /// </summary>
    public string? Locator { get; set; }

    /// <summary>是否允许编辑。UE 存档第一版只读浏览，会置 false。</summary>
    public bool IsEditable { get; set; } = true;

    /// <summary>
    /// 格式私有的类型标记。用于回写时还原精确的底层类型
    /// （如 NBT 的 byte/short/int/long 在编辑器里统一显示为整数，但写回必须知道原始宽度）。
    /// </summary>
    public string? TypeHint { get; set; }

    /// <summary>
    /// 格式私有的对象引用。用于写回时定位到底层结构体
    /// （如 UE 的 FProperty 实例、Python pickle 的对象节点）。
    /// </summary>
    public object? Tag { get; set; }

    /// <summary>是否被用户修改过（用于保存判定与 UI 高亮）。</summary>
    public bool IsModified { get; private set; }

    public bool IsContainer => Kind is ValueKind.Object or ValueKind.Array;

    /// <summary>UI 展示用的值文本。</summary>
    public string DisplayValue => Kind switch
    {
        ValueKind.Null => "null",
        ValueKind.Binary => Value is byte[] b ? $"<{b.Length} 字节>" : "<binary>",
        ValueKind.Boolean => Value is true ? "true" : "false",
        ValueKind.Object => $"{{ {Children.Count} 个字段 }}",
        ValueKind.Array => $"[ {Children.Count} 项 ]",
        _ => Value?.ToString() ?? string.Empty,
    };

    /// <summary>设置值并标记修改。</summary>
    public void SetValue(object? value)
    {
        Value = value;
        Kind = InferKind(value, Kind);
        IsModified = true;
    }

    /// <summary>解析后初始化值，不算用户修改。</summary>
    internal void InitValue(object? value, ValueKind kind)
    {
        Value = value;
        Kind = kind;
        IsModified = false;
    }

    internal void ClearModified()
    {
        IsModified = false;
        foreach (var c in Children) c.ClearModified();
    }

    private static ValueKind InferKind(object? value, ValueKind fallback) => value switch
    {
        null => ValueKind.Null,
        bool => ValueKind.Boolean,
        string => ValueKind.String,
        byte[] => ValueKind.Binary,
        sbyte or byte or short or ushort or int or uint or long or ulong => ValueKind.Integer,
        float or double or decimal => ValueKind.Float,
        _ => fallback,
    };

    /// <summary>深度遍历（含自身）。</summary>
    public IEnumerable<SaveNode> Descendants()
    {
        yield return this;
        foreach (var child in Children)
        {
            foreach (var node in child.Descendants())
                yield return node;
        }
    }
}
