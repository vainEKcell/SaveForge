namespace SaveForge.Core.Formats;

/// <summary>
/// MessagePack 存档格式。
///
/// MessagePack 是无 schema 的二进制序列化：类型完全由首字节决定，
/// 因此可以像 JSON 一样解析成通用对象树，不需要知道游戏的数据结构。
///
/// 支持：nil / bool / 全部整数宽度 / float32 / float64 / str / bin /
///       array / map / ext（保留原始类型字节与负载）
/// 大整数超出 long 范围时退化为十进制字符串，避免数据丢失。
/// </summary>
public sealed class MessagePackFormat : ISaveFormat
{
    public string Id => "msgpack";
    public string DisplayName => "MessagePack 存档";
    public string[] Extensions => ["msgpack", "mpack", "msgp", "msg"];
    public bool CanWrite => true;

    private const int MaxDepth = 64;
    private const int MaxChildren = 5000;

    public float Detect(ReadOnlySpan<byte> data, string? fileName)
    {
        if (data.Length < 3) return 0f;

        // 顶层必须是 map 或 array，否则不像存档
        byte first = data[0];
        bool looksContainer =
            (first & 0xF0) is 0x80 or 0x90 ||
            first is 0xDC or 0xDD or 0xDE or 0xDF;

        if (!looksContainer) return 0f;

        try
        {
            var reader = new Reader(data.ToArray());
            reader.ReadValue();

            // 必须恰好消费完全部字节，否则只是碰巧开头匹配
            if (!reader.AtEnd) return 0f;

            bool knownExtension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant()
                is ".msgpack" or ".mpack" or ".msgp";

            return knownExtension ? 0.92f : 0.68f;
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
            throw new InvalidDataException($"MessagePack 解析失败：{ex.Message}", ex);
        }

        var document = new SaveDocument
        {
            FilePath = filePath,
            FormatId = Id,
            FormatName = DisplayName,
            OriginalBytes = data,
            Tag = new MsgPackContext { Root = root },
        };

        document.Roots.Add(BuildNode(root, "存档", 0, null));
        document.AcceptChanges();

        return document;
    }

