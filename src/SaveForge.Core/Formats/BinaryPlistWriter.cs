namespace SaveForge.Core.Formats;

/// <summary>
/// Binary plist（bplist00）编码器。与 <see cref="BinaryPlistReader"/> 配对。
///
/// 结构：
///   "bplist00" + 对象表 + 偏移表 + 32 字节尾部
///   尾部记录：偏移字节宽度、对象引用字节宽度、对象数、根索引、偏移表位置
///
/// 编码策略：
///   - 先把对象图扁平化成一个对象数组（父在前、子在后），每个对象记住自己的索引
///   - 再按顺序写出，同时记录每个对象的字节偏移
///   - 引用（数组元素 / 字典键值）写的是对象索引，宽度由对象总数决定
/// </summary>
internal sealed class BinaryPlistWriter
{
    private enum Kind
    {
        Null,
        Bool,
        Integer,
        Real,
        Date,
        Data,
        AsciiString,
        Utf16String,
        Array,
        Dict,
    }

    private sealed class Node
    {
        public Kind Kind;
        public object? Scalar;
        public List<int>? Children;
        public List<int>? Keys;
        public List<int>? Values;
    }

    private readonly List<Node> _objects = new();

    /// <summary>写出二进制 plist。value 支持 null/bool/整数/浮点/DateTime/byte[]/字符串/数组/字典。</summary>
    public static byte[] Write(object? root)
    {
        var writer = new BinaryPlistWriter();
        int rootIndex = writer.AddObject(root);
        return writer.Serialize(rootIndex);
    }

    // ====================================================================
    // 对象收集
    // ====================================================================

    private int AddObject(object? value)
    {
        int index = _objects.Count;
        var node = new Node();
        _objects.Add(node);   // 先占位，保证子对象索引在父之后

        switch (value)
        {
            case null:
                node.Kind = Kind.Null;
                break;

            case bool flag:
                node.Kind = Kind.Bool;
                node.Scalar = flag;
                break;

            case byte small:
                node.Kind = Kind.Integer;
                node.Scalar = (long)small;
                break;

            case sbyte signedSmall:
                node.Kind = Kind.Integer;
                node.Scalar = (long)signedSmall;
                break;

            case short shortValue:
                node.Kind = Kind.Integer;
                node.Scalar = (long)shortValue;
                break;

            case ushort ushortValue:
                node.Kind = Kind.Integer;
                node.Scalar = (long)ushortValue;
                break;

            case int integer:
                node.Kind = Kind.Integer;
                node.Scalar = (long)integer;
                break;

            case uint unsignedInteger:
                node.Kind = Kind.Integer;
                node.Scalar = (long)unsignedInteger;
                break;

            case long longValue:
                node.Kind = Kind.Integer;
                node.Scalar = longValue;
                break;

            case ulong unsignedLong:
                node.Kind = Kind.Integer;
                node.Scalar = unchecked((long)unsignedLong);
                break;

            case float single:
                node.Kind = Kind.Real;
                node.Scalar = (double)single;
                break;

            case double number:
                node.Kind = Kind.Real;
                node.Scalar = number;
                break;

            case DateTime date:
                node.Kind = Kind.Date;
                node.Scalar = date;
                break;

            case byte[] bytes:
                node.Kind = Kind.Data;
                node.Scalar = bytes;
                break;

            case string text:
                if (IsAscii(text))
                {
                    node.Kind = Kind.AsciiString;
                }
                else
                {
                    node.Kind = Kind.Utf16String;
                }
                node.Scalar = text;
                break;

            case List<KeyValuePair<string, object?>> dict:
            {
                node.Kind = Kind.Dict;
                node.Keys = new List<int>(dict.Count);
                node.Values = new List<int>(dict.Count);

                foreach (var pair in dict)
                {
                    node.Keys.Add(AddObject(pair.Key));
                    node.Values.Add(AddObject(pair.Value));
                }
                break;
            }

            case List<object?> list:
            {
                node.Kind = Kind.Array;
                node.Children = new List<int>(list.Count);
                foreach (var item in list) node.Children.Add(AddObject(item));
                break;
            }

            default:
                // 未知类型降级为字符串，避免整份存档写坏
                node.Kind = Kind.Utf16String;
                node.Scalar = value.ToString() ?? string.Empty;
                break;
        }

        return index;
    }

    // ====================================================================
    // 序列化
    // ====================================================================

