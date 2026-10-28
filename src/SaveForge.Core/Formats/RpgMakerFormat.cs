namespace SaveForge.Core.Formats;

/// <summary>
/// RPG Maker 存档格式。
///
///   引擎版本   存档格式       内容
///   XP        .rxdata        Ruby Marshal 对象图（RPG::Save 实例）
///   VX        .rvdata        同
///   VX Ace    .rvdata2       同（Ruby 1.9，字符串带编码标记）
///
/// 写回流程：
///   1. 把界面上修改过的叶子值按原类型写回对象图（实例变量 / 数组元素 / 哈希值）
///   2. 用 RubyMarshalWriter 重新序列化（保持符号表与对象链接正确）
///   3. 覆盖原文件，写前自动备份
///
/// 字符串编码策略跟随原文件：Ruby 1.8（XP/VX）不写 I 标记，Ruby 1.9（VX Ace）写。
/// </summary>
public sealed class RpgMakerFormat : ISaveFormat
{
    public string Id => "rpgmaker";
    public string DisplayName => "RPG Maker 存档";
    public string[] Extensions => ["rxdata", "rvdata", "rvdata2"];
    public bool CanWrite => true;

    private const int MaxChildrenPerContainer = 1000;
    private const int MaxDepth = 40;

    public float Detect(ReadOnlySpan<byte> data, string? fileName)
    {
        // Marshal 头只有 04 08，区分度低，主要靠扩展名 + 尝试解码
        if (fileName is null) return 0f;

        string ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext is not (".rxdata" or ".rvdata" or ".rvdata2")) return 0f;
        if (data.Length < 2) return 0f;
        if (data[0] != 0x04 || data[1] != 0x08) return 0.2f;

