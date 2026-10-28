using System.IO.Compression;

namespace SaveForge.Core.Formats;

/// <summary>
/// Ren'Py 存档格式（.save）。
///
/// 结构：.save 本身是一个 ZIP 包，内含
///   - log          : zlib(pickle.dumps((roots, log)))  —— 游戏真实状态
///   - json         : 存档缩略信息（Ren'Py 7.4+）
///   - screenshot.png
///   - extra_info   : pickle
///
/// 写回流程：
///   1. 把界面上修改过的叶子值写回 pickle 对象图（按原类型转换）
///   2. 用 PickleWriter 以协议 2 重新序列化
///   3. zlib 压缩，替换 ZIP 包里的 log 条目，其余条目原样保留
///
/// 注意：写回属于精确重建，未修改时对象图与原始完全等价；
/// 但 Ren'Py 的运行时对象种类繁多，仍建议保留自动备份以便回滚。
/// </summary>
public sealed class RenpyFormat : ISaveFormat
{
    public string Id => "renpy";
    public string DisplayName => "Ren'Py 存档";
    public string[] Extensions => ["save"];
    public bool CanWrite => true;

    private const int MaxChildrenPerContainer = 500;
    private const int MaxDepth = 64;

    public float Detect(ReadOnlySpan<byte> data, string? fileName)
    {
        if (data.Length < 4) return 0f;

        // ZIP 本地文件头 PK\x03\x04
        if (!(data[0] == 0x50 && data[1] == 0x4B && data[2] == 0x03 && data[3] == 0x04))
            return 0f;

        try
        {
            using var stream = new MemoryStream(data.ToArray(), writable: false);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

            if (zip.Entries.Any(e => e.FullName == "log"))
                return 0.90f;

            if (zip.Entries.Any(e => e.FullName is "extra_info" or "screenshot.png"))
                return 0.55f;
        }
        catch
        {
            // 不是合法 ZIP
        }

        return 0f;
    }

    public SaveDocument Read(byte[] data, string? filePath)
    {
        var document = new SaveDocument
        {
            FilePath = filePath,
            FormatId = Id,
            FormatName = DisplayName,
            OriginalBytes = data,
        };

        using var stream = new MemoryStream(data, writable: false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        var logEntry = zip.GetEntry("log")
            ?? throw new InvalidDataException("Ren'Py 存档缺少 log 条目");

        byte[] compressed = ReadEntry(logEntry);
        byte[] pickleData = Inflate(compressed);

        object? root;
        try
        {
            root = PickleReader.Read(pickleData);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"pickle 解码失败：{ex.Message}", ex);
        }

        document.Tag = new RenpyContext { Root = root };

        // Ren'Py 保存的是 tuple(roots, rollback_log)
        if (root is PickleTuple tuple && tuple.Count >= 2)
        {
            document.Roots.Add(BuildNode(tuple[0], "存档数据", 0, SlotFor(tuple, 0)));
            document.Roots.Add(BuildNode(tuple[1], "回滚日志", 0, SlotFor(tuple, 1)));
        }
        else
        {
            document.Roots.Add(BuildNode(root, "存档数据", 0, null));
        }

        document.Roots.Add(BuildMeta(zip));
        document.AcceptChanges();

        return document;
    }

