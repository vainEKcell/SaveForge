namespace SaveForge.Core.IO;

/// <summary>字节序。</summary>
public enum Endianness
{
    Little,
    Big,
}

/// <summary>
/// 基于内存缓冲的大小端读取器。相比 Stream 读取，边界检查更明确，
/// 解析损坏文件时能第一时间抛出可读异常而不是静默错位。
/// </summary>
public sealed class EndianReader
{
    private readonly byte[] _data;
    private readonly bool _bigEndian;
    private int _pos;

    public EndianReader(byte[] data, Endianness endian = Endianness.Little)
    {
        _data = data;
        _bigEndian = endian == Endianness.Big;
    }

    public int Position
    {
        get => _pos;
        set
        {
            if (value < 0 || value > _data.Length)
                throw new ArgumentOutOfRangeException(nameof(value), $"定位越界: {value} (长度 {_data.Length})");
            _pos = value;
        }
    }

    public int Length => _data.Length;
    public int Remaining => _data.Length - _pos;
    public bool AtEnd => _pos >= _data.Length;

    /// <summary>底层原始字节（只读用途）。</summary>
    public ReadOnlySpan<byte> Raw => _data;

    public void Seek(int offset) => Position = offset;
    public void Skip(int count) => Position = _pos + count;

    // ---------- 基础读取 ----------

    public byte ReadByte()
    {
        EnsureAvailable(1);
        return _data[_pos++];
    }

    public sbyte ReadSByte() => unchecked((sbyte)ReadByte());

    public bool ReadBoolean() => ReadByte() != 0;

    public short ReadInt16()
    {
        EnsureAvailable(2);
        int v = _bigEndian
            ? (_data[_pos] << 8) | _data[_pos + 1]
            : _data[_pos] | (_data[_pos + 1] << 8);
        _pos += 2;
        return (short)v;
    }

    public ushort ReadUInt16() => unchecked((ushort)ReadInt16());

    public int ReadInt32()
    {
        EnsureAvailable(4);
        int v = _bigEndian
            ? (_data[_pos] << 24) | (_data[_pos + 1] << 16) | (_data[_pos + 2] << 8) | _data[_pos + 3]
            : _data[_pos] | (_data[_pos + 1] << 8) | (_data[_pos + 2] << 16) | (_data[_pos + 3] << 24);
        _pos += 4;
        return v;
    }

    public uint ReadUInt32() => unchecked((uint)ReadInt32());

    public long ReadInt64()
    {
        EnsureAvailable(8);
        Span<byte> buf = stackalloc byte[8];
        _data.AsSpan(_pos, 8).CopyTo(buf);
        if (!_bigEndian) buf.Reverse();
        _pos += 8;
        return BitConverter.ToInt64(buf);
    }

    public ulong ReadUInt64() => unchecked((ulong)ReadInt64());

    public float ReadSingle()
    {
        EnsureAvailable(4);
        Span<byte> buf = stackalloc byte[4];
        _data.AsSpan(_pos, 4).CopyTo(buf);
        if (!_bigEndian) buf.Reverse();
        _pos += 4;
        return BitConverter.ToSingle(buf);
    }

    public double ReadDouble()
    {
        EnsureAvailable(8);
        Span<byte> buf = stackalloc byte[8];
        _data.AsSpan(_pos, 8).CopyTo(buf);
        if (!_bigEndian) buf.Reverse();
        _pos += 8;
        return BitConverter.ToDouble(buf);
    }

    // ---------- 批量与字符串 ----------

    public byte[] ReadBytes(int count)
    {
        if (count < 0) throw new InvalidDataException($"读取长度不能为负: {count}");
        EnsureAvailable(count);
        byte[] result = new byte[count];
        Array.Copy(_data, _pos, result, 0, count);
        _pos += count;
        return result;
    }

    /// <summary>读取 4 字节长度前缀 + 指定数量字节。</summary>
    public byte[] ReadLengthPrefixedBytes(int maxLength = 64 * 1024 * 1024)
    {
        int len = ReadInt32();
        if (len < 0 || len > maxLength)
            throw new InvalidDataException($"字节长度异常: {len}");
        return ReadBytes(len);
    }

    /// <summary>读取固定字节长度字符串，遇到 0 截断。</summary>
    public string ReadFixedString(int byteLength, StringEncoding encoding = StringEncoding.Utf8)
    {
        byte[] raw = ReadBytes(byteLength);
        int nullIndex = Array.IndexOf(raw, (byte)0);
        if (nullIndex >= 0) raw = raw[..nullIndex];
        return encoding switch
        {
            StringEncoding.Utf8 => Encoding.UTF8.GetString(raw),
            StringEncoding.Ascii => Encoding.ASCII.GetString(raw),
            StringEncoding.Utf16Little => Encoding.Unicode.GetString(raw),
            StringEncoding.Utf16Big => Encoding.BigEndianUnicode.GetString(raw),
            _ => Encoding.UTF8.GetString(raw),
        };
    }

