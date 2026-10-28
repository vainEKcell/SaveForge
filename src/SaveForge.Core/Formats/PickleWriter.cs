using System.Buffers.Binary;

namespace SaveForge.Core.Formats;

/// <summary>
/// Python pickle 编码器（协议 2）。
///
/// 与 <see cref="PickleReader"/> 配对，用于把修改后的对象图写回 Ren'Py 存档。
/// 选择协议 2 的原因：Ren'Py 7（Python 2）与 Ren'Py 8（Python 3）都能读取。
///
/// 类型保真：
///   - 普通 dict/list/tuple/set 用原生指令
///   - 带 OriginalClass 的容器（如 renpy.python.RevertableDict）用
///     copyreg._reconstructor 形式重建，保证 Ren'Py 加载后仍是 Revertable 类型
///   - 未知类（PickleInstance）用 GLOBAL + REDUCE + BUILD 还原
/// </summary>
internal static class PickleWriter
{
    private const byte Proto = 0x80;
    private const byte BinFloat = (byte)'G';
    private const byte BinInt = (byte)'J';
    private const byte Long1 = 0x8A;
    private const byte BinUnicode = (byte)'X';
    private const byte BinString = (byte)'T';
    private const byte NewTrue = 0x88;
    private const byte NewFalse = 0x89;
    private const byte EmptyDict = (byte)'}';
    private const byte EmptyList = (byte)']';
    private const byte EmptyTuple = (byte)')';
    private const byte EmptySet = 0x8F;
    private const byte Mark = (byte)'(';
    private const byte AppendItems = (byte)'u';   // SETITEMS
    private const byte AppendList = (byte)'e';    // APPENDS
    private const byte AddItems = 0x90;           // ADDITEMS
    private const byte Tuple = (byte)'t';
    private const byte Tuple1 = 0x85;
    private const byte Tuple2 = 0x86;
    private const byte Tuple3 = 0x87;
    private const byte Global = (byte)'c';
    private const byte Reduce = (byte)'R';
    private const byte Build = (byte)'b';
    private const byte Stop = (byte)'.';
    private const byte None = (byte)'N';

    public static byte[] Write(object? value)
    {
        using var stream = new MemoryStream();
        stream.WriteByte(Proto);
        stream.WriteByte(0x02);   // protocol 2

        WriteObject(stream, value);

        stream.WriteByte(Stop);
        return stream.ToArray();
    }

    // ====================================================================
    // 分派
    // ====================================================================

    private static void WriteObject(Stream stream, object? value)
    {
        switch (value)
        {
            case null:
                stream.WriteByte(None);
                break;

            case bool flag:
                stream.WriteByte(flag ? NewTrue : NewFalse);
                break;

            case byte small:
                WriteLong(stream, small);
                break;

            case sbyte signedSmall:
                WriteLong(stream, signedSmall);
                break;

            case short shortValue:
                WriteLong(stream, shortValue);
                break;

            case ushort ushortValue:
                WriteLong(stream, ushortValue);
                break;

            case int integer:
                WriteLong(stream, integer);
                break;

            case uint unsignedInteger:
                WriteLong(stream, unsignedInteger);
                break;

            case long longValue:
                WriteLong(stream, longValue);
                break;

            case ulong unsignedLong:
                WriteLong(stream, unchecked((long)unsignedLong));
                break;

            case float single:
                WriteFloat(stream, single);
                break;

            case double number:
                WriteFloat(stream, number);
                break;

            case string text:
                WriteUnicode(stream, text);
                break;

            case byte[] bytes:
                WriteBinary(stream, bytes);
                break;

            case PickleDict dict:
                WriteDict(stream, dict);
                break;

            case PickleTuple tuple:
                WriteTuple(stream, tuple);
                break;

            case PickleList list:
                WriteList(stream, list);
                break;

            case PickleSet set:
                WriteSet(stream, set);
                break;

            case PickleInstance instance:
                WriteInstance(stream, instance);
                break;

            default:
                // 无法识别的对象降级为字符串，避免整份存档写坏
                WriteUnicode(stream, value.ToString() ?? string.Empty);
                break;
        }
    }

    // ====================================================================
    // 基础类型
    // ====================================================================

    private static void WriteLong(Stream stream, long value)
    {
        if (value is >= int.MinValue and <= int.MaxValue)
        {
            stream.WriteByte(BinInt);
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(buffer, (int)value);
            stream.Write(buffer);
            return;
        }

        byte[] magnitude = EncodeBigSigned(value);
        stream.WriteByte(Long1);
        stream.WriteByte((byte)magnitude.Length);
        stream.Write(magnitude, 0, magnitude.Length);
    }

    /// <summary>pickle 的长整数是 little-endian 补码。</summary>
    private static byte[] EncodeBigSigned(long value)
    {
        var bytes = new List<byte>(8);
        long remaining = value;

        do
        {
            bytes.Add((byte)(remaining & 0xFF));
            remaining >>= 8;
        }
        while (remaining != 0 && remaining != -1);

        byte last = bytes[^1];
        if (value < 0 && (last & 0x80) == 0) bytes.Add(0xFF);
        else if (value >= 0 && (last & 0x80) != 0) bytes.Add(0x00);

        return bytes.ToArray();
    }

