namespace SaveForge.Core.Formats;

/// <summary>
/// Python pickle 解码器（协议 0–5 的常用子集）。
///
/// 用途：Ren'Py 存档的 `log` 条目是 zlib(pickle.dumps(...))，
/// 要读出游戏变量必须先把 pickle 还原成对象树。
///
/// 设计取舍：
///   - 完整实现基础类型与容器（dict/list/tuple/set/str/bytes/int/float/bool/None）
///   - 遇到未知类引用（GLOBAL / STACK_GLOBAL）不报错，用 <see cref="PickleInstance"/>
///     占位，尽量保留其构造参数与状态，供界面展示
///   - 不实现持久化 ID（PERSID）与扩展注册表（EXT），遇到时降级为占位
/// </summary>
internal sealed class PickleReader
{
    private readonly byte[] _data;
    private int _pos;
    private readonly List<object?> _stack = new();
    private readonly List<object?> _memo = new();
    private readonly List<int> _marks = new();

    private PickleReader(byte[] data) => _data = data;

    public static object? Read(byte[] data)
    {
        var reader = new PickleReader(data);
        return reader.Run();
    }

    private object? Run()
    {
        int guard = 0;

        while (_pos < _data.Length)
        {
            if (++guard > 20_000_000)
                throw new InvalidDataException("pickle 指令数量异常，可能数据损坏");

            byte op = ReadByte();

            switch (op)
            {
                case (byte)'\x80': // PROTO
                    ReadByte();
                    break;

                case (byte)'\x95': // FRAME
                    Skip(8);
                    break;

                case (byte)'.':    // STOP
                    return _stack.Count > 0 ? Pop() : null;

                case (byte)'(':    // MARK
                    _marks.Add(_stack.Count);
                    break;

                case (byte)'0':    // POP
                    Pop();
                    break;

                case (byte)'1':    // POP_MARK
                    PopMark();
                    break;

                case (byte)'2':    // DUP
                    _stack.Add(Peek());
                    break;

                case (byte)'N':    // NONE
                    _stack.Add(null);
                    break;

                case (byte)'\x88': // NEWTRUE
                    _stack.Add(true);
                    break;

                case (byte)'\x89': // NEWFALSE
                    _stack.Add(false);
                    break;

                case (byte)'I':    // INT (文本行)
                {
                    string line = ReadLine().Trim();
                    if (line == "01") _stack.Add(true);
                    else if (line == "00") _stack.Add(false);
                    else _stack.Add(ParseLong(line));
                    break;
                }

                case (byte)'J':    // BININT (4 字节有符号小端)
                    _stack.Add((long)BitConverter.ToInt32(_data, Take(4)));
                    break;

                case (byte)'K':    // BININT1
                    _stack.Add((long)ReadByte());
                    break;

                case (byte)'M':    // BININT2 (2 字节无符号小端)
                {
                    int v = _data[_pos] | (_data[_pos + 1] << 8);
                    Skip(2);
                    _stack.Add((long)v);
                    break;
                }

                case (byte)'\x8a': // LONG1
                {
                    int len = ReadByte();
                    _stack.Add(ReadBigSigned(TakeChecked(len)));
                    break;
                }

                case (byte)'\x8b': // LONG4
                {
                    int len = BitConverter.ToInt32(_data, Take(4));
                    if (len < 0 || len > 4096) throw new InvalidDataException($"pickle 长整数长度异常: {len}");
                    _stack.Add(ReadBigSigned(TakeChecked(len)));
                    break;
                }

                case (byte)'L':    // LONG (文本行)
                {
                    string line = ReadLine().Trim().TrimEnd('L', 'l');
                    _stack.Add(ParseLong(line));
                    break;
                }

                case (byte)'F':    // FLOAT (文本行)
                    _stack.Add(double.Parse(ReadLine().Trim(),
                        System.Globalization.CultureInfo.InvariantCulture));
                    break;

                case (byte)'G':    // BINFLOAT (8 字节大端 double)
                {
                    byte[] buf = _data.AsSpan(_pos, 8).ToArray();
                    Skip(8);
                    Array.Reverse(buf);
                    _stack.Add(BitConverter.ToDouble(buf, 0));
                    break;
                }

                case (byte)'S':    // STRING (带引号的文本)
                    _stack.Add(UnescapePythonString(ReadLine().Trim()));
                    break;

                case (byte)'T':    // BINSTRING (4 字节长度 + bytes, Python2 str)
                {
                    int len = BitConverter.ToInt32(_data, Take(4));
                    _stack.Add(DecodePythonBytes(TakeChecked(len)));
                    break;
                }

                case (byte)'U':    // SHORT_BINSTRING
                {
                    int len = ReadByte();
                    _stack.Add(DecodePythonBytes(TakeChecked(len)));
                    break;
                }

                case (byte)'B':    // BINBYTES
                {
                    int len = BitConverter.ToInt32(_data, Take(4));
                    _stack.Add(TakeChecked(len));
                    break;
                }

                case (byte)'C':    // SHORT_BINBYTES
                    _stack.Add(TakeChecked(ReadByte()));
                    break;

                case (byte)'\x8e': // BINBYTES8
                    _stack.Add(TakeChecked((int)BitConverter.ToInt64(_data, Take(8))));
                    break;

                case (byte)'X':    // BINUNICODE
                {
                    int len = BitConverter.ToInt32(_data, Take(4));
                    _stack.Add(Encoding.UTF8.GetString(TakeChecked(len)));
                    break;
                }

                case (byte)'\x8c': // SHORT_BINUNICODE
                    _stack.Add(Encoding.UTF8.GetString(TakeChecked(ReadByte())));
                    break;

                case (byte)'\x8d': // BINUNICODE8
                {
                    long len = BitConverter.ToInt64(_data, Take(8));
                    _stack.Add(Encoding.UTF8.GetString(TakeChecked((int)len)));
                    break;
                }

                case (byte)'V':    // UNICODE (raw-unicode-escape)
                    _stack.Add(ReadLine());
                    break;

                case (byte)'}':    // EMPTY_DICT
                    _stack.Add(new PickleDict());
                    break;

                case (byte)']':    // EMPTY_LIST
                    _stack.Add(new PickleList());
                    break;

                case (byte)')':    // EMPTY_TUPLE
                    _stack.Add(new PickleTuple());
                    break;

                case (byte)'\x8f': // EMPTY_SET
                    _stack.Add(new PickleSet());
                    break;

                case (byte)'d':    // DICT
                {
                    var items = PopToMark();
                    var dict = new PickleDict();
                    for (int i = 0; i + 1 < items.Count; i += 2)
                        dict.Add(new KeyValuePair<object?, object?>(items[i], items[i + 1]));
                    _stack.Add(dict);
                    break;
                }

                case (byte)'l':    // LIST
                    _stack.Add(new PickleList(PopToMark()));
                    break;

                case (byte)'t':    // TUPLE
                    _stack.Add(new PickleTuple(PopToMark()));
                    break;

                case (byte)'\x85': // TUPLE1
                {
                    object? a = Pop();
                    _stack.Add(new PickleTuple([a]));
                    break;
                }

                case (byte)'\x86': // TUPLE2
                {
                    object? b = Pop(), a = Pop();
                    _stack.Add(new PickleTuple([a, b]));
                    break;
                }

                case (byte)'\x87': // TUPLE3
                {
                    object? c = Pop(), b = Pop(), a = Pop();
                    _stack.Add(new PickleTuple([a, b, c]));
                    break;
                }

                case (byte)'s':    // SETITEM
                {
                    object? value = Pop(), key = Pop();
                    if (Peek() is PickleDict dict)
                        dict.Add(new KeyValuePair<object?, object?>(key, value));
                    break;
                }

                case (byte)'u':    // SETITEMS
                {
                    var items = PopToMark();
                    if (Peek() is PickleDict dict)
                        for (int i = 0; i + 1 < items.Count; i += 2)
                            dict.Add(new KeyValuePair<object?, object?>(items[i], items[i + 1]));
                    break;
                }

                case (byte)'a':    // APPEND
                {
                    object? value = Pop();
                    if (Peek() is PickleList list) list.Add(value);
                    break;
                }

                case (byte)'e':    // APPENDS
                {
                    var items = PopToMark();
                    if (Peek() is PickleList list) list.AddRange(items);
                    break;
                }

                case (byte)'\x90': // ADDITEMS
                {
                    var items = PopToMark();
                    if (Peek() is PickleSet set) set.AddRange(items);
                    break;
                }

                case (byte)'c':    // GLOBAL
                {
                    string module = ReadLine().Trim();
                    string name = ReadLine().Trim();
                    _stack.Add(new PickleInstance($"{module}.{name}"));
                    break;
                }

                case (byte)'\x93': // STACK_GLOBAL
                {
                    object? nameObj = Pop(), moduleObj = Pop();
                    _stack.Add(new PickleInstance($"{moduleObj}.{nameObj}"));
                    break;
                }

                case (byte)'R':    // REDUCE
                {
                    object? args = Pop();
                    object? callable = Pop();
                    _stack.Add(Reduce(callable, args));
                    break;
                }

                case (byte)'\x81': // NEWOBJ
                {
                    object? args = Pop();
                    object? cls = Pop();
                    _stack.Add(CreateInstance(cls, args));
                    break;
                }

                case (byte)'\x92': // NEWOBJ_EX
                {
                    object? kwargs = Pop();
                    object? args = Pop();
                    object? cls = Pop();
                    _stack.Add(CreateInstance(cls, args, kwargs));
                    break;
                }

                case (byte)'b':    // BUILD
                {
                    object? state = Pop();
                    if (Peek() is PickleInstance instance)
                        instance.State = state;
                    else if (Peek() is PickleDict dict && state is PickleDict stateDict)
                        foreach (var pair in stateDict) dict.Add(pair);
                    break;
                }

                case (byte)'q':    // BINPUT
                    Memoize(ReadByte());
                    break;

                case (byte)'r':    // LONG_BINPUT
                    Memoize(BitConverter.ToInt32(_data, Take(4)));
                    break;

                case (byte)'p':    // PUT (文本行)
                    Memoize(int.Parse(ReadLine().Trim()));
                    break;

                case (byte)'h':    // BINGET
                    _stack.Add(GetMemo(ReadByte()));
                    break;

                case (byte)'j':    // LONG_BINGET
                    _stack.Add(GetMemo(BitConverter.ToInt32(_data, Take(4))));
                    break;

                case (byte)'g':    // GET (文本行)
                    _stack.Add(GetMemo(int.Parse(ReadLine().Trim())));
                    break;

                case (byte)'\x94': // MEMOIZE
                    Memoize(_memo.Count);
                    break;

                case (byte)'P':    // PERSID
                    _stack.Add(new PickleInstance("persid:" + ReadLine().Trim()));
                    break;

                case (byte)'Q':    // BINPERSID
                    _stack.Add(new PickleInstance("persid:" + Pop()));
                    break;

                case (byte)'i':    // INST
                {
                    string module = ReadLine().Trim();
                    string name = ReadLine().Trim();
                    var args = PopToMark();
                    _stack.Add(new PickleInstance($"{module}.{name}") { Args = args });
                    break;
                }

                case (byte)'o':    // OBJ
                {
                    var args = PopToMark();
                    object? cls = Pop();
                    _stack.Add(CreateInstance(cls, new PickleTuple(args)));
                    break;
                }

                case (byte)'\x82': // EXT1
                case (byte)'\x83': // EXT2
                case (byte)'\x84': // EXT4
                {
                    int size = op == 0x82 ? 1 : op == 0x83 ? 2 : 4;
                    Skip(size);
                    _stack.Add(new PickleInstance("ext"));
                    break;
                }

                default:
                    throw new InvalidDataException(
                        $"不支持的 pickle 指令 0x{op:X2}（位置 {_pos - 1}）");
            }
        }

