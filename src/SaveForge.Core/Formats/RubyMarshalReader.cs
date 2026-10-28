namespace SaveForge.Core.Formats;

/// <summary>
/// Ruby Marshal（版本 4.8）解码器。
///
/// 用途：RPG Maker XP / VX / VX Ace 的存档（.rxdata / .rvdata / .rvdata2）
/// 是 Ruby 对象图的 Marshal 序列化结果，必须先解码才能读取游戏数据。
///
/// 支持：nil / true / false / Fixnum / Float / String / Symbol / Array / Hash /
///       Object / UserMarshal（RPG::Table、Color、Tone 等）/ Bignum / 对象链接 / IVar 包装
///
/// 不支持的标记会跳过并降级为占位，保证整份存档仍可浏览。
/// </summary>
internal sealed class RubyMarshalReader
{
    private readonly byte[] _data;
    private int _pos;

    private readonly List<RubySymbol> _symbols = new();
    private readonly List<object?> _links = new();

    private RubyMarshalReader(byte[] data) => _data = data;

    /// <summary>解码出的根对象。</summary>
    public object? Root { get; private set; }

    /// <summary>
    /// 原始数据是否使用了 Ruby 1.9+ 的字符串编码标记（I 包装）。
    /// 写回时必须保持一致：Ruby 1.8（XP/VX）不认识该标记。
    /// </summary>
    public bool UsedEncodingIvars { get; private set; }

    public static RubyMarshalReader Read(byte[] data)
    {
        var reader = new RubyMarshalReader(data);

        byte major = reader.ReadByte();
        byte minor = reader.ReadByte();

        if (major != 4 || minor != 8)
            throw new InvalidDataException($"不支持的 Ruby Marshal 版本：{major}.{minor}（仅支持 4.8）");

        reader.Root = reader.ReadValue();
        return reader;
    }

    // ====================================================================
    // 值解析
    // ====================================================================

    private object? ReadValue()
    {
        byte type = ReadByte();

        switch (type)
        {
            case (byte)'0':   // '0' nil
                return null;

            case (byte)'T':
                return true;

            case (byte)'F':
                return false;

            case (byte)'i':   // Fixnum
                return ReadLong();

            case (byte)'f':   // Float：内容是一个字符串
            {
                object? text = ReadValue();
                return double.TryParse(text?.ToString(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double number)
                    ? number
                    : 0d;
            }

            case (byte)'"':   // String
            {
                byte[] raw = ReadByteString();
                return DecodeText(raw);
            }

            case (byte)':':   // Symbol
                return ReadNewSymbol();

            case (byte)';':   // Symbol link
                return ReadSymbolLink();

            case (byte)'[':   // Array
                return ReadArray();

            case (byte)'{':   // Hash
                return ReadHash();

            case (byte)'o':   // Object
                return ReadObject();

            case (byte)'u':   // UserMarshal（RPG::Table 等）
                return ReadUserMarshal();

            case (byte)'U':   // UserDef
                return ReadUserDef();

            case (byte)'l':   // Bignum
                return ReadBignum();

            case (byte)'@':   // 对象链接
            {
                long index = ReadLong();
                return index >= 0 && index < _links.Count ? _links[(int)index] : null;
            }

            case (byte)'I':   // IVar 包装（Ruby 1.9+ 字符串编码等）
                return ReadIVarWrapped();

            case (byte)'S':   // Struct
                return ReadObject();

            case (byte)'c':   // Class
            case (byte)'m':   // Module
            {
                RubySymbol name = ReadSymbolValue();
                return new RubyReference(name.Name);
            }

            case (byte)'/':   // Regexp
            {
                ReadByteString();
                ReadByte();
                return "(正则表达式)";
            }

            default:
                throw new InvalidDataException(
                    $"不支持的 Ruby Marshal 标记 0x{type:X2}（字符 '{type}'，位置 {_pos - 1}）");
        }
    }

    // ====================================================================
    // 具体类型
    // ====================================================================

    private RubyArray ReadArray()
    {
        long count = ReadLong();
        if (count < 0 || count > 1_000_000)
            throw new InvalidDataException($"Ruby 数组长度异常：{count}");

        var array = new RubyArray();
        _links.Add(array);

        for (long i = 0; i < count; i++)
            array.Add(ReadValue());

        return array;
    }

    private RubyHash ReadHash()
    {
        long count = ReadLong();
        if (count < 0 || count > 1_000_000)
            throw new InvalidDataException($"Ruby 哈希长度异常：{count}");

        var hash = new RubyHash();
        _links.Add(hash);

        for (long i = 0; i < count; i++)
        {
            object? key = ReadValue();
            object? value = ReadValue();
            hash.Add(new KeyValuePair<object?, object?>(key, value));
        }

        return hash;
    }

    private RubyObject ReadObject()
    {
        RubySymbol className = ReadSymbolValue();
        long ivarCount = ReadLong();

        if (ivarCount < 0 || ivarCount > 100_000)
            throw new InvalidDataException($"Ruby 对象实例变量数异常：{ivarCount}");

        var rubyObject = new RubyObject(className.Name);
        _links.Add(rubyObject);

        for (long i = 0; i < ivarCount; i++)
        {
            RubySymbol name = ReadSymbolValue();
            object? value = ReadValue();
            rubyObject.Ivars.Add(new KeyValuePair<string, object?>(name.Name, value));
        }

        return rubyObject;
    }

    private RubyUserMarshal ReadUserMarshal()
    {
        RubySymbol className = ReadSymbolValue();
        byte[] payload = ReadByteString();

        return new RubyUserMarshal(className.Name, payload);
    }