    /// <summary>读取 int32 长度前缀 + 字符串体。</summary>
    public string ReadLengthPrefixedString(
        StringEncoding encoding = StringEncoding.Utf8,
        int maxCharacters = 1024 * 1024)
    {
        int len = ReadInt32();
        if (len < 0 || len > maxCharacters)
            throw new InvalidDataException($"字符串长度异常: {len}");

        byte[] raw = ReadBytes(encoding is StringEncoding.Utf16Little or StringEncoding.Utf16Big
            ? len * 2
            : len);

        return encoding switch
        {
            StringEncoding.Utf8 => Encoding.UTF8.GetString(raw),
            StringEncoding.Ascii => Encoding.ASCII.GetString(raw),
            StringEncoding.Utf16Little => Encoding.Unicode.GetString(raw),
            StringEncoding.Utf16Big => Encoding.BigEndianUnicode.GetString(raw),
            _ => Encoding.UTF8.GetString(raw),
        };
    }

    /// <summary>查看即将读取的字节而不前进。</summary>
    public byte PeekByte()
    {
        EnsureAvailable(1);
        return _data[_pos];
    }

    /// <summary>查看指定偏移处的 int32 而不前进（用于启发式解析）。</summary>
    public int PeekInt32(int offset)
    {
        if (offset < 0 || offset + 4 > _data.Length) return 0;
        return _bigEndian
            ? (_data[offset] << 24) | (_data[offset + 1] << 16) | (_data[offset + 2] << 8) | _data[offset + 3]
            : _data[offset] | (_data[offset + 1] << 8) | (_data[offset + 2] << 16) | (_data[offset + 3] << 24);
    }

    private void EnsureAvailable(int count)
    {
        if (_pos + count > _data.Length)
            throw new EndOfStreamException($"读取越界：需要 {count} 字节，剩余 {_data.Length - _pos}（位置 {_pos}）");
    }
}

/// <summary>字符串编码。</summary>
public enum StringEncoding
{
    Utf8,
    Ascii,
    Utf16Little,
    Utf16Big,
}

/// <summary>大小端写入器，基于内存缓冲。</summary>
public sealed class EndianWriter
{
    private readonly MemoryStream _buffer = new();
    private readonly bool _bigEndian;

    public EndianWriter(Endianness endian = Endianness.Little)
    {
        _bigEndian = endian == Endianness.Big;
    }

    public long Position
    {
        get => _buffer.Position;
        set => _buffer.Position = value;
    }

    public void WriteByte(byte value) => _buffer.WriteByte(value);

    public void WriteInt16(short value) => WriteBytesWrapped(BitConverter.GetBytes(value), 2);

    public void WriteUInt16(ushort value) => WriteInt16(unchecked((short)value));

    public void WriteInt32(int value) => WriteBytesWrapped(BitConverter.GetBytes(value), 4);

    public void WriteUInt32(uint value) => WriteInt32(unchecked((int)value));

    public void WriteInt64(long value) => WriteBytesWrapped(BitConverter.GetBytes(value), 8);

    public void WriteSingle(float value) => WriteBytesWrapped(BitConverter.GetBytes(value), 4);

    public void WriteDouble(double value) => WriteBytesWrapped(BitConverter.GetBytes(value), 8);

    public void WriteBoolean(bool value) => WriteByte(value ? (byte)1 : (byte)0);

    public void WriteBytes(byte[] data) => _buffer.Write(data, 0, data.Length);

    public void WriteLengthPrefixedString(string value, StringEncoding encoding = StringEncoding.Utf8)
    {
        byte[] raw = encoding switch
        {
            StringEncoding.Utf8 => Encoding.UTF8.GetBytes(value),
            StringEncoding.Ascii => Encoding.ASCII.GetBytes(value),
            StringEncoding.Utf16Little => Encoding.Unicode.GetBytes(value),
            StringEncoding.Utf16Big => Encoding.BigEndianUnicode.GetBytes(value),
            _ => Encoding.UTF8.GetBytes(value),
        };

        bool isWide = encoding is StringEncoding.Utf16Little or StringEncoding.Utf16Big;
        WriteInt32(isWide ? raw.Length / 2 : raw.Length);
        WriteBytes(raw);
    }

    public byte[] ToArray() => _buffer.ToArray();

    private void WriteBytesWrapped(byte[] bytes, int expected)
    {
        if (bytes.Length != expected) throw new InvalidOperationException($"字节数不符: {bytes.Length} != {expected}");
        if (_bigEndian) Array.Reverse(bytes);
        _buffer.Write(bytes, 0, bytes.Length);
    }
}