        return _stack.Count > 0 ? _stack[^1] : null;
    }

    // ====================================================================
    // REDUCE / 实例构造
    // ====================================================================

    private static object? Reduce(object? callable, object? args)
    {
        var argsList = ToArgumentList(args);

        if (callable is PickleInstance cls)
        {
            string name = cls.ClassName;

            // Ren'Py 的 revertable 容器：本质上就是 dict / list / set，但要记住原始类名以便写回时还原
            if (name.EndsWith("RevertableDict", StringComparison.Ordinal))
                return new PickleDict { OriginalClass = name };
            if (name.EndsWith("RevertableList", StringComparison.Ordinal))
                return new PickleList { OriginalClass = name };
            if (name.EndsWith("RevertableSet", StringComparison.Ordinal))
                return new PickleSet { OriginalClass = name };

            // copyreg._reconstructor(cls, base, state)：取 base 的空容器，并保留 cls
            if (name == "copyreg._reconstructor" && argsList.Count >= 2)
            {
                string clsName = argsList[0] switch
                {
                    PickleInstance instance => instance.ClassName,
                    _ => argsList[0]?.ToString() ?? string.Empty,
                };

                string baseName = argsList[1] is PickleInstance baseInstance
                    ? baseInstance.ClassName
                    : argsList[1]?.ToString() ?? string.Empty;

                if (clsName.Contains("Dict", StringComparison.Ordinal) ||
                    baseName.Contains("dict", StringComparison.OrdinalIgnoreCase))
                    return new PickleDict { OriginalClass = clsName.Length > 0 ? clsName : null };

                if (clsName.Contains("List", StringComparison.Ordinal) ||
                    baseName.Contains("list", StringComparison.OrdinalIgnoreCase))
                    return new PickleList { OriginalClass = clsName.Length > 0 ? clsName : null };

                if (clsName.Contains("Set", StringComparison.Ordinal) ||
                    baseName.Contains("set", StringComparison.OrdinalIgnoreCase))
                    return new PickleSet { OriginalClass = clsName.Length > 0 ? clsName : null };
            }

            // 常见内置
            if (name == "builtins.set" || name == "__builtin__.set")
                return new PickleSet();
            if (name == "builtins.bytearray" || name == "__builtin__.bytearray")
                return argsList.Count > 0 ? argsList[0] as byte[] ?? [] : Array.Empty<byte>();
            if (name == "builtins.frozenset" || name == "__builtin__.frozenset")
                return new PickleSet();

            return new PickleInstance(name) { Args = argsList };
        }

