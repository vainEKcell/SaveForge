namespace SaveForge.Core.Formats;

/// <summary>
/// CBOR 存档格式（RFC 8949）。
///
/// CBOR 同样是自描述二进制：首字节高 3 位是主类型，低 5 位是附加信息，
/// 因此可以解析成通用对象树。相比 MessagePack，CBOR 多了「不定长容器」与「标签」。
///
/// 支持：无符号/负整数、字节串、文本串、数组、映射、标签（透传）、
///       简单值（true/false/null/undefined）、半精度/单精度/双精度浮点
/// 大整数超出 long 范围时退化为十进制字符串。
/// </summary>
public sealed class CborFormat : ISaveFormat
{
    public string Id => "cbor";
    public string DisplayName => "CBOR 存档";
    public string[] Extensions => ["cbor", "cbor2"];
    public bool CanWrite => true;

    private const int MaxDepth = 64;
    private const int MaxChildren = 5000;

    public float Detect(ReadOnlySpan<byte> data, string? fileName)
    {
        if (data.Length < 3) return 0f;

        int major = data[0] >> 5;
        bool looksContainer = major is 4 or 5;   // array / map

        if (!looksContainer) return 0f;

        try
        {
            var reader = new Reader(data.ToArray());
            reader.ReadValue();

            if (!reader.AtEnd) return 0f;

            bool knownExtension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant()
                is ".cbor" or ".cbor2";

            return knownExtension ? 0.92f : 0.66f;
        }
        catch
        {
            return 0f;
        }
    }

    public SaveDocument Read(byte[] data, string? filePath)
    {
        var reader = new Reader(data);
        object? root;

        try
        {
            root = reader.ReadValue();
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"CBOR 解析失败：{ex.Message}", ex);
        }

        var document = new SaveDocument
        {
            FilePath = filePath,
            FormatId = Id,
            FormatName = DisplayName,
            OriginalBytes = data,
            Tag = new CborContext { Root = root },
        };

        document.Roots.Add(BuildNode(root, "存档", 0, null));
        document.AcceptChanges();