        try
        {
            RubyMarshalReader.Read(data.ToArray());
            return 0.90f;
        }
        catch
        {
            return 0.25f;
        }
    }

    public SaveDocument Read(byte[] data, string? filePath)
    {
        RubyMarshalReader reader;
        try
        {
            reader = RubyMarshalReader.Read(data);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"RPG Maker 存档解析失败：{ex.Message}", ex);
        }

        var document = new SaveDocument
        {
            FilePath = filePath,
            FormatId = Id,
            FormatName = DisplayName,
            OriginalBytes = data,
            Tag = new RpgContext
            {
                Root = reader.Root,
                UseEncodingIvars = reader.UsedEncodingIvars,
            },
        };

        document.Roots.Add(BuildNode(reader.Root, "存档", 0, null));
        document.AcceptChanges();

        return document;
    }

    public byte[] Write(SaveDocument document)
    {
        if (document.Tag is not RpgContext context)
            throw new InvalidOperationException("文档缺少 RPG Maker 存档上下文，无法写回");

        ApplyChanges(document);

        return RubyMarshalWriter.Write(context.Root, context.UseEncodingIvars);
    }

    // ====================================================================
    // 修改写回对象图
    // ====================================================================

    private static void ApplyChanges(SaveDocument document)
    {
        foreach (var root in document.Roots)
        {
            foreach (var node in root.Descendants())
            {
                if (!node.IsModified) continue;
                if (node.Tag is not RubySlot slot) continue;

                object? current = slot.Get();
                slot.Set(Coerce(node.Value, current));
            }
        }
    }

    /// <summary>按原值类型转换，避免把整数写成字符串导致 RPG Maker 加载异常。</summary>
    private static object? Coerce(object? value, object? current) => current switch
    {
        string => value?.ToString() ?? string.Empty,
        byte[] => value as byte[] ?? [],
        bool => value is bool flag && flag,
        long => ToInt64(value),
        int => unchecked((int)ToInt64(value)),
        short => unchecked((short)ToInt64(value)),
        byte => unchecked((byte)ToInt64(value)),
        double => ToDouble(value),
        float => (float)ToDouble(value),
        _ => value,
    };

    // ====================================================================
    // 对象图 → 节点树
    // ====================================================================

    private static SaveNode BuildNode(object? value, string name, int depth, RubySlot? slot)
    {
        var node = new SaveNode { Name = name };

        if (depth > MaxDepth)
        {
            node.InitValue("…（层级过深，已截断）", ValueKind.String);
            node.IsEditable = false;
            return node;
        }

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

            case string text:
                node.InitValue(text, ValueKind.String);
                break;

            case byte[] bytes:
                node.InitValue(FormatBytes(bytes), ValueKind.String);
                node.IsEditable = false;
                break;

            case RubySymbol symbol:
                node.InitValue(symbol.Name, ValueKind.String);
                node.TypeHint = "symbol";
                node.IsEditable = false;
                break;

            case RubyReference reference:
                node.InitValue(reference.Name, ValueKind.String);
                node.TypeHint = "class";
                node.IsEditable = false;
                break;

            case RubyArray array:
                node.InitValue(null, ValueKind.Array);
                node.TypeHint = "array";
                AppendList(node, array, depth);
                break;

            case RubyHash hash:
                node.InitValue(null, ValueKind.Object);
                node.TypeHint = "hash";
                AppendHash(node, hash, depth);
                break;

            case RubyObject rubyObject:
                node.InitValue(null, ValueKind.Object);
                node.TypeHint = rubyObject.ClassName;
                AppendObject(node, rubyObject, depth);
                break;

            case RubyUserMarshal userMarshal:
                node.InitValue(null, ValueKind.Object);
                node.TypeHint = userMarshal.ClassName;
                AppendUserMarshal(node, userMarshal);
                break;

            default:
                node.InitValue(value.ToString() ?? "?", ValueKind.String);
                node.IsEditable = false;
                break;
        }

        // 容器没有槽位；叶子记录它在对象图中的位置，保存时据此回写
        if (!node.IsContainer && node.IsEditable)
            node.Tag = slot;

        return node;
    }

    private static void AppendList(SaveNode node, RubyArray array, int depth)
    {
        for (int index = 0; index < array.Count; index++)
        {
            if (index >= MaxChildrenPerContainer)
            {
                node.Children.Add(Truncated());
                return;
            }

            node.Children.Add(BuildNode(
                array[index],
                $"[{index}]",
                depth + 1,
                new RubySlot { Array = array, Index = index }));
        }
    }

    private static void AppendHash(SaveNode node, RubyHash hash, int depth)
    {
        int count = 0;

        foreach (var pair in hash)
        {
            if (count++ >= MaxChildrenPerContainer)
            {
                node.Children.Add(Truncated());
                return;
            }

            node.Children.Add(BuildNode(
                pair.Value,
                KeyName(pair.Key),
                depth + 1,
                new RubySlot { Hash = hash, HashKey = pair.Key }));
        }
    }

    private static void AppendObject(SaveNode node, RubyObject rubyObject, int depth)
    {
        foreach (var ivar in rubyObject.Ivars)
        {
            node.Children.Add(BuildNode(
                ivar.Value,
                ivar.Key,
                depth + 1,
                new RubySlot { Object = rubyObject, IvarName = ivar.Key }));
        }

        if (node.Children.Count == 0)
        {
            var empty = new SaveNode { Name = "(无实例变量)", IsEditable = false };
            empty.InitValue(rubyObject.ClassName, ValueKind.String);
            node.Children.Add(empty);
        }
    }

    private static void AppendUserMarshal(SaveNode node, RubyUserMarshal userMarshal)
    {
        var payloadNode = new SaveNode { Name = "$payload", IsEditable = false };
        payloadNode.InitValue(FormatBytes(userMarshal.Payload), ValueKind.String);
        node.Children.Add(payloadNode);
    }

    private static SaveNode Truncated()
    {
        var node = new SaveNode { Name = "…", IsEditable = false };
        node.InitValue("超出单节点上限，未继续展开", ValueKind.String);
        return node;
    }

    private static string KeyName(object? key) => key switch
    {
        null => "(null)",
        string text => text,
        RubySymbol symbol => symbol.Name,
        RubyReference reference => reference.Name,
        _ => key.ToString() ?? "?",
    };

    private static string FormatBytes(byte[] bytes)
    {
        int preview = Math.Min(bytes.Length, 64);
        string hex = Convert.ToHexString(bytes.AsSpan(0, preview));
        string suffix = bytes.Length > preview ? "…" : "";
        return $"[{bytes.Length} 字节] {hex}{suffix}";
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
    // 上下文与槽位
    // ====================================================================

    private sealed class RpgContext
    {
        public object? Root { get; init; }
        public bool UseEncodingIvars { get; init; }
    }

    /// <summary>记录某个叶子值在对象图中的位置。</summary>
    private sealed class RubySlot
    {
        public RubyObject? Object { get; init; }
        public string? IvarName { get; init; }
        public RubyArray? Array { get; init; }
        public int Index { get; init; } = -1;
        public RubyHash? Hash { get; init; }
        public object? HashKey { get; init; }

        public object? Get()
        {
            if (Object is not null)
            {
                foreach (var ivar in Object.Ivars)
                    if (string.Equals(ivar.Key, IvarName, StringComparison.Ordinal))
                        return ivar.Value;
                return null;
            }

            if (Array is not null && Index >= 0 && Index < Array.Count)
                return Array[Index];

            if (Hash is not null)
            {
                foreach (var pair in Hash)
                    if (Equals(pair.Key, HashKey))
                        return pair.Value;
            }

            return null;
        }

        public void Set(object? value)
        {
            if (Object is not null)
            {
                for (int i = 0; i < Object.Ivars.Count; i++)
                {
                    if (!string.Equals(Object.Ivars[i].Key, IvarName, StringComparison.Ordinal))
                        continue;

                    Object.Ivars[i] = new KeyValuePair<string, object?>(IvarName!, value);
                    return;
                }

                Object.Ivars.Add(new KeyValuePair<string, object?>(IvarName ?? string.Empty, value));
                return;
            }

            if (Array is not null && Index >= 0 && Index < Array.Count)
            {
                Array[Index] = value;
                return;
            }

            if (Hash is not null)
            {
                for (int i = 0; i < Hash.Count; i++)
                {
                    if (!Equals(Hash[i].Key, HashKey)) continue;

                    Hash[i] = new KeyValuePair<object?, object?>(HashKey, value);
                    return;
                }
            }
        }
    }
}