        return new PickleInstance(callable?.ToString() ?? "unknown") { Args = argsList };
    }

    private static object? CreateInstance(object? cls, object? args, object? kwargs = null)
    {
        string name = cls is PickleInstance instance ? instance.ClassName : cls?.ToString() ?? "unknown";
        var argsList = ToArgumentList(args);

        if (kwargs is PickleDict kw)
            foreach (var pair in kw)
                argsList.Add(pair.Value);

        return new PickleInstance(name) { Args = argsList };
    }

    private static List<object?> ToArgumentList(object? args) => args switch
    {
        PickleTuple tuple => new List<object?>(tuple),
        PickleList list => new List<object?>(list),
        null => [],
        _ => [args],
    };

    private void Memoize(int index)
    {
        while (_memo.Count <= index) _memo.Add(null);
        _memo[index] = Peek();
    }

    private object? GetMemo(int index) => index >= 0 && index < _memo.Count ? _memo[index] : null;

    // ====================================================================
    // 栈操作
    // ====================================================================

    private object? Pop()
    {
        if (_stack.Count == 0) return null;
        object? value = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        return value;
    }

    private object? Peek() => _stack.Count > 0 ? _stack[^1] : null;

    private List<object?> PopToMark()
    {
        if (_marks.Count == 0) return [];

        int mark = _marks[^1];
        _marks.RemoveAt(_marks.Count - 1);

        var items = _stack.GetRange(mark, _stack.Count - mark);
        _stack.RemoveRange(mark, _stack.Count - mark);
        return items;
    }