    public byte[] Write(SaveDocument document)
    {
        if (document.Tag is not MsgPackContext context)
            throw new InvalidOperationException("文档缺少 MessagePack 上下文，无法写回");

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
                if (node.Tag is not MsgPackSlot slot) continue;

                slot.Set(Coerce(node.Value, slot.Get()));
            }
        }
    }

    private static object? Coerce(object? value, object? current) => current switch
    {
        string => value?.ToString() ?? string.Empty,
        bool => value is bool flag && flag,
        long => ToInt64(value),
        double => ToDouble(value),
        float => (float)ToDouble(value),
        int => unchecked((int)ToInt64(value)),
        byte[] => value as byte[] ?? [],
        _ => value,
    };

    // ====================================================================
    // 对象图 → 节点树
    // ====================================================================

    private static SaveNode BuildNode(object? value, string name, int depth, MsgPackSlot? slot)
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

            case MsgPackExt ext:
                node.InitValue($"[ext type={ext.Type}] {FormatBytes(ext.Data)}", ValueKind.String);
                node.TypeHint = "ext";
                node.IsEditable = false;
                break;

            case MsgPackList list:
                node.InitValue(null, ValueKind.Array);
                node.TypeHint = "array";
                AppendList(node, list, depth);
                break;

            case MsgPackMap map:
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

    private static void AppendList(SaveNode node, MsgPackList list, int depth)
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
                new MsgPackSlot { List = list, Index = i }));
        }
    }

    private static void AppendMap(SaveNode node, MsgPackMap map, int depth)
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
                new MsgPackSlot { Map = map, Key = pair.Key }));
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

    private sealed class MsgPackContext
    {
        public object? Root { get; init; }
    }

    private sealed class MsgPackSlot
    {
        public MsgPackList? List { get; init; }
        public int Index { get; init; } = -1;
        public MsgPackMap? Map { get; init; }
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
                throw new InvalidDataException("MessagePack 嵌套层级过深");

            byte first = ReadByte();

            if (first <= 0x7F) return (long)first;                 // positive fixint
            if (first >= 0xE0) return (long)unchecked((sbyte)first); // negative fixint
            if ((first & 0xF0) == 0x80) return ReadMap(first & 0x0F, depth);
            if ((first & 0xF0) == 0x90) return ReadArray(first & 0x0F, depth);
            if ((first & 0xE0) == 0xA0) return ReadString(first & 0x1F);

            switch (first)
            {
                case 0xC0: return null;
                case 0xC2: return false;
                case 0xC3: return true;

                case 0xC4: return ReadBinary(ReadByte());
                case 0xC5: return ReadBinary(ReadUInt16());
                case 0xC6: return ReadBinary(CheckedLength(ReadUInt32()));

                case 0xC7: return ReadExt(ReadByte());
                case 0xC8: return ReadExt(ReadUInt16());
                case 0xC9: return ReadExt(CheckedLength(ReadUInt32()));

                case 0xCA: return (double)ReadFloat32();
                case 0xCB: return ReadFloat64();

                case 0xCC: return (long)ReadByte();
                case 0xCD: return (long)ReadUInt16();
                case 0xCE: return (long)ReadUInt32();
                case 0xCF: return ReadUInt64AsObject();

                case 0xD0: return (long)unchecked((sbyte)ReadByte());
                case 0xD1: return (long)ReadInt16();
                case 0xD2: return (long)ReadInt32();
                case 0xD3: return ReadInt64();

                case 0xD4: return ReadFixedExt(1);
                case 0xD5: return ReadFixedExt(2);
                case 0xD6: return ReadFixedExt(4);
                case 0xD7: return ReadFixedExt(8);
                case 0xD8: return ReadFixedExt(16);

                case 0xD9: return ReadString(ReadByte());
                case 0xDA: return ReadString(ReadUInt16());
                case 0xDB: return ReadString(CheckedLength(ReadUInt32()));

                case 0xDC: return ReadArray(ReadUInt16(), depth);
                case 0xDD: return ReadArray(CheckedLength(ReadUInt32()), depth);

                case 0xDE: return ReadMap(ReadUInt16(), depth);
                case 0xDF: return ReadMap(CheckedLength(ReadUInt32()), depth);

                default:
                    throw new InvalidDataException($"未知 MessagePack 类型 0x{first:X2}");
            }
        }

        private MsgPackList ReadArray(int count, int depth)
        {
            var list = new MsgPackList();
            for (int i = 0; i < count; i++) list.Add(ReadValue(depth + 1));
            return list;
        }

        private MsgPackMap ReadMap(int count, int depth)
        {
            var map = new MsgPackMap();
            for (int i = 0; i < count; i++)
            {
                object? key = ReadValue(depth + 1);
                object? value = ReadValue(depth + 1);
                map.Add(new KeyValuePair<object?, object?>(key, value));
            }
            return map;
        }

        private string ReadString(int length)
        {
            byte[] raw = ReadBytes(length);
            return Encoding.UTF8.GetString(raw);
        }

        private byte[] ReadBinary(int length)
        {
            if (length > 32 * 1024 * 1024)
                throw new InvalidDataException($"MessagePack 二进制长度异常：{length}");
            return ReadBytes(length);
        }

        private MsgPackExt ReadExt(int length)
        {
            sbyte type = unchecked((sbyte)ReadByte());
            return new MsgPackExt(type, ReadBytes(length));
        }

        private MsgPackExt ReadFixedExt(int length)
        {
            sbyte type = unchecked((sbyte)ReadByte());
            return new MsgPackExt(type, ReadBytes(length));
        }

        private object ReadUInt64AsObject()
        {
            ulong value = ReadUInt64();
            return value <= long.MaxValue ? (object)(long)value : value.ToString();
        }

        private int CheckedLength(uint value)
        {
            if (value > int.MaxValue)
                throw new InvalidDataException($"MessagePack 长度超出范围：{value}");
            return (int)value;
        }

        // ---------- 原语 ----------

        private byte ReadByte()
        {
            if (_pos >= _data.Length) throw new EndOfStreamException("MessagePack 数据意外结束");
            return _data[_pos++];
        }

        private byte[] ReadBytes(int count)
        {
            if (count < 0 || _pos + count > _data.Length)
                throw new EndOfStreamException($"MessagePack 读取越界（需要 {count}）");

            byte[] result = _data.AsSpan(_pos, count).ToArray();
            _pos += count;
            return result;
        }

        private ushort ReadUInt16()
        {
            byte[] raw = ReadBytes(2);
            return (ushort)((raw[0] << 8) | raw[1]);
        }

        private uint ReadUInt32()
        {
            byte[] raw = ReadBytes(4);
            return ((uint)raw[0] << 24) | ((uint)raw[1] << 16) | ((uint)raw[2] << 8) | raw[3];
        }

        private ulong ReadUInt64()
        {
            byte[] raw = ReadBytes(8);
            ulong value = 0;
            foreach (byte b in raw) value = (value << 8) | b;
            return value;
        }

        private short ReadInt16() => unchecked((short)ReadUInt16());

        private int ReadInt32() => unchecked((int)ReadUInt32());

        private long ReadInt64() => unchecked((long)ReadUInt64());

        private float ReadFloat32()
        {
            byte[] raw = ReadBytes(4);
            Array.Reverse(raw);
            return BitConverter.ToSingle(raw, 0);
        }

        private double ReadFloat64()
        {
            byte[] raw = ReadBytes(8);
            Array.Reverse(raw);
            return BitConverter.ToDouble(raw, 0);
        }
    }

    // ====================================================================
    // 写入
    // ====================================================================

    private static class Writer
    {
        public static void WriteValue(Stream stream, object? value)
        {
            switch (value)
            {
                case null:
                    stream.WriteByte(0xC0);
                    break;

                case bool flag:
                    stream.WriteByte(flag ? (byte)0xC3 : (byte)0xC2);
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
                {
                    stream.WriteByte(0xCA);
                    byte[] raw = BitConverter.GetBytes(single);
                    if (BitConverter.IsLittleEndian) Array.Reverse(raw);
                    stream.Write(raw, 0, raw.Length);
                    break;
                }

                case double number:
                {
                    stream.WriteByte(0xCB);
                    byte[] raw = BitConverter.GetBytes(number);
                    if (BitConverter.IsLittleEndian) Array.Reverse(raw);
                    stream.Write(raw, 0, raw.Length);
                    break;
                }

                case string text:
                    WriteString(stream, text);
                    break;

                case byte[] bytes:
                    WriteBinary(stream, bytes);
                    break;

                case MsgPackExt ext:
                    WriteExt(stream, ext);
                    break;

                case MsgPackList list:
                    WriteArrayHeader(stream, list.Count);
                    foreach (var item in list) WriteValue(stream, item);
                    break;

                case MsgPackMap map:
                    WriteMapHeader(stream, map.Count);
                    foreach (var pair in map)
                    {
                        WriteValue(stream, pair.Key);
                        WriteValue(stream, pair.Value);
                    }
                    break;

                default:
                    WriteString(stream, value.ToString() ?? string.Empty);
                    break;
            }
        }

        private static void WriteInteger(Stream stream, long value)
        {
            if (value >= 0)
            {
                if (value <= 0x7F)
                {
                    stream.WriteByte((byte)value);
                }
                else if (value <= byte.MaxValue)
                {
                    stream.WriteByte(0xCC);
                    stream.WriteByte((byte)value);
                }
                else if (value <= ushort.MaxValue)
                {
                    stream.WriteByte(0xCD);
                    WriteUInt16Be(stream, (ushort)value);
                }
                else if (value <= uint.MaxValue)
                {
                    stream.WriteByte(0xCE);
                    WriteUInt32Be(stream, (uint)value);
                }
                else
                {
                    stream.WriteByte(0xCF);
                    WriteUInt64Be(stream, (ulong)value);
                }
                return;
            }

            if (value >= -32)
            {
                stream.WriteByte(unchecked((byte)(sbyte)value));
            }
            else if (value >= sbyte.MinValue)
            {
                stream.WriteByte(0xD0);
                stream.WriteByte(unchecked((byte)(sbyte)value));
            }
            else if (value >= short.MinValue)
            {
                stream.WriteByte(0xD1);
                WriteUInt16Be(stream, unchecked((ushort)(short)value));
            }
            else if (value >= int.MinValue)
            {
                stream.WriteByte(0xD2);
                WriteUInt32Be(stream, unchecked((uint)(int)value));
            }
            else
            {
                stream.WriteByte(0xD3);
                WriteUInt64Be(stream, unchecked((ulong)value));
            }
        }

        private static void WriteInteger(Stream stream, ulong value)
        {
            if (value <= long.MaxValue)
            {
                WriteInteger(stream, (long)value);
                return;
            }

            stream.WriteByte(0xCF);
            WriteUInt64Be(stream, value);
        }

        private static void WriteString(Stream stream, string text)
        {
            byte[] raw = Encoding.UTF8.GetBytes(text);

            if (raw.Length <= 31)
            {
                stream.WriteByte((byte)(0xA0 | raw.Length));
            }
            else if (raw.Length <= byte.MaxValue)
            {
                stream.WriteByte(0xD9);
                stream.WriteByte((byte)raw.Length);
            }
            else if (raw.Length <= ushort.MaxValue)
            {
                stream.WriteByte(0xDA);
                WriteUInt16Be(stream, (ushort)raw.Length);
            }
            else
            {
                stream.WriteByte(0xDB);
                WriteUInt32Be(stream, (uint)raw.Length);
            }

            stream.Write(raw, 0, raw.Length);
        }

        private static void WriteBinary(Stream stream, byte[] data)
        {
            if (data.Length <= byte.MaxValue)
            {
                stream.WriteByte(0xC4);
                stream.WriteByte((byte)data.Length);
            }
            else if (data.Length <= ushort.MaxValue)
            {
                stream.WriteByte(0xC5);
                WriteUInt16Be(stream, (ushort)data.Length);
            }
            else
            {
                stream.WriteByte(0xC6);
                WriteUInt32Be(stream, (uint)data.Length);
            }

            stream.Write(data, 0, data.Length);
        }

        private static void WriteExt(Stream stream, MsgPackExt ext)
        {
            switch (ext.Data.Length)
            {
                case 1: stream.WriteByte(0xD4); break;
                case 2: stream.WriteByte(0xD5); break;
                case 4: stream.WriteByte(0xD6); break;
                case 8: stream.WriteByte(0xD7); break;
                case 16: stream.WriteByte(0xD8); break;
                default:
                    if (ext.Data.Length <= byte.MaxValue)
                    {
                        stream.WriteByte(0xC7);
                        stream.WriteByte((byte)ext.Data.Length);
                    }
                    else if (ext.Data.Length <= ushort.MaxValue)
                    {
                        stream.WriteByte(0xC8);
                        WriteUInt16Be(stream, (ushort)ext.Data.Length);
                    }
                    else
                    {
                        stream.WriteByte(0xC9);
                        WriteUInt32Be(stream, (uint)ext.Data.Length);
                    }
                    break;
            }

            stream.WriteByte(unchecked((byte)ext.Type));
            stream.Write(ext.Data, 0, ext.Data.Length);
        }

        private static void WriteArrayHeader(Stream stream, int count)
        {
            if (count <= 15)
            {
                stream.WriteByte((byte)(0x90 | count));
            }
            else if (count <= ushort.MaxValue)
            {
                stream.WriteByte(0xDC);
                WriteUInt16Be(stream, (ushort)count);
            }
            else
            {
                stream.WriteByte(0xDD);
                WriteUInt32Be(stream, (uint)count);
            }
        }

        private static void WriteMapHeader(Stream stream, int count)
        {
            if (count <= 15)
            {
                stream.WriteByte((byte)(0x80 | count));
            }
            else if (count <= ushort.MaxValue)
            {
                stream.WriteByte(0xDE);
                WriteUInt16Be(stream, (ushort)count);
            }
            else
            {
                stream.WriteByte(0xDF);
                WriteUInt32Be(stream, (uint)count);
            }
        }

        /// <summary>
        /// 大端写出辅助。注意不能复用 BitConverter + Reverse：
        /// 调用方传进来的本来就是大端字节数组，再反转会写反。
        /// </summary>
        private static void WriteUInt16Be(Stream stream, ushort value)
        {
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)value);
        }

        private static void WriteUInt32Be(Stream stream, uint value)
        {
            stream.WriteByte((byte)(value >> 24));
            stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)value);
        }

        private static void WriteUInt64Be(Stream stream, ulong value)
        {
            for (int i = 7; i >= 0; i--)
                stream.WriteByte((byte)(value >> (i * 8)));
        }
    }
}

// ========================================================================
// MessagePack 对象模型
// ========================================================================

internal sealed class MsgPackList : List<object?>
{
}

internal sealed class MsgPackMap : List<KeyValuePair<object?, object?>>
{
}

internal sealed class MsgPackExt
{
    public sbyte Type { get; }
    public byte[] Data { get; }

    public MsgPackExt(sbyte type, byte[] data)
    {
        Type = type;
        Data = data;
    }
}
