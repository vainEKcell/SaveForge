using UeSaveGame;
using UeSaveGame.PropertyTypes;
using UeSaveGame.StructData;
using FString = UeSaveGame.FString;
using UeProperty = UeSaveGame.FProperty;

namespace SaveForge.Core.Formats;

/// <summary>
/// Unreal Engine 存档格式（.sav / GVAS）。
///
/// 解析与写回由 UeSaveGame 库负责（Apache-2.0，支持 UE 4.26 – 5.6）。
/// 相比之前的启发式解析，这里能精确还原属性类型、并按原结构写回。
///
/// 可编辑范围：
///   - 标量属性：Int / Int64 / UInt32 / UInt64 / Float / Double / Bool /
///     Str / Name / Enum / Byte 均可直接修改
///   - StructProperty 内的属性列表（PropertiesStruct）递归展开，叶子同样可改
///   - Array / Map / Set 以及内置结构体（Vector / Guid / DateTime 等）首版只读
///
/// 写回原理：编辑节点的 Tag 保存了对应的 FProperty 实例，
/// 保存时把新值赋回 FProperty.Value，再由库按原版本重新序列化。
/// </summary>
public sealed class UnrealFormat : ISaveFormat
{
    public string Id => "ue-sav";
    public string DisplayName => "Unreal Engine 存档";
    public string[] Extensions => ["sav", "gvas"];
    public bool CanWrite => true;

    private static ReadOnlySpan<byte> Magic => "GVAS"u8;

    public float Detect(ReadOnlySpan<byte> data, string? fileName)
    {
        if (data.Length >= 4 && data[..4].SequenceEqual(Magic))
            return 0.92f;

        if (fileName?.EndsWith(".sav", StringComparison.OrdinalIgnoreCase) == true)
            return 0.30f;

        return 0f;
    }

    public SaveDocument Read(byte[] data, string? filePath)
    {
        SaveGame save;

        try
        {
            using var stream = new MemoryStream(data, writable: false);
            save = SaveGame.LoadFrom(stream);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"UE 存档解析失败：{ex.Message}", ex);
        }

        var document = new SaveDocument
        {
            FilePath = filePath,
            FormatId = Id,
            FormatName = DisplayName,
            OriginalBytes = data,
            Tag = save,
        };

        document.Roots.Add(BuildHeader(save, data.Length));

        var properties = new SaveNode { Name = "属性", Kind = ValueKind.Object };
        if (save.Properties is not null)
        {
            foreach (var tag in save.Properties)
            {
                if (tag.IsNone) continue;
                properties.Children.Add(BuildTag(tag));
            }
        }
        document.Roots.Add(properties);

