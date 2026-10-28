using SaveForge.Core.IO;

namespace SaveForge.Core.Formats;

/// <summary>
/// Minecraft NBT（Named Binary Tag）格式。
///
/// 规范要点（大端）：
///   Tag = 类型(1 字节) + 名称(short 长度 + UTF-8) + 负载
///   类型：0 End / 1 Byte / 2 Short / 3 Int / 4 Long / 5 Float / 6 Double /
///         7 ByteArray / 8 String / 9 List / 10 Compound / 11 IntArray / 12 LongArray
///   根标签必须是 Compound
///
/// 注意：Minecraft 的 .dat 文件通常外层还有 gzip，由压缩预处理层负责解包。
/// </summary>
public sealed class NbtFormat : ISaveFormat
{
    public string Id => "nbt";
    public string DisplayName => "NBT 存档（Minecraft）";
    public string[] Extensions => ["dat", "nbt", "mca"];
    public bool CanWrite => true;

    private const byte TagEnd = 0;
    private const byte TagByte = 1;
    private const byte TagShort = 2;
    private const byte TagInt = 3;
    private const byte TagLong = 4;
    private const byte TagFloat = 5;
    private const byte TagDouble = 6;
    private const byte TagByteArray = 7;
    private const byte TagString = 8;
    private const byte TagList = 9;
    private const byte TagCompound = 10;
    private const byte TagIntArray = 11;
    private const byte TagLongArray = 12;

    public float Detect(ReadOnlySpan<byte> data, string? fileName)
    {
        if (data.Length < 4) return 0f;
        if (data[0] != TagCompound) return 0f;

        // 真正解析一遍，确认结构合法
        try
        {
            var reader = new EndianReader(data.ToArray(), Endianness.Big);
            ReadRoot(reader);
            return 0.90f;
        }
        catch
        {
            return 0f;
        }
    }

    public SaveDocument Read(byte[] data, string? filePath)
    {
        var reader = new EndianReader(data, Endianness.Big);

        SaveNode root;
        try
        {
            root = ReadRoot(reader);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"NBT 解析失败：{ex.Message}", ex);
        }

        var document = new SaveDocument
        {
            FilePath = filePath,
            FormatId = Id,
            FormatName = DisplayName,
            OriginalBytes = data,
        };