    private byte[] Serialize(int rootIndex)
    {
        using var stream = new MemoryStream();

        stream.Write(Encoding.ASCII.GetBytes("bplist00"));

        var offsets = new long[_objects.Count];
        int objectRefSize = SizeOf(_objects.Count);

        for (int i = 0; i < _objects.Count; i++)
        {
            offsets[i] = stream.Position;
            WriteNode(stream, _objects[i], objectRefSize);
        }

        long offsetTableOffset = stream.Position;
        int offsetIntSize = SizeOf(offsetTableOffset);

        foreach (long offset in offsets)
            WriteBigEndian(stream, (ulong)offset, offsetIntSize);

        // 32 字节尾部
        stream.Write(new byte[6]);
        stream.WriteByte((byte)offsetIntSize);
        stream.WriteByte((byte)objectRefSize);
        WriteBigEndian(stream, (ulong)_objects.Count, 8);
        WriteBigEndian(stream, (ulong)rootIndex, 8);
        WriteBigEndian(stream, (ulong)offsetTableOffset, 8);

        return stream.ToArray();
    }

    private static void WriteNode(Stream stream, Node node, int objectRefSize)
    {
        switch (node.Kind)
        {
            case Kind.Null:
                stream.WriteByte(0x00);
                break;

            case Kind.Bool:
                stream.WriteByte(node.Scalar is true ? (byte)0x09 : (byte)0x08);
                break;

            case Kind.Integer:
                WriteInteger(stream, (long)node.Scalar!);
                break;

            case Kind.Real:
                stream.WriteByte(0x23);
                WriteBigEndian(stream, unchecked((ulong)BitConverter.DoubleToInt64Bits((double)node.Scalar!)), 8);
                break;

            case Kind.Date:
            {
                DateTime date = (DateTime)node.Scalar!;
                double seconds = (date.ToUniversalTime()
                    - new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;

                stream.WriteByte(0x33);
                WriteBigEndian(stream, unchecked((ulong)BitConverter.DoubleToInt64Bits(seconds)), 8);
                break;
            }

            case Kind.Data:
            {
                byte[] bytes = (byte[])node.Scalar!;
                WriteLengthHeader(stream, 0x40, bytes.Length);
                stream.Write(bytes, 0, bytes.Length);
                break;
            }

            case Kind.AsciiString:
            {
                byte[] raw = Encoding.ASCII.GetBytes((string)node.Scalar!);
                WriteLengthHeader(stream, 0x50, raw.Length);
                stream.Write(raw, 0, raw.Length);
                break;
            }

            case Kind.Utf16String:
            {
                string text = (string)node.Scalar!;
                byte[] raw = Encoding.BigEndianUnicode.GetBytes(text);

                // UTF-16 字符串的长度字段是字符数，不是字节数
                WriteLengthHeader(stream, 0x60, text.Length);
                stream.Write(raw, 0, raw.Length);
                break;
            }

            case Kind.Array:
            {
                WriteLengthHeader(stream, 0xA0, node.Children!.Count);
                foreach (int child in node.Children)
                    WriteBigEndian(stream, (ulong)child, objectRefSize);
                break;
            }

            case Kind.Dict:
            {
                WriteLengthHeader(stream, 0xD0, node.Keys!.Count);

                foreach (int key in node.Keys)
                    WriteBigEndian(stream, (ulong)key, objectRefSize);

                foreach (int value in node.Values!)
                    WriteBigEndian(stream, (ulong)value, objectRefSize);

                break;
            }
        }
    }

    private static void WriteInteger(Stream stream, long value)
    {
        if (value is >= 0 and <= 0xFF)
        {
            stream.WriteByte(0x10);
            stream.WriteByte((byte)value);
        }
        else if (value is >= short.MinValue and <= short.MaxValue)
        {
            stream.WriteByte(0x11);
            WriteBigEndian(stream, unchecked((ulong)(ushort)(short)value), 2);
        }
        else if (value is >= int.MinValue and <= int.MaxValue)
        {
            stream.WriteByte(0x12);
            WriteBigEndian(stream, unchecked((ulong)(uint)(int)value), 4);
        }
        else
        {
            stream.WriteByte(0x13);
            WriteBigEndian(stream, unchecked((ulong)value), 8);
        }
    }

    /// <summary>长度 &lt; 15 时直接编进类型字节，否则写 0xF + 一个整数对象表示长度。</summary>
    private static void WriteLengthHeader(Stream stream, byte baseByte, int length)
    {
        if (length < 15)
        {
            stream.WriteByte((byte)(baseByte | length));
            return;
        }

        stream.WriteByte((byte)(baseByte | 0x0F));
        WriteInteger(stream, length);
    }

    private static void WriteBigEndian(Stream stream, ulong value, int byteCount)
    {
        for (int i = byteCount - 1; i >= 0; i--)
            stream.WriteByte((byte)(value >> (i * 8)));
    }

    /// <summary>表示某个数值所需的最小字节数（1/2/4/8）。</summary>
    private static int SizeOf(long value)
    {
        if (value <= byte.MaxValue) return 1;
        if (value <= ushort.MaxValue) return 2;
        if (value <= uint.MaxValue) return 4;
        return 8;
    }

    private static bool IsAscii(string text)
    {
        foreach (char c in text)
            if (c > 0x7F) return false;
        return true;
    }
}
