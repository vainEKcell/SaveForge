namespace SaveForge.Core.Formats;

/// <summary>
/// Ruby Marshal（4.8）编码器。与 <see cref="RubyMarshalReader"/> 配对。
///
/// 关键机制：
///   - 符号表：重复的符号用 ";" + 索引 引用，避免重复写入
///   - 对象链接表：Array / Hash / Object 首次出现时登记索引，重复引用写 "@" + 索引，
///     这样既能处理共享引用，也能正确处理循环引用（先登记再写内容）
///   - 字符串编码：按原始文件是否使用 Ruby 1.9+ 的 I 包装来决定，保证 Ruby 1.8（XP/VX）
///     与 Ruby 1.9（VX Ace）都能读取
/// </summary>
internal sealed class RubyMarshalWriter
{
    private readonly MemoryStream _output = new();

    private readonly List<string> _symbols = new();
    private readonly Dictionary<string, int> _symbolIndex = new(StringComparer.Ordinal);

    private readonly List<object> _links = new();
    private readonly Dictionary<object, int> _linkIndex = new(ReferenceEqualityComparer.Instance);

    private readonly bool _useEncodingIvars;

    private RubyMarshalWriter(bool useEncodingIvars) => _useEncodingIvars = useEncodingIvars;

    public static byte[] Write(object? value, bool useEncodingIvars)
    {
        var writer = new RubyMarshalWriter(useEncodingIvars);

        writer.WriteByte(0x04);
        writer.WriteByte(0x08);
        writer.WriteValue(value);
        writer._output.Flush();

        return writer._output.ToArray();
    }

    // ====================================================================
    // 分派
    // ====================================================================

    private void WriteValue(object? value)
    {
        switch (value)
        {
            case null:
                WriteByte((byte)'0');
                break;

            case bool flag:
                WriteByte(flag ? (byte)'T' : (byte)'F');
                break;

            case byte small:
                WriteInteger(small);
                break;

            case sbyte signedSmall:
                WriteInteger(signedSmall);
                break;

            case short shortValue:
                WriteInteger(shortValue);
                break;

            case ushort ushortValue:
                WriteInteger(ushortValue);
                break;

            case int integer:
                WriteInteger(integer);
                break;

            case uint unsignedInteger:
                WriteInteger(unsignedInteger);
                break;

            case long longValue:
                WriteInteger(longValue);
                break;

            case ulong unsignedLong:
                WriteInteger(unchecked((long)unsignedLong));
                break;

            case float single:
                WriteFloat(single);
                break;

            case double number:
                WriteFloat(number);
                break;

            case string text:
                WriteString(text);
                break;

            case byte[] bytes:
                WriteRawBytes(bytes);
                break;

            case RubySymbol symbol:
                WriteSymbol(symbol.Name);
                break;

            case RubyReference reference:
                WriteByte((byte)'c');
                WriteSymbol(reference.Name);
                break;

            case RubyArray array:
                WriteArray(array);
                break;

            case RubyHash hash:
                WriteHash(hash);
                break;

            case RubyObject rubyObject:
                WriteObject(rubyObject);
                break;

            case RubyUserMarshal userMarshal:
                WriteUserMarshal(userMarshal);
                break;

            default:
                WriteString(value.ToString() ?? string.Empty);
                break;
        }
    }

    // ====================================================================
    // 容器与对象
    // ====================================================================

    private void WriteArray(RubyArray array)
    {
        if (TryWriteLink(array)) return;

        WriteByte((byte)'[');
        WriteLong(array.Count);

        foreach (var item in array)
            WriteValue(item);
    }

    private void WriteHash(RubyHash hash)
    {
        if (TryWriteLink(hash)) return;

        WriteByte((byte)'{');
        WriteLong(hash.Count);

        foreach (var pair in hash)
        {
            WriteValue(pair.Key);
            WriteValue(pair.Value);
        }
    }

    private void WriteObject(RubyObject rubyObject)
    {
        if (TryWriteLink(rubyObject)) return;

        WriteByte((byte)'o');
        WriteSymbol(rubyObject.ClassName);
        WriteLong(rubyObject.Ivars.Count);

        foreach (var ivar in rubyObject.Ivars)
        {
            WriteSymbol(ivar.Key);
            WriteValue(ivar.Value);
        }
    }

    private void WriteUserMarshal(RubyUserMarshal userMarshal)
    {
        WriteByte((byte)'u');
        WriteSymbol(userMarshal.ClassName);
        WriteLong(userMarshal.Payload.Length);
        _output.Write(userMarshal.Payload, 0, userMarshal.Payload.Length);
    }