        document.Roots.Add(root);
        document.AcceptChanges();
        return document;
    }

    public byte[] Write(SaveDocument document)
    {
        var root = document.Roots.FirstOrDefault()
            ?? throw new InvalidOperationException("文档没有根节点");

        if (root.Kind != ValueKind.Object)
            throw new InvalidOperationException("NBT 根节点必须是 Compound 类型");

        var writer = new EndianWriter(Endianness.Big);
        writer.WriteByte(TagCompound);
        WriteNbtString(writer, root.Name == "NBT" ? string.Empty : root.Name);
        WriteCompoundPayload(writer, root);

        return writer.ToArray();
    }

    // ====================================================================
    // 读取
    // ====================================================================

    private static SaveNode ReadRoot(EndianReader reader)
    {
        byte type = reader.ReadByte();
        if (type != TagCompound)
            throw new InvalidDataException($"根标签应为 Compound(10)，实际为 {type}");

        string name = ReadNbtString(reader);
        var node = new SaveNode
        {
            Name = string.IsNullOrEmpty(name) ? "NBT" : name,
            Locator = "根标签",
        };
        node.InitValue(null, ValueKind.Object);
        ReadCompoundPayload(reader, node);
        return node;
    }

    private static void ReadCompoundPayload(EndianReader reader, SaveNode parent)
    {
        int guard = 0;
        while (true)
        {
            if (++guard > 1_000_000)
                throw new InvalidDataException("NBT 标签数量异常，可能文件损坏");

            byte type = reader.ReadByte();
            if (type == TagEnd) return;

            string name = ReadNbtString(reader);
            parent.Children.Add(ReadPayload(reader, type, name));
        }
    }

    private static SaveNode ReadPayload(EndianReader reader, byte type, string name)
    {
        var node = new SaveNode
        {
            Name = name,
            Locator = $"0x{reader.Position:X}",
        };

        switch (type)
        {
            case TagByte:
                node.TypeHint = "byte";
                node.InitValue((int)reader.ReadSByte(), ValueKind.Integer);
                break;

            case TagShort:
                node.TypeHint = "short";
                node.InitValue((int)reader.ReadInt16(), ValueKind.Integer);
                break;

            case TagInt:
                node.TypeHint = "int";
                node.InitValue(reader.ReadInt32(), ValueKind.Integer);
                break;

            case TagLong:
                node.TypeHint = "long";
                node.InitValue(reader.ReadInt64(), ValueKind.Integer);
                break;

            case TagFloat:
                node.TypeHint = "float";
                node.InitValue(reader.ReadSingle(), ValueKind.Float);
                break;

            case TagDouble:
                node.TypeHint = "double";
                node.InitValue(reader.ReadDouble(), ValueKind.Float);
                break;

            case TagString:
                node.TypeHint = "string";
                node.InitValue(ReadNbtString(reader), ValueKind.String);
                break;

            case TagByteArray:
            {
                int length = ReadSafeLength(reader);
                node.TypeHint = "bytearray";
                node.InitValue(reader.ReadBytes(length), ValueKind.Binary);
                break;
            }

            case TagList:
            {
                byte elementType = reader.ReadByte();
                int length = ReadSafeLength(reader);
                node.TypeHint = "list";
                node.InitValue(null, ValueKind.Array);

                for (int i = 0; i < length; i++)
                    node.Children.Add(ReadPayload(reader, elementType, $"[{i}]"));

                break;
            }

            case TagCompound:
                node.TypeHint = "compound";
                node.InitValue(null, ValueKind.Object);
                ReadCompoundPayload(reader, node);
                break;

            case TagIntArray:
            {
                int length = ReadSafeLength(reader);
                node.TypeHint = "intarray";
                node.InitValue(null, ValueKind.Array);

                for (int i = 0; i < length; i++)
                {
                    var item = new SaveNode { Name = $"[{i}]", TypeHint = "int" };
                    item.InitValue(reader.ReadInt32(), ValueKind.Integer);
                    node.Children.Add(item);
                }

                break;
            }

            case TagLongArray:
            {
                int length = ReadSafeLength(reader);
                node.TypeHint = "longarray";
                node.InitValue(null, ValueKind.Array);

                for (int i = 0; i < length; i++)
                {
                    var item = new SaveNode { Name = $"[{i}]", TypeHint = "long" };
                    item.InitValue(reader.ReadInt64(), ValueKind.Integer);
                    node.Children.Add(item);
                }

                break;
            }

            default:
                throw new InvalidDataException($"未知 NBT 标签类型：{type}");
        }

        return node;
    }

    private static int ReadSafeLength(EndianReader reader)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > 16 * 1024 * 1024)
            throw new InvalidDataException($"NBT 数组长度异常：{length}");
        return length;
    }

    private static string ReadNbtString(EndianReader reader)
    {
        int length = reader.ReadUInt16();
        if (length == 0) return string.Empty;
        byte[] raw = reader.ReadBytes(length);
        return Encoding.UTF8.GetString(raw);
    }

    // ====================================================================
    // 写回
    // ====================================================================

    private static void WriteCompoundPayload(EndianWriter writer, SaveNode compound)
    {
        foreach (var child in compound.Children)
        {
            byte type = ResolveTagType(child);
            writer.WriteByte(type);
            WriteNbtString(writer, child.Name);
            WritePayload(writer, type, child);
        }

        writer.WriteByte(TagEnd);
    }

    private static byte ResolveTagType(SaveNode node)
    {
        if (node.TypeHint is not null)
        {
            return node.TypeHint switch
            {
                "byte" => TagByte,
                "short" => TagShort,
                "int" => TagInt,
                "long" => TagLong,
                "float" => TagFloat,
                "double" => TagDouble,
                "string" => TagString,
                "bytearray" => TagByteArray,
                "list" => TagList,
                "compound" => TagCompound,
                "intarray" => TagIntArray,
                "longarray" => TagLongArray,
                _ => InferTagType(node),
            };
        }

        return InferTagType(node);
    }

    private static byte InferTagType(SaveNode node) => node.Kind switch
    {
        ValueKind.Object => TagCompound,
        ValueKind.Array => TagList,
        ValueKind.Binary => TagByteArray,
        ValueKind.String => TagString,
        ValueKind.Boolean => TagByte,
        ValueKind.Float => TagDouble,
        ValueKind.Integer => TagInt,
        _ => TagString,
    };

    private static void WritePayload(EndianWriter writer, byte type, SaveNode node)
    {
        switch (type)
        {
            case TagByte:
                writer.WriteByte(unchecked((byte)ToInt(node.Value)));
                break;

            case TagShort:
                writer.WriteInt16(unchecked((short)ToInt(node.Value)));
                break;

            case TagInt:
                writer.WriteInt32(unchecked((int)ToInt(node.Value)));
                break;

            case TagLong:
                writer.WriteInt64(ToInt(node.Value));
                break;

            case TagFloat:
                writer.WriteSingle(unchecked((float)ToDouble(node.Value)));
                break;

            case TagDouble:
                writer.WriteDouble(ToDouble(node.Value));
                break;

            case TagString:
                WriteNbtString(writer, node.Value?.ToString() ?? string.Empty);
                break;

            case TagByteArray:
            {
                byte[] bytes = node.Value is byte[] b ? b : [];
                writer.WriteInt32(bytes.Length);
                writer.WriteBytes(bytes);
                break;
            }

            case TagList:
            {
                byte elementType = node.Children.Count > 0
                    ? ResolveTagType(node.Children[0])
                    : TagEnd;

                writer.WriteByte(elementType);
                writer.WriteInt32(node.Children.Count);

                foreach (var child in node.Children)
                    WritePayload(writer, elementType, child);

                break;
            }

            case TagCompound:
                WriteCompoundPayload(writer, node);
                break;

            case TagIntArray:
                writer.WriteInt32(node.Children.Count);
                foreach (var child in node.Children)
                    writer.WriteInt32(unchecked((int)ToInt(child.Value)));
                break;

            case TagLongArray:
                writer.WriteInt32(node.Children.Count);
                foreach (var child in node.Children)
                    writer.WriteInt64(ToInt(child.Value));
                break;
        }
    }

    private static void WriteNbtString(EndianWriter writer, string value)
    {
        byte[] raw = Encoding.UTF8.GetBytes(value);
        if (raw.Length > ushort.MaxValue)
            throw new InvalidOperationException($"NBT 字符串过长：{raw.Length} 字节");

        writer.WriteUInt16((ushort)raw.Length);
        writer.WriteBytes(raw);
    }

    private static long ToInt(object? value)
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