    public byte[] Write(SaveDocument document)
    {
        if (document.Tag is not RenpyContext context)
            throw new InvalidOperationException("文档缺少 Ren'Py 存档上下文，无法写回");

        ApplyChanges(document);

        byte[] pickle = PickleWriter.Write(context.Root);
        byte[] compressed = Deflate(pickle);

        return RebuildZip(document.OriginalBytes, compressed);
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
                if (node.Tag is not PickleSlot slot) continue;

                object? current = slot.Get();
                slot.Set(Coerce(node.Value, current));
            }
        }
    }

    /// <summary>按原值类型转换，避免把 int 写成 str 导致 Ren'Py 加载异常。</summary>
    private static object? Coerce(object? value, object? current) => current switch
    {
        string => value?.ToString() ?? string.Empty,
        bool => value is bool flag && flag,
        byte[] => value as byte[] ?? [],
        sbyte => unchecked((sbyte)ToInt64(value)),
        byte => unchecked((byte)ToInt64(value)),
        short => unchecked((short)ToInt64(value)),
        ushort => unchecked((ushort)ToInt64(value)),
        int => unchecked((int)ToInt64(value)),
        uint => unchecked((uint)ToInt64(value)),
        long => ToInt64(value),
        ulong => unchecked((ulong)ToInt64(value)),
        float => (float)ToDouble(value),
        double => ToDouble(value),
        _ => value,
    };

    // ====================================================================
    // 对象图槽位
    // ====================================================================

    private static PickleSlot? SlotFor(PickleTuple tuple, int index)
    {
        if (index < 0 || index >= tuple.Count) return null;
        return new PickleSlot { List = tuple, Index = index };
    }

    private static PickleSlot? SlotFor(PickleDict dict, object? key) =>
        new() { Dict = dict, Key = key };

    private static PickleSlot? SlotFor(PickleList list, int index)
    {
        if (index < 0 || index >= list.Count) return null;
        return new PickleSlot { List = list, Index = index };
    }

    // ====================================================================
    // 对象图 → 节点树
    // ====================================================================

    private static SaveNode BuildNode(object? value, string name, int depth, PickleSlot? slot)
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
                node.InitValue(bytes, ValueKind.Binary);
                node.IsEditable = false;
                break;

            case PickleDict dict:
                node.InitValue(null, ValueKind.Object);
                node.TypeHint = dict.OriginalClass ?? "dict";
                AppendDictChildren(node, dict, depth);
                break;

            case PickleTuple tuple:
                node.InitValue(null, ValueKind.Array);
                node.TypeHint = "tuple";
                AppendListChildren(node, tuple, depth);
                break;

            case PickleList list:
                node.InitValue(null, ValueKind.Array);
                node.TypeHint = list.OriginalClass ?? "list";
                AppendListChildren(node, list, depth);
                break;

            case PickleSet set:
                node.InitValue(null, ValueKind.Array);
                node.TypeHint = set.OriginalClass ?? "set";
                AppendListChildren(node, set, depth);
                break;

            case PickleInstance instance:
                node.InitValue(null, ValueKind.Object);
                node.TypeHint = instance.ClassName;
                AppendInstanceChildren(node, instance, depth);
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

    private static void AppendDictChildren(SaveNode node, PickleDict dict, int depth)
    {
        int count = 0;

        foreach (var pair in dict)
        {
            if (count++ >= MaxChildrenPerContainer)
            {
                node.Children.Add(Truncated(dict.Count));
                return;
            }

            node.Children.Add(BuildNode(
                pair.Value,
                KeyName(pair.Key),
                depth + 1,
                SlotFor(dict, pair.Key)));
        }
    }

    private static void AppendListChildren(SaveNode node, IEnumerable<object?> items, int depth)
    {
        // 需要按下标回写，因此具体容器类型单独处理
        int index = 0;

        foreach (var item in items)
        {
            if (index >= MaxChildrenPerContainer)
            {
                node.Children.Add(Truncated(index));
                return;
            }

            PickleSlot? slot = items switch
            {
                PickleList list => SlotFor(list, index),
                PickleTuple tuple => SlotFor(tuple, index),
                PickleSet => null,   // 集合无稳定下标，只读
                _ => null,
            };

            node.Children.Add(BuildNode(item, $"[{index}]", depth + 1, slot));
            index++;
        }
    }

    private static void AppendInstanceChildren(SaveNode node, PickleInstance instance, int depth)
    {
        if (instance.Args.Count > 0)
        {
            var argsNode = new SaveNode { Name = "$args", IsEditable = false };
            argsNode.InitValue(null, ValueKind.Array);
            AppendListChildren(argsNode, instance.Args, depth + 1);
            node.Children.Add(argsNode);
        }

        if (instance.State is not null)
        {
            node.Children.Add(BuildNode(instance.State, "$state", depth + 1, null));
        }

        if (node.Children.Count == 0)
        {
            var placeholder = new SaveNode { Name = "(无内容)", IsEditable = false };
            placeholder.InitValue(instance.ClassName, ValueKind.String);
            node.Children.Add(placeholder);
        }
    }

    private static SaveNode Truncated(int total)
    {
        var node = new SaveNode { Name = "…", IsEditable = false };
        node.InitValue($"另有 {total} 项未展示（超出单节点上限）", ValueKind.String);
        return node;
    }

    private static string KeyName(object? key) => key switch
    {
        null => "(null)",
        string text => text,
        byte[] bytes => $"<{Convert.ToHexString(bytes.AsSpan(0, Math.Min(8, bytes.Length)))}…>",
        PickleInstance instance => instance.ClassName,
        _ => key.ToString() ?? "?",
    };

    // ====================================================================
    // 包内元信息
    // ====================================================================

    private static SaveNode BuildMeta(ZipArchive zip)
    {
        var meta = new SaveNode { Name = "包内文件", Kind = ValueKind.Object, IsEditable = false };

        foreach (var entry in zip.Entries)
        {
            var node = new SaveNode { Name = entry.FullName, IsEditable = false };
            node.InitValue($"{entry.Length:N0} 字节", ValueKind.String);

            // json 条目是明文，顺手展示一下内容
            if (entry.FullName == "json")
            {
                try
                {
                    string text = Encoding.UTF8.GetString(ReadEntry(entry));
                    if (text.Length > 4000) text = text[..4000] + "…";
                    var preview = new SaveNode { Name = "$content", IsEditable = false };
                    preview.InitValue(text, ValueKind.String);
                    node.Children.Add(preview);
                }
                catch { /* 忽略 */ }
            }

            meta.Children.Add(node);
        }

        return meta;
    }

    // ====================================================================
    // 压缩 / ZIP 重建
    // ====================================================================

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var entryStream = entry.Open();
        using var output = new MemoryStream();
        entryStream.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>Ren'Py 用 zlib.compress 压缩 pickle；兼容尝试 gzip 与原始数据。</summary>
    private static byte[] Inflate(byte[] compressed)
    {
        try
        {
            using var input = new MemoryStream(compressed, writable: false);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            zlib.CopyTo(output);
            return output.ToArray();
        }
        catch
        {
            // 继续尝试 gzip
        }

        try
        {
            using var input = new MemoryStream(compressed, writable: false);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return output.ToArray();
        }
        catch
        {
            // 可能是未压缩的 pickle
        }

        return compressed;
    }

    private static byte[] Deflate(byte[] payload)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(payload, 0, payload.Length);
        return output.ToArray();
    }

    /// <summary>保留原 ZIP 的所有条目，只替换 log。</summary>
    private static byte[] RebuildZip(byte[] originalZip, byte[] newLog)
    {
        using var input = new MemoryStream(originalZip, writable: false);
        using var source = new ZipArchive(input, ZipArchiveMode.Read);
        using var output = new MemoryStream();

        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                var created = target.CreateEntry(entry.FullName, CompressionLevel.Optimal);

                using var destination = created.Open();

                if (entry.FullName == "log")
                {
                    destination.Write(newLog, 0, newLog.Length);
                    continue;
                }

                using var origin = entry.Open();
                origin.CopyTo(destination);
            }
        }

        return output.ToArray();
    }

    // ====================================================================
    // 上下文与槽位
    // ====================================================================

    private sealed class RenpyContext
    {
        public object? Root { get; init; }
    }

    /// <summary>记录某个叶子值在对象图中的位置，保存时据此回写。</summary>
    private sealed class PickleSlot
    {
        public PickleDict? Dict { get; init; }
        public object? Key { get; init; }
        public IList<object?>? List { get; init; }
        public int Index { get; init; } = -1;

        public object? Get()
        {
            if (Dict is not null)
                return Dict.FirstOrDefault(p => Equals(p.Key, Key)).Value;

            if (List is not null && Index >= 0 && Index < List.Count)
                return List[Index];

            return null;
        }

        public void Set(object? value)
        {
            if (Dict is not null)
            {
                for (int i = 0; i < Dict.Count; i++)
                {
                    if (!Equals(Dict[i].Key, Key)) continue;
                    Dict[i] = new KeyValuePair<object?, object?>(Key, value);
                    return;
                }

                Dict.Add(new KeyValuePair<object?, object?>(Key, value));
                return;
            }

            if (List is not null && Index >= 0 && Index < List.Count)
                List[Index] = value;
        }
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
}