    private void PopMark()
    {
        if (_marks.Count == 0) return;
        int mark = _marks[^1];
        _marks.RemoveAt(_marks.Count - 1);
        _stack.RemoveRange(mark, _stack.Count - mark);
    }

    // ====================================================================
    // 读取原语
    // ====================================================================

    private byte ReadByte()
    {
        if (_pos >= _data.Length) throw new EndOfStreamException("pickle 数据意外结束");
        return _data[_pos++];
    }

    private int Take(int count)
    {
        int start = _pos;
        Skip(count);
        return start;
    }

    private byte[] TakeChecked(int count)
    {
        if (count < 0 || count > _data.Length - _pos)
            throw new InvalidDataException($"pickle 长度越界: {count}（剩余 {_data.Length - _pos}）");

        byte[] result = _data.AsSpan(_pos, count).ToArray();
        _pos += count;
        return result;
    }

    private void Skip(int count)
    {
        if (count < 0 || _pos + count > _data.Length)
            throw new EndOfStreamException($"pickle 读取越界（需要 {count}，剩余 {_data.Length - _pos}）");
        _pos += count;
    }

    private string ReadLine()
    {
        int start = _pos;
        while (_pos < _data.Length && _data[_pos] != (byte)'\n') _pos++;
        string line = Encoding.UTF8.GetString(_data, start, _pos - start);
        if (_pos < _data.Length) _pos++; // 跳过换行
        return line;
    }