        return document;
    }

    public byte[] Write(SaveDocument document)
    {
        if (document.Tag is not CborContext context)
            throw new InvalidOperationException("文档缺少 CBOR 上下文，无法写回");

        ApplyChanges(document);

        using var output = new MemoryStream();
        Writer.WriteValue(output, context.Root);
        return output.ToArray();
    }

    // ====================================================================
    // 修改写回
    // ====================================================================

    private static void ApplyChanges(SaveDocument document)
    {
        foreach (var root in document.Roots)
        {
            foreach (var node in root.Descendants())
            {
                if (!node.IsModified) continue;
                if (node.Tag is not CborSlot slot) continue;

                slot.Set(Coerce(node.Value, slot.Get()));
            }
        }
    }

    private static object? Coerce(object? value, object? current) => current switch
    {
        string => value?.ToString() ?? string.Empty,
        bool => value is bool flag && flag,
        long => ToInt64(value),
        ulong => unchecked((ulong)ToInt64(value)),
        double => ToDouble(value),
        float => (float)ToDouble(value),
        byte[] => value as byte[] ?? [],
        _ => value,
    };

    // ====================================================================
    // 对象图 → 节点树
    // ====================================================================

    private static SaveNode BuildNode(object? value, string name, int depth, CborSlot? slot)
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
                node.InitValue((long)small, ValueKind.Integer);
                break;

            case sbyte signedSmall:
                node.InitValue((long)signedSmall, ValueKind.Integer);
                break;

            case short shortValue:
                node.InitValue((long)shortValue, ValueKind.Integer);
                break;

            case ushort ushortValue:
                node.InitValue((long)ushortValue, ValueKind.Integer);
                break;

            case int integer:
                node.InitValue((long)integer, ValueKind.Integer);
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
                node.InitValue((double)single, ValueKind.Float);
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

            case CborList list:
                node.InitValue(null, ValueKind.Array);
                node.TypeHint = "array";
                AppendList(node, list, depth);
                break;

            case CborMap map:
                node.InitValue(null, ValueKind.Object);
                node.TypeHint = "map";
                AppendMap(node, map, depth);
                break;

            default:
                node.InitValue(value.ToString() ?? "?", ValueKind.String);
                node.IsEditable = false;
                break;
        }

        if (!node.IsContainer && node.IsEditable)
            node.Tag = slot;

        return node;
    }

    private static void AppendList(SaveNode node, CborList list, int depth)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (i >= MaxChildren)
            {
                node.Children.Add(Truncated());
                return;
            }

            node.Children.Add(BuildNode(
                list[i],
                $"[{i}]",
                depth + 1,
                new CborSlot { List = list, Index = i }));
        }
    }

    private static void AppendMap(SaveNode node, CborMap map, int depth)
    {
        int count = 0;

        foreach (var pair in map)
        {
            if (count++ >= MaxChildren)
            {
                node.Children.Add(Truncated());
                return;
            }

            node.Children.Add(BuildNode(
                pair.Value,
                KeyName(pair.Key),
                depth + 1,
                new CborSlot { Map = map, Key = pair.Key }));
        }
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
        byte[] bytes => $"<{Convert.ToHexString(bytes.AsSpan(0, Math.Min(8, bytes.Length)))}…>",
        _ => key.ToString() ?? "?",
    };

    private static string FormatBytes(byte[] bytes)
    {
        int preview = Math.Min(bytes.Length, 64);
        string hex = Convert.ToHexString(bytes.AsSpan(0, preview));
        return $"[{bytes.Length} 字节] {hex}{(bytes.Length > preview ? "…" : "")}";
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

    private sealed class CborContext
    {
        public object? Root { get; init; }
    }

    private sealed class CborSlot
    {
        public CborList? List { get; init; }
        public int Index { get; init; } = -1;
        public CborMap? Map { get; init; }
        public object? Key { get; init; }

        public object? Get()
        {
            if (List is not null && Index >= 0 && Index < List.Count) return List[Index];

            if (Map is not null)
            {
                foreach (var pair in Map)
                    if (Equals(pair.Key, Key))
                        return pair.Value;
            }

            return null;
        }

        public void Set(object? value)
        {
            if (List is not null && Index >= 0 && Index < List.Count)
            {
                List[Index] = value;
                return;
            }

            if (Map is null) return;

            for (int i = 0; i < Map.Count; i++)
            {
                if (!Equals(Map[i].Key, Key)) continue;

                Map[i] = new KeyValuePair<object?, object?>(Key, value);
                return;
            }
        }
    }

    // ====================================================================
    // 读取
    // ====================================================================

    private sealed class Reader
    {
        private readonly byte[] _data;
        private int _pos;

        public Reader(byte[] data) => _data = data;

        public bool AtEnd => _pos >= _data.Length;

        public object? ReadValue(int depth = 0)
        {
            if (depth > MaxDepth)
                throw new InvalidDataException("CBOR 嵌套层级过深");

            byte initial = ReadByte();
            int major = initial >> 5;
            int info = initial & 0x1F;

            switch (major)
            {
                case 0: // 无符号整数
                {
                    ulong value = ReadUnsigned(info);
                    return value <= long.MaxValue ? (object)(long)value : value.ToString();
                }

                case 1: // 负整数：-1 - n
                {
                    ulong value = ReadUnsigned(info);
                    if (value >= long.MaxValue) return "-" + value;
                    return -1L - (long)value;
                }

                case 2: // 字节串
                    return ReadByteString(info);

                case 3: // 文本串
                {
                    byte[] raw = ReadByteString(info);
                    return Encoding.UTF8.GetString(raw);
                }

                case 4: // 数组
                    return ReadArray(info, depth);

                case 5: // 映射
                    return ReadMap(info, depth);

                case 6: // 标签：读取标签号后透传内容
                    ReadUnsigned(info);
                    return ReadValue(depth + 1);

                default: // 简单值 / 浮点
                    return ReadSimple(info);
            }
        }

        private object? ReadSimple(int info)
        {
            switch (info)
            {
                case 20: return false;
                case 21: return true;
                case 22: return null;   // null
                case 23: return null;   // undefined

                case 24:
                    ReadByte();
                    return null;

                case 25: // 半精度浮点
                    return ReadHalfFloat();

                case 26: // 单精度
                {
                    byte[] raw = ReadBytes(4);
                    Array.Reverse(raw);
                    return (double)BitConverter.ToSingle(raw, 0);
                }

                case 27: // 双精度
                {
                    byte[] raw = ReadBytes(8);
                    Array.Reverse(raw);
                    return BitConverter.ToDouble(raw, 0);
                }

                case 31: // 不定长简单值（break）
                    return null;

                default:
                    return info < 20 ? (object)(long)info : null;
            }
        }

        private object ReadArray(int info, int depth)
        {
            var list = new CborList();

            if (info == 31)
            {
                while (!IsBreak())
                {
                    if (list.Count >= MaxChildren)
                        throw new InvalidDataException("CBOR 数组元素过多");
                    list.Add(ReadValue(depth + 1));
                }
                ConsumeBreak();
                return list;
            }

            long count = ReadLength(info);
            for (long i = 0; i < count; i++)
            {
                if (list.Count >= MaxChildren)
                    throw new InvalidDataException("CBOR 数组元素过多");
                list.Add(ReadValue(depth + 1));
            }

            return list;
        }

        private object ReadMap(int info, int depth)
        {
            var map = new CborMap();

            if (info == 31)
            {
                while (!IsBreak())
                {
                    object? key = ReadValue(depth + 1);
                    object? value = ReadValue(depth + 1);
                    map.Add(new KeyValuePair<object?, object?>(key, value));
                }
                ConsumeBreak();
                return map;
            }

            long count = ReadLength(info);
            for (long i = 0; i < count; i++)
            {
                object? key = ReadValue(depth + 1);
                object? value = ReadValue(depth + 1);
                map.Add(new KeyValuePair<object?, object?>(key, value));
            }

            return map;
        }

        private byte[] ReadByteString(int info)
        {
            if (info == 31)
            {
                // 不定长：分片拼接
                using var buffer = new MemoryStream();

                while (!IsBreak())
                {
                    byte chunkInitial = ReadByte();
                    int chunkInfo = chunkInitial & 0x1F;
                    if ((chunkInitial >> 5) != 2)
                        throw new InvalidDataException("CBOR 不定长字节串分片类型不匹配");

                    byte[] chunk = ReadBytes((int)ReadLength(chunkInfo));
                    buffer.Write(chunk, 0, chunk.Length);
                }

                ConsumeBreak();
                return buffer.ToArray();
            }

            return ReadBytes((int)ReadLength(info));
        }

        /// <summary>读取附加信息对应的整数（含 1/2/4/8 字节扩展）。</summary>
        private ulong ReadUnsigned(int info)
        {
            return info switch
            {
                < 24 => (ulong)info,
                24 => ReadByte(),
                25 => ReadBigEndian(2),
                26 => ReadBigEndian(4),
                27 => ReadBigEndian(8),
                _ => throw new InvalidDataException($"CBOR 附加信息不支持：{info}"),
            };
        }

        private long ReadLength(int info)
        {
            ulong value = ReadUnsigned(info);
            if (value > 32 * 1024 * 1024)
                throw new InvalidDataException($"CBOR 长度异常：{value}");
            return (long)value;
        }

        private bool IsBreak()
        {
            if (_pos >= _data.Length) throw new EndOfStreamException("CBOR 数据意外结束");
            return _data[_pos] == 0xFF;
        }

        private void ConsumeBreak()
        {
            if (_pos >= _data.Length || _data[_pos] != 0xFF)
                throw new InvalidDataException("CBOR 缺少 break 标记");
            _pos++;
        }

        private double ReadHalfFloat()
        {
            int half = (ReadByte() << 8) | ReadByte();

            int sign = (half >> 15) & 1;
            int exponent = (half >> 10) & 0x1F;
            int mantissa = half & 0x3FF;

            double value;

            if (exponent == 0)
                value = mantissa * Math.Pow(2, -24);
            else if (exponent == 31)
                value = mantissa == 0 ? double.PositiveInfinity : double.NaN;
            else
                value = (mantissa + 1024) * Math.Pow(2, exponent - 25);

            return sign == 1 ? -value : value;
        }

        private ulong ReadBigEndian(int count)
        {
            byte[] raw = ReadBytes(count);
            ulong value = 0;
            foreach (byte b in raw) value = (value << 8) | b;
            return value;
        }

        private byte ReadByte()
        {
            if (_pos >= _data.Length) throw new EndOfStreamException("CBOR 数据意外结束");
            return _data[_pos++];
        }

        private byte[] ReadBytes(int count)
        {
            if (count < 0 || _pos + count > _data.Length)
                throw new EndOfStreamException($"CBOR 读取越界（需要 {count}）");

            byte[] result = _data.AsSpan(_pos, count).ToArray();
            _pos += count;
            return result;
        }
    }

    // ====================================================================
    // 写入（全部使用定长编码，语义与不定长等价）
    // ====================================================================

    private static class Writer
    {
        public static void WriteValue(Stream stream, object? value)
        {
            switch (value)
            {
                case null:
                    stream.WriteByte(0xF6);   // null
                    break;

                case bool flag:
                    stream.WriteByte(flag ? (byte)0xF5 : (byte)0xF4);
                    break;

                case byte small:
                    WriteInteger(stream, small);
                    break;

                case sbyte signedSmall:
                    WriteInteger(stream, signedSmall);
                    break;

                case short shortValue:
                    WriteInteger(stream, shortValue);
                    break;

                case ushort ushortValue:
                    WriteInteger(stream, ushortValue);
                    break;

                case int integer:
                    WriteInteger(stream, integer);
                    break;

                case uint unsignedInteger:
                    WriteInteger(stream, unsignedInteger);
                    break;

                case long longValue:
                    WriteInteger(stream, longValue);
                    break;

                case ulong unsignedLong:
                    WriteInteger(stream, unsignedLong);
                    break;

                case float single:
                    WriteFloat(stream, single);
                    break;

                case double number:
                    WriteFloat(stream, number);
                    break;

                case string text:
                {
                    byte[] raw = Encoding.UTF8.GetBytes(text);
                    WriteHeader(stream, 3, raw.Length);
                    stream.Write(raw, 0, raw.Length);
                    break;
                }

                case byte[] bytes:
                    WriteHeader(stream, 2, bytes.Length);
                    stream.Write(bytes, 0, bytes.Length);
                    break;

                case CborList list:
                {
                    WriteHeader(stream, 4, list.Count);
                    foreach (var item in list) WriteValue(stream, item);
                    break;
                }

                case CborMap map:
                {
                    WriteHeader(stream, 5, map.Count);
                    foreach (var pair in map)
                    {
                        WriteValue(stream, pair.Key);
                        WriteValue(stream, pair.Value);
                    }
                    break;
                }

                default:
                {
                    byte[] raw = Encoding.UTF8.GetBytes(value.ToString() ?? string.Empty);
                    WriteHeader(stream, 3, raw.Length);
                    stream.Write(raw, 0, raw.Length);
                    break;
                }
            }
        }

        private static void WriteInteger(Stream stream, long value)
        {
            if (value >= 0)
            {
                WriteHeader(stream, 0, unchecked((ulong)value));
                return;
            }

            // 负数编码为 -1 - n
            ulong magnitude = unchecked((ulong)(-(value + 1)));
            WriteHeader(stream, 1, magnitude);
        }

        private static void WriteInteger(Stream stream, ulong value) =>
            WriteHeader(stream, 0, value);

        private static void WriteFloat(Stream stream, double value)
        {
            stream.WriteByte(0xFB);   // 双精度
            byte[] raw = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian) Array.Reverse(raw);
            stream.Write(raw, 0, raw.Length);
        }

        private static void WriteHeader(Stream stream, int major, int length) =>
            WriteHeader(stream, major, (ulong)length);

        private static void WriteHeader(Stream stream, int major, ulong length)
        {
            int majorBits = major << 5;

            if (length < 24)
            {
                stream.WriteByte((byte)(majorBits | (int)length));
            }
            else if (length <= byte.MaxValue)
            {
                stream.WriteByte((byte)(majorBits | 24));
                stream.WriteByte((byte)length);
            }
            else if (length <= ushort.MaxValue)
            {
                stream.WriteByte((byte)(majorBits | 25));
                stream.WriteByte((byte)(length >> 8));
                stream.WriteByte((byte)length);
            }
            else if (length <= uint.MaxValue)
            {
                stream.WriteByte((byte)(majorBits | 26));
                byte[] raw = BitConverter.GetBytes((uint)length);
                if (BitConverter.IsLittleEndian) Array.Reverse(raw);
                stream.Write(raw, 0, raw.Length);
            }
            else
            {
                stream.WriteByte((byte)(majorBits | 27));
                byte[] raw = BitConverter.GetBytes(length);
                if (BitConverter.IsLittleEndian) Array.Reverse(raw);
                stream.Write(raw, 0, raw.Length);
            }
        }
    }
}

// ========================================================================
// CBOR 对象模型
// ========================================================================

internal sealed class CborList : List<object?>
{
}

internal sealed class CborMap : List<KeyValuePair<object?, object?>>
{
}