    private static void WriteFloat(Stream stream, double value)
    {
        stream.WriteByte(BinFloat);
        byte[] buffer = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian) Array.Reverse(buffer);
        stream.Write(buffer, 0, buffer.Length);
    }

    private static void WriteUnicode(Stream stream, string text)
    {
        byte[] raw = Encoding.UTF8.GetBytes(text);
        stream.WriteByte(BinUnicode);
        WriteLength(stream, raw.Length);
        stream.Write(raw, 0, raw.Length);
    }

    private static void WriteBinary(Stream stream, byte[] data)
    {
        stream.WriteByte(BinString);
        WriteLength(stream, data.Length);
        stream.Write(data, 0, data.Length);
    }

    private static void WriteLength(Stream stream, int length)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, length);
        stream.Write(buffer);
    }

    // ====================================================================
    // 容器
    // ====================================================================

    private static void WriteDict(Stream stream, PickleDict dict)
    {
        if (dict.OriginalClass is not null)
        {
            WriteReconstructedContainer(stream, dict.OriginalClass, "dict", () =>
            {
                if (dict.Count == 0) return;

                stream.WriteByte(Mark);
                foreach (var pair in dict)
                {
                    WriteObject(stream, pair.Key);
                    WriteObject(stream, pair.Value);
                }
                stream.WriteByte(AppendItems);
            });

            return;
        }

        if (dict.Count == 0)
        {
            stream.WriteByte(EmptyDict);
            return;
        }

        stream.WriteByte(EmptyDict);
        stream.WriteByte(Mark);
        foreach (var pair in dict)
        {
            WriteObject(stream, pair.Key);
            WriteObject(stream, pair.Value);
        }
        stream.WriteByte(AppendItems);
    }

    private static void WriteList(Stream stream, PickleList list)
    {
        if (list.OriginalClass is not null)
        {
            WriteReconstructedContainer(stream, list.OriginalClass, "list", () =>
            {
                if (list.Count == 0) return;

                stream.WriteByte(Mark);
                foreach (var item in list) WriteObject(stream, item);
                stream.WriteByte(AppendList);
            });

            return;
        }

        stream.WriteByte(EmptyList);

        if (list.Count == 0) return;

        stream.WriteByte(Mark);
        foreach (var item in list) WriteObject(stream, item);
        stream.WriteByte(AppendList);
    }

    private static void WriteTuple(Stream stream, PickleTuple tuple)
    {
        switch (tuple.Count)
        {
            case 0:
                stream.WriteByte(EmptyTuple);
                return;

            case 1:
                WriteObject(stream, tuple[0]);
                stream.WriteByte(Tuple1);
                return;

            case 2:
                WriteObject(stream, tuple[0]);
                WriteObject(stream, tuple[1]);
                stream.WriteByte(Tuple2);
                return;

            case 3:
                WriteObject(stream, tuple[0]);
                WriteObject(stream, tuple[1]);
                WriteObject(stream, tuple[2]);
                stream.WriteByte(Tuple3);
                return;

            default:
                stream.WriteByte(Mark);
                foreach (var item in tuple) WriteObject(stream, item);
                stream.WriteByte(Tuple);
                return;
        }
    }

    private static void WriteSet(Stream stream, PickleSet set)
    {
        if (set.OriginalClass is not null)
        {
            WriteReconstructedContainer(stream, set.OriginalClass, "set", () =>
            {
                if (set.Count == 0) return;

                stream.WriteByte(Mark);
                foreach (var item in set) WriteObject(stream, item);
                stream.WriteByte(AddItems);
            });

            return;
        }

        stream.WriteByte(EmptySet);

        if (set.Count == 0) return;

        stream.WriteByte(Mark);
        foreach (var item in set) WriteObject(stream, item);
        stream.WriteByte(AddItems);
    }

    // ====================================================================
    // 类实例
    // ====================================================================

    private static void WriteInstance(Stream stream, PickleInstance instance)
    {
        WriteGlobalRef(stream, instance.ClassName);

        WriteTuple(stream, new PickleTuple(instance.Args));
        stream.WriteByte(Reduce);

        if (instance.State is not null)
        {
            WriteObject(stream, instance.State);
            stream.WriteByte(Build);
        }
    }

    /// <summary>
    /// 用 copyreg._reconstructor(cls, base, {}) 重建容器子类，
    /// 之后再把元素填进去，保证 Ren'Py 加载后仍是 Revertable 类型。
    /// </summary>
    private static void WriteReconstructedContainer(
        Stream stream, string className, string baseTypeName, Action writeItems)
    {
        stream.WriteByte(Global);
        WriteLine(stream, "copyreg");
        WriteLine(stream, "_reconstructor");

        stream.WriteByte(Mark);
        WriteGlobalRef(stream, className);
        WriteGlobalRef(stream, "__builtin__." + baseTypeName);
        stream.WriteByte(EmptyDict);  // 空的初始 state
        stream.WriteByte(Tuple);
        stream.WriteByte(Reduce);

        writeItems();
    }

    private static void WriteGlobalRef(Stream stream, string fullName)
    {
        int dot = fullName.LastIndexOf('.');

        string module = dot > 0 ? fullName[..dot] : "__main__";
        string name = dot > 0 ? fullName[(dot + 1)..] : fullName;

        stream.WriteByte(Global);
        WriteLine(stream, module);
        WriteLine(stream, name);
    }

    private static void WriteLine(Stream stream, string text)
    {
        byte[] raw = Encoding.ASCII.GetBytes(text);
        stream.Write(raw, 0, raw.Length);
        stream.WriteByte((byte)'\n');
    }
}