    /// <summary>pickle 里的大整数是二进制补码、字节反向存储。</summary>
    private static long ReadBigSigned(byte[] littleEndianMagnitude)
    {
        if (littleEndianMagnitude.Length == 0) return 0;

        // 转成 big-endian 再按补码解释
        byte[] big = littleEndianMagnitude.Reverse().ToArray();
        bool negative = (big[0] & 0x80) != 0;

        if (!negative)
        {
            long value = 0;
            foreach (byte b in big) value = (value << 8) | b;
            return value;
        }

        // 负数的补码还原
        for (int i = 0; i < big.Length; i++) big[i] = (byte)~big[i];
        long inverted = 0;
        foreach (byte b in big) inverted = (inverted << 8) | b;
        return -(inverted + 1);
    }

    private static long ParseLong(string text)
    {
        if (long.TryParse(text, out long value)) return value;

        // 超出 long 范围时退化为 0（编辑器无法编辑这种值）
        return 0;
    }

    /// <summary>Python 2 的 str 实际是字节；先按 UTF-8 解，失败保留原始字节。</summary>
    private static object DecodePythonBytes(byte[] raw)
    {
        try { return Encoding.UTF8.GetString(raw); }
        catch { return raw; }
    }

    private static string UnescapePythonString(string quoted)
    {
        if (quoted.Length >= 2 && quoted[0] == '\'' && quoted[^1] == '\'')
            quoted = quoted[1..^1];

        var builder = new StringBuilder(quoted.Length);

        for (int i = 0; i < quoted.Length; i++)
        {
            char c = quoted[i];
            if (c != '\\' || i + 1 >= quoted.Length)
            {
                builder.Append(c);
                continue;
            }

            char next = quoted[++i];
            builder.Append(next switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                '\\' => '\\',
                '\'' => '\'',
                '"' => '"',
                '0' => '\0',
                _ => next,
            });
        }

        return builder.ToString();
    }
}

// ========================================================================
// pickle 对象模型
// ========================================================================

/// <summary>pickle 字典（保序）。OriginalClass 记录它来自哪个容器子类（如 Ren'Py 的 RevertableDict）。</summary>
internal sealed class PickleDict : List<KeyValuePair<object?, object?>>
{
    public string? OriginalClass { get; set; }
}

/// <summary>pickle 列表。</summary>
internal sealed class PickleList : List<object?>
{
    public string? OriginalClass { get; set; }

    public PickleList() { }
    public PickleList(IEnumerable<object?> items) : base(items) { }
}

/// <summary>pickle 元组。</summary>
internal sealed class PickleTuple : List<object?>
{
    public PickleTuple() { }
    public PickleTuple(IEnumerable<object?> items) : base(items) { }
}

/// <summary>pickle 集合。</summary>
internal sealed class PickleSet : List<object?>
{
    public string? OriginalClass { get; set; }
}

/// <summary>无法还原的类引用占位（如 renpy.python.RevertableDict 之外的引擎对象）。</summary>
internal sealed class PickleInstance
{
    public string ClassName { get; }
    public List<object?> Args { get; set; } = [];
    public object? State { get; set; }

    public PickleInstance(string className) => ClassName = className;
}