        document.AcceptChanges();
        return document;
    }

    public byte[] Write(SaveDocument document)
    {
        if (document.Tag is not SaveGame save)
            throw new InvalidOperationException("文档缺少 UE 存档上下文，无法写回");

        ApplyChanges(document);

        using var output = new MemoryStream();
        save.WriteTo(output);
        return output.ToArray();
    }

    // ====================================================================
    // 头部
    // ====================================================================

    private static SaveNode BuildHeader(SaveGame save, int fileSize)
    {
        var header = new SaveNode { Name = "头部信息", Kind = ValueKind.Object, IsEditable = false };

        header.Children.Add(Text("存档类", save.SaveClass?.Value ?? "(空)"));
        header.Children.Add(Text("属性数量", (save.Properties?.Count ?? 0).ToString()));
        header.Children.Add(Text("文件大小", $"{fileSize:N0} 字节"));

        return header;
    }

    // ====================================================================
    // 属性 → 节点树
    // ====================================================================

    private static SaveNode BuildTag(FPropertyTag tag)
    {
        var property = tag.Property;

        var node = new SaveNode
        {
            Name = tag.Name.Value,
            TypeHint = tag.Type.Name.Value,
            Tag = property,
            Locator = tag.Type.Name.Value,
        };

        if (property is null)
        {
            node.InitValue("(无值)", ValueKind.String);
            node.IsEditable = false;
            return node;
        }

        object? value = property.Value;

        switch (value)
        {
            case null:
                node.InitValue(null, ValueKind.Null);
                break;

            case bool flag:
                node.InitValue(flag, ValueKind.Boolean);
                break;

            case byte small:
                node.InitValue((int)small, ValueKind.Integer);
                break;

            case sbyte signedSmall:
                node.InitValue((int)signedSmall, ValueKind.Integer);
                break;

            case short shortValue:
                node.InitValue((int)shortValue, ValueKind.Integer);
                break;

            case ushort ushortValue:
                node.InitValue((int)ushortValue, ValueKind.Integer);
                break;

            case int integer:
                node.InitValue(integer, ValueKind.Integer);
                break;

            case uint unsignedInteger:
                node.InitValue((long)unsignedInteger, ValueKind.Integer);
                break;

            case long longValue:
                node.InitValue(longValue, ValueKind.Integer);
                break;

            case ulong unsignedLong:
                node.InitValue(unchecked((long)unsignedLong), ValueKind.Integer);
                break;

            case float single:
                node.InitValue(single, ValueKind.Float);
                break;

            case double number:
                node.InitValue(number, ValueKind.Float);
                break;

            case FString text:
                node.InitValue(text.Value, ValueKind.String);
                break;

            case PropertiesStruct structValue:
                node.InitValue(null, ValueKind.Object);
                foreach (var childTag in structValue.Properties)
                {
                    if (childTag.IsNone) continue;
                    node.Children.Add(BuildTag(childTag));
                }
                break;

            case Array arrayValue:
                node.InitValue(null, ValueKind.Array);
                node.IsEditable = false;
                for (int i = 0; i < arrayValue.Length; i++)
                    node.Children.Add(BuildArrayItem(arrayValue.GetValue(i), i));
                break;

            case IStructData structData:
                // Vector / Guid / DateTime / Transform 等内置结构体：首版只读展示
                node.InitValue(structData.ToString() ?? structData.GetType().Name, ValueKind.String);
                node.IsEditable = false;
                break;

            default:
                node.InitValue(value.ToString() ?? value.GetType().Name, ValueKind.String);
                node.IsEditable = false;
                break;
        }

        return node;
    }

    private static SaveNode BuildArrayItem(object? item, int index)
    {
        var node = new SaveNode { Name = $"[{index}]", IsEditable = false };

        switch (item)
        {
            case null:
                node.InitValue(null, ValueKind.Null);
                break;

            case bool flag:
                node.InitValue(flag, ValueKind.Boolean);
                break;

            case byte small:
                node.InitValue((int)small, ValueKind.Integer);
                break;

            case int integer:
                node.InitValue(integer, ValueKind.Integer);
                break;

            case uint unsignedInteger:
                node.InitValue((long)unsignedInteger, ValueKind.Integer);
                break;

            case long longValue:
                node.InitValue(longValue, ValueKind.Integer);
                break;

            case float single:
                node.InitValue(single, ValueKind.Float);
                break;

            case double number:
                node.InitValue(number, ValueKind.Float);
                break;

            case FString text:
                node.InitValue(text.Value, ValueKind.String);
                break;

            case FPropertyTag structElement:
            {
                // struct 数组元素：递归展开，其叶子可以编辑（引用同一 FPropertyTag 实例）
                var inner = BuildTag(structElement);
                inner.Name = $"[{index}]";
                return inner;
            }

            default:
                node.InitValue(item.ToString() ?? item.GetType().Name, ValueKind.String);
                break;
        }

        return node;
    }

    // ====================================================================
    // 节点树 → 属性
    // ====================================================================

    private static void ApplyChanges(SaveDocument document)
    {
        foreach (var root in document.Roots)
        {
            foreach (var node in root.Descendants())
            {
                if (!node.IsModified) continue;
                if (node.Tag is not UeProperty property) continue;
                TryApply(property, node);
            }
        }
    }

    /// <summary>把编辑后的节点值赋回 FProperty.Values。</summary>
    private static void TryApply(UeProperty property, SaveNode node)
    {
        object? value = node.Value;

        switch (property)
        {
            case StrProperty:   // 覆盖 StrProperty 与其子类 NameProperty
            case EnumProperty:
                property.Value = new FString(value?.ToString() ?? string.Empty);
                break;

            case BoolProperty:
                property.Value = value is bool flag && flag;
                break;

            case IntProperty:
                property.Value = ToInt32(value);
                break;

            case Int64Property:
                property.Value = ToInt64(value);
                break;

            case UInt32Property:
                property.Value = unchecked((uint)ToInt64(value));
                break;

            case UInt64Property:
                property.Value = unchecked((ulong)ToInt64(value));
                break;

            case FloatProperty:
                property.Value = (float)ToDouble(value);
                break;

            case DoubleProperty:
                property.Value = ToDouble(value);
                break;

            case ByteProperty byteProperty when byteProperty.Value is FString:
                // 枚举型 ByteProperty 存的是名字
                property.Value = new FString(value?.ToString() ?? string.Empty);
                break;

            case ByteProperty:
                property.Value = unchecked((byte)ToInt64(value));
                break;

            // Text / Struct / Array / Map / Set 等复杂类型首版不支持直接改写
        }
    }

    private static int ToInt32(object? value)
    {
        try { return value is null ? 0 : Convert.ToInt32(value); }
        catch { return 0; }
    }

    private static long ToInt64(object? value)
    {
        try { return value is null ? 0L : Convert.ToInt64(value); }
        catch { return 0L; }
    }

    private static double ToDouble(object? value)
    {
        try { return value is null ? 0d : Convert.ToDouble(value); }
        catch { return 0d; }
    }

    // ====================================================================
    // 辅助
    // ====================================================================

    private static SaveNode Text(string name, string value)
    {
        var node = new SaveNode { Name = name, IsEditable = false };
        node.InitValue(value, ValueKind.String);
        return node;
    }
}