    /// <summary>已登记则写 "@索引" 并返回 true；否则登记后返回 false。</summary>
    private bool TryWriteLink(object value)
    {
        if (_linkIndex.TryGetValue(value, out int existing))
        {
            WriteByte((byte)'@');
            WriteLong(existing);
            return true;
        }

        _linkIndex[value] = _links.Count;
        _links.Add(value);
        return false;
    }

    // ====================================================================
    // 基础类型
    // ====================================================================

    private void WriteInteger(long value)
    {
        WriteByte((byte)'i');
        WriteLong(value);
    }

    private void WriteFloat(double value)
    {
        WriteByte((byte)'f');

        string text = value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        // Ruby 的 Float 文本必须能被 to_f 解析；整数形态要补 .0
        if (!text.Contains('.') && !text.Contains('E') && !text.Contains('e')
            && !text.Contains("Inf") && !text.Contains("NaN"))
            text += ".0";

        WriteRawString(text);
    }

    private void WriteString(string text)
    {
        if (_useEncodingIvars && NeedsEncodingMarker(text))
        {
            // I 包装 + 字符串 + ivar(:E = UTF-8)
            WriteByte((byte)'I');
            WriteRawString(text);
            WriteLong(1);
            WriteSymbol("E");
            WriteByte((byte)'T');
            return;
        }

        WriteRawString(text);
    }

    /// <summary>纯 ASCII 不需要编码标记；含非 ASCII 才需要。</summary>
    private static bool NeedsEncodingMarker(string text)
    {
        foreach (char c in text)
            if (c > 0x7F) return true;

        return false;
    }

    private void WriteRawString(string text)
    {
        byte[] raw = Encoding.UTF8.GetBytes(text);
        WriteByte((byte)'"');
        WriteLong(raw.Length);
        _output.Write(raw, 0, raw.Length);
    }

    private void WriteRawBytes(byte[] bytes)
    {
        WriteByte((byte)'"');
        WriteLong(bytes.Length);
        _output.Write(bytes, 0, bytes.Length);
    }

    private void WriteSymbol(string name)
    {
        if (_symbolIndex.TryGetValue(name, out int existing))
        {
            WriteByte((byte)';');
            WriteLong(existing);
            return;
        }

        _symbolIndex[name] = _symbols.Count;
        _symbols.Add(name);

        WriteByte((byte)':');

        byte[] raw = Encoding.UTF8.GetBytes(name);
        WriteLong(raw.Length);
        _output.Write(raw, 0, raw.Length);
    }

    // ====================================================================
    // 变长整数
    // ====================================================================

    /// <summary>
    /// Ruby Marshal 的整数编码：
    ///   0            → 单字节 0
    ///   1..122       → 单字节 value+5
    ///   -123..-1     → 单字节 (value-5) &amp; 0xFF
    ///   其他         → 首字节为字节数（正）或 256-字节数（负），后跟大端补码
    /// </summary>
    private void WriteLong(long value)
    {
        if (value == 0)
        {
            WriteByte(0);
            return;
        }

        if (value > 0 && value < 123)
        {
            WriteByte((byte)(value + 5));
            return;
        }

        if (value < 0 && value > -124)
        {
            WriteByte((byte)((value - 5) & 0xFF));
            return;
        }

        if (value > 0)
        {
            int byteCount = CountPositiveBytes(value);
            WriteByte((byte)byteCount);

            for (int i = byteCount - 1; i >= 0; i--)
                WriteByte((byte)((value >> (i * 8)) & 0xFF));
        }
        else
        {
            int byteCount = CountNegativeBytes(value);
            WriteByte((byte)(256 - byteCount));

            for (int i = byteCount - 1; i >= 0; i--)
                WriteByte((byte)((value >> (i * 8)) & 0xFF));
        }
    }

    private static int CountPositiveBytes(long value)
    {
        int count = 0;
        long remaining = value;

        while (remaining > 0)
        {
            count++;
            remaining >>= 8;
        }

        return count;
    }

    private static int CountNegativeBytes(long value)
    {
        int count = 0;
        long remaining = value;

        while (remaining < -1)
        {
            count++;
            remaining >>= 8;
        }

        return count;
    }

    // ====================================================================
    // 输出
    // ====================================================================

    private void WriteByte(byte value) => _output.WriteByte(value);
}
