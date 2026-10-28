namespace SaveForge.Core.Formats;

/// <summary>
/// Binary plist（bplist00）解码器。
///
/// 结构：8 字节魔数 + 对象表 + 偏移表 + 32 字节尾部。
/// 尾部记录：偏移量字节宽度、对象引用字节宽度、对象数、根对象索引、偏移表位置。
/// </summary>
internal sealed class BinaryPlistReader
{
    private const int TrailerSize = 32;
    private const int MaxDepth = 200;

    private readonly byte[] _data;
    private long[] _offsets = [];
    private int _objectRefSize;
    private int _topObject;

    public BinaryPlistReader(byte[] data) => _data = data;

    public object? Parse()
    {
        if (_data.Length < 8 + TrailerSize + 1)
            throw new InvalidDataException("binary plist 文件过短");

        int trailer = _data.Length - TrailerSize;

        int offsetIntSize = _data[trailer + 6];
        _objectRefSize = _data[trailer + 7];

        if (offsetIntSize is < 1 or > 8 || _objectRefSize is < 1 or > 8)
            throw new InvalidDataException($"binary plist 尾部参数异常：offsetSize={offsetIntSize}, refSize={_objectRefSize}");

        ulong numObjects = ReadUnsigned(trailer + 8, 8);
        _topObject = (int)ReadUnsigned(trailer + 16, 8);
        ulong offsetTableOffset = ReadUnsigned(trailer + 24, 8);

        if (numObjects == 0 || numObjects > 10_000_000)
            throw new InvalidDataException($"binary plist 对象数量异常：{numObjects}");

        long tableStart = (long)offsetTableOffset;
        if (tableStart < 0 || tableStart + (long)numObjects * offsetIntSize > _data.Length)
            throw new InvalidDataException("binary plist 偏移表越界");

        _offsets = new long[numObjects];
        for (int i = 0; i < (int)numObjects; i++)
            _offsets[i] = (long)ReadUnsigned((int)(tableStart + (long)i * offsetIntSize), offsetIntSize);

        return ParseObject(_topObject, 0);
    }

    private object? ParseObject(int index, int depth)
    {
        if (depth > MaxDepth)
            throw new InvalidDataException("binary plist 嵌套层级过深，可能文件损坏");

        if (index < 0 || index >= _offsets.Length)
            throw new InvalidDataException($"binary plist 对象索引越界：{index}");

        long offset = _offsets[index];
        if (offset < 0 || offset >= _data.Length)
            throw new InvalidDataException($"binary plist 对象偏移越界：{offset}");

        byte marker = _data[offset];
        int type = marker >> 4;
        int info = marker & 0x0F;
        int pos = (int)offset + 1;

        switch (type)
        {
            case 0x0: // 简单类型
                if (info == 0x08) return false;
                if (info == 0x09) return true;
                return null;

            case 0x1: // 整数（大端）
            {
                int byteCount = 1 << info;
                if (byteCount > 8) byteCount = 8;
                return (long)ReadUnsigned(pos, byteCount);
            }

            case 0x2: // 浮点（大端）
            {
                int byteCount = 1 << info;
                if (byteCount == 4) return (double)ReadSingle(pos);
                if (byteCount == 8) return ReadDouble(pos);
                return 0d;
            }

            case 0x3: // 日期：自 2001-01-01 UTC 起的秒数
                return new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                    .AddSeconds(ReadDouble(pos))
                    .ToLocalTime();

            case 0x4: // 二进制数据
            {
                var (length, dataPos) = ReadLength(info, pos);
                EnsureRange(dataPos, length);
                return _data.AsSpan(dataPos, length).ToArray();
            }

            case 0x5: // ASCII 字符串
            {
                var (length, dataPos) = ReadLength(info, pos);
                EnsureRange(dataPos, length);
                return Encoding.ASCII.GetString(_data, dataPos, length);
            }

            case 0x6: // UTF-16BE 字符串
            {
                var (length, dataPos) = ReadLength(info, pos);
                EnsureRange(dataPos, length * 2);
                return Encoding.BigEndianUnicode.GetString(_data, dataPos, length * 2);
            }

            case 0x8: // UID
            {
                int byteCount = info + 1;
                return (long)ReadUnsigned(pos, byteCount);
            }

            case 0xA: // 数组
            {
                var (count, refPos) = ReadLength(info, pos);
                if (count > 1_000_000) throw new InvalidDataException($"binary plist 数组过大：{count}");

                var items = new List<object?>(count);
                for (int i = 0; i < count; i++)
                {
                    int childIndex = (int)ReadUnsigned(refPos + i * _objectRefSize, _objectRefSize);
                    items.Add(ParseObject(childIndex, depth + 1));
                }
                return items;
            }

            case 0xD: // 字典：前半是 key 引用，后半是 value 引用
            {
                var (count, keyPos) = ReadLength(info, pos);
                if (count > 1_000_000) throw new InvalidDataException($"binary plist 字典过大：{count}");

                var pairs = new List<KeyValuePair<string, object?>>(count);
                for (int i = 0; i < count; i++)
                {
                    int keyIndex = (int)ReadUnsigned(keyPos + i * _objectRefSize, _objectRefSize);
                    int valueIndex = (int)ReadUnsigned(keyPos + (count + i) * _objectRefSize, _objectRefSize);

                    string key = ParseObject(keyIndex, depth + 1)?.ToString() ?? $"key{i}";
                    pairs.Add(new KeyValuePair<string, object?>(key, ParseObject(valueIndex, depth + 1)));
                }
                return pairs;
            }

            default:
                return null;
        }
    }

    // ====================================================================
    // 基础读取
    // ====================================================================

    private (int Length, int DataPos) ReadLength(int info, int pos)
    {
        if (info != 0x0F)
            return (info, pos);

        byte marker = _data[pos];
        int intInfo = marker & 0x0F;
        int byteCount = 1 << intInfo;
        ulong length = ReadUnsigned(pos + 1, byteCount);

        if (length > int.MaxValue)
            throw new InvalidDataException($"binary plist 长度异常：{length}");

        return ((int)length, pos + 1 + byteCount);
    }

    private ulong ReadUnsigned(int pos, int count)
    {
        EnsureRange(pos, count);
        ulong value = 0;
        for (int i = 0; i < count; i++)
            value = (value << 8) | _data[pos + i];
        return value;
    }

    private float ReadSingle(int pos)
    {
        EnsureRange(pos, 4);
        byte[] buf = _data.AsSpan(pos, 4).ToArray();
        Array.Reverse(buf);
        return BitConverter.ToSingle(buf, 0);
    }

    private double ReadDouble(int pos)
    {
        EnsureRange(pos, 8);
        byte[] buf = _data.AsSpan(pos, 8).ToArray();
        Array.Reverse(buf);
        return BitConverter.ToDouble(buf, 0);
    }

    private void EnsureRange(int pos, int count)
    {
        if (pos < 0 || count < 0 || pos + count > _data.Length)
            throw new InvalidDataException($"binary plist 读取越界：pos={pos}, count={count}, 文件长度={_data.Length}");
    }
}