    private RubyUserMarshal ReadUserDef()
    {
        RubySymbol className = ReadSymbolValue();
        byte[] payload = ReadByteString();

        return new RubyUserMarshal(className.Name, payload);
    }

    private object? ReadBignum()
    {
        byte sign = ReadByte();
        long length = ReadLong();
        if (length < 0 || length > 1024)
            throw new InvalidDataException($"Ruby 大整数长度异常：{length}");

        byte[] raw = ReadBytes((int)length);
        long value = 0;
        foreach (byte b in raw) value = (value << 8) | b;

        return sign == (byte)'-' ? -value : value;
    }

    private object? ReadIVarWrapped()
    {
        UsedEncodingIvars = true;

        object? inner = ReadValue();

        long count = ReadLong();
        if (count < 0 || count > 100)
            throw new InvalidDataException($"Ruby IVar 数量异常：{count}");

        for (long i = 0; i < count; i++)
        {
            ReadSymbolValue();   // ivar 名（如 :E 表示编码）
            ReadValue();         // ivar 值
        }

        return inner;
    }

    // ====================================================================
    // 符号
    // ====================================================================

    private RubySymbol ReadSymbolValue()
    {
        byte type = ReadByte();

        return type switch
        {
            (byte)':' => ReadNewSymbol(),
            (byte)';' => ReadSymbolLink(),
            _ => throw new InvalidDataException($"期望符号，实际标记 0x{type:X2}"),
        };
    }

    private RubySymbol ReadNewSymbol()
    {
        byte[] raw = ReadByteString();
        object decoded = DecodeText(raw);

        // 符号名必须是文本；非法编码时退回十六进制表示，保证解析不中断
        string name = decoded as string ?? Convert.ToHexString(raw);

        var symbol = new RubySymbol(name);
        _symbols.Add(symbol);
        return symbol;
    }

    private RubySymbol ReadSymbolLink()
    {
        long index = ReadLong();
        if (index < 0 || index >= _symbols.Count)
            return new RubySymbol($"sym#{index}");

        return _symbols[(int)index];
    }

    // ====================================================================
    // 原语
    // ====================================================================

    /// <summary>Ruby Marshal 的变长整数。</summary>
    private long ReadLong()
    {
        sbyte first = unchecked((sbyte)ReadByte());

        if (first == 0) return 0;

        if (first > 0)
        {
            if (first > 4)
                return first - 5;   // 单字节小正数

            long value = 0;
            for (int i = 0; i < first; i++)
                value = (value << 8) | ReadByte();
            return value;
        }

        if (first < -4)
            return first + 5;       // 单字节小负数

        long negative = -1;
        for (int i = 0; i < -first; i++)
            negative = (negative << 8) | ReadByte();
        return negative;
    }

    /// <summary>读取变长长度前缀 + 字节串。</summary>
    private byte[] ReadByteString()
    {
        long length = ReadLong();
        if (length < 0 || length > 64 * 1024 * 1024)
            throw new InvalidDataException($"Ruby 字节串长度异常：{length}");

        return ReadBytes((int)length);
    }

    private byte ReadByte()
    {
        if (_pos >= _data.Length)
            throw new EndOfStreamException("Ruby Marshal 数据意外结束");

        return _data[_pos++];
    }

    private byte[] ReadBytes(int count)
    {
        if (count < 0 || _pos + count > _data.Length)
            throw new EndOfStreamException($"Ruby Marshal 读取越界（需要 {count}）");

        byte[] result = _data.AsSpan(_pos, count).ToArray();
        _pos += count;
        return result;
    }

    /// <summary>Ruby 字符串可能是 UTF-8 或 ASCII-8BIT；优先 UTF-8。</summary>
    private static object DecodeText(byte[] raw)
    {
        try
        {
            string text = Encoding.UTF8.GetString(raw);

            // 含替换字符说明不是合法 UTF-8，保留原始字节
            if (text.Contains('\uFFFD') && raw.Any(b => b >= 0x80))
                return raw;

            return text;
        }
        catch
        {
            return raw;
        }
    }
}

// ========================================================================
// Ruby Marshal 对象模型
// ========================================================================

/// <summary>Ruby 符号（如 :@hp）。</summary>
internal sealed class RubySymbol
{
    public string Name { get; }

    public RubySymbol(string name) => Name = name;

    public override string ToString() => ":" + Name;
}

/// <summary>Ruby 数组。</summary>
internal sealed class RubyArray : List<object?>
{
}

/// <summary>Ruby 哈希（保序）。</summary>
internal sealed class RubyHash : List<KeyValuePair<object?, object?>>
{
}

/// <summary>Ruby 普通对象（如 RPG::Actor 的实例）。</summary>
internal sealed class RubyObject
{
    public string ClassName { get; }
    public List<KeyValuePair<string, object?>> Ivars { get; } = new();

    public RubyObject(string className) => ClassName = className;
}

/// <summary>Ruby UserMarshal 对象（RPG::Table / Color / Tone 等，内容为自定义二进制）。</summary>
internal sealed class RubyUserMarshal
{
    public string ClassName { get; }
    public byte[] Payload { get; }

    public RubyUserMarshal(string className, byte[] payload)
    {
        ClassName = className;
        Payload = payload;
    }
}

/// <summary>类/模块引用（如 RPG::Actor 这个类本身）。</summary>
internal sealed class RubyReference
{
    public string Name { get; }

    public RubyReference(string name) => Name = name;
}
