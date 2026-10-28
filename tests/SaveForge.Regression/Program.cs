using System.IO.Compression;
using System.Text;
using SaveForge.Core;
using SaveForge.Core.Compression;
using SaveForge.Core.Formats;
using SaveForge.Core.IO;

namespace SaveForge.Regression;

/// <summary>
/// 全格式回归测试。
///
/// 对每种支持的格式都跑同一套动作：
///   1. 识别 → 2. 读取 → 3. 无修改写回（部分格式额外校验字节一致）
///   4. 改一个值 → 保存 → 重新打开验证
///
/// 另外覆盖 8 种压缩包裹、真实 RPG Maker MZ 存档、物品名映射、游戏根目录定位。
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;
    private static readonly List<string> Failures = new();

    private const string RepoDemo = @"s:\AgentsWorkers\Tare CN\.demo";

    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "SaveForge-Regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            Console.WriteLine("=== 格式读写 ===");
            RunFormatTests(root);

            Console.WriteLine();
            Console.WriteLine("=== 压缩 / 编码包裹 ===");
            RunWrapperTests(root);

            Console.WriteLine();
            Console.WriteLine("=== 真实存档与物品库 ===");
            RunRealSaveTests(root);

            Console.WriteLine();
            Console.WriteLine("=== 游戏根目录定位 ===");
            RunGameRootTests(root);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* 临时目录清理失败不影响结果 */ }
        }

        Console.WriteLine();
        Console.WriteLine($"结果：通过 {_passed}，失败 {_failed}");

        if (Failures.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("失败项：");
            foreach (string failure in Failures) Console.WriteLine("  - " + failure);
        }

        return _failed == 0 ? 0 : 1;
    }

    // ====================================================================
    // 格式
    // ====================================================================

    private sealed record Sample(
        string Label,
        string FileName,
        byte[] Data,
        string ExpectedFormat,
        string? TargetKey = null,
        bool ByteStable = false);

    private static void RunFormatTests(string root)
    {
        const string json =
            """{"player":{"gold":100,"name":"Hero","level":7},"world":{"day":3,"weather":"sunny"}}""";

        const string xml =
            """<?xml version="1.0" encoding="utf-8"?><save><player><gold>100</gold><name>Hero</name></player><world><day>3</day></world></save>""";

        const string ini =
            "[Player]\nGold=100\nName=Hero\nLevel=7\n\n[World]\nDay=3\n";

        const string plistXml =
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>gold</key><integer>100</integer>
                <key>name</key><string>Hero</string>
            </dict>
            </plist>
            """;

        // RPG Maker MV/MZ 的 JsonEx 内容就是一个数组
        const string mvJson = """[{"player":{"gold":100,"name":"Hero"}},[],{}]""";

        var samples = new List<Sample>
        {
            new("JSON", "sample.json", Utf8(json), "json", "gold"),
            new("XML", "sample.xml", Utf8(xml), "xml", "gold"),
            new("INI", "sample.ini", Utf8(ini), "ini", "Gold"),
            new("NBT", "sample.nbt", BuildNbt(), "nbt", "gold", ByteStable: true),
            new("plist（XML）", "sample.plist", Utf8(plistXml), "plist", "gold"),
            new("plist（binary）", "binary.plist",
                BinaryPlistWriter.Write(new List<KeyValuePair<string, object?>>
                {
                    new("gold", 100L),
                    new("name", "Hero"),
                }), "plist", "gold", ByteStable: true),
            new("MessagePack", "sample.msgpack", BuildMessagePack(), "msgpack", "gold"),
            new("CBOR", "sample.cbor", BuildCbor(), "cbor", "gold"),
            new("Ren'Py", "sample.save", BuildRenpy(), "renpy", "gold"),
            new("RPG Maker XP/VX/Ace", "sample.rvdata2", BuildRubyMarshal(), "rpgmaker", "gold"),
            new("RPG Maker MV/MZ（LZString）", "compressed.rpgsave",
                Utf8(LzString.CompressToBase64(mvJson)), "rpgmaker-mv", "gold"),
            new("RPG Maker MV/MZ（明文）", "plain.rpgsave", Utf8(mvJson), "rpgmaker-mv", "gold"),
            new("Unreal Engine", "sample.sav", BuildGvas(), "ue-sav", null, ByteStable: true),
            new("SVDT 加密容器（Unity）", "encrypted.sav", BuildSvdt(), "svdt"),
            new("未知二进制", "unknown.bin",
                new byte[] { 0x00, 0x13, 0x37, 0xFF, 0x42, 0x99, 0x01, 0x7E, 0x55, 0xAA }, "raw"),
        };

        foreach (var sample in samples)
            TestFormat(root, sample);
    }

    private static void TestFormat(string root, Sample sample)
    {
        string directory = Path.Combine(root, "formats");
        Directory.CreateDirectory(directory);

        string path = Path.Combine(directory, sample.FileName);
        File.WriteAllBytes(path, sample.Data);

        var service = SaveFileService.Default;
        SaveDocument document;

        try
        {
            document = service.Open(path);
        }
        catch (Exception ex)
        {
            Check($"{sample.Label}：打开不抛异常", false, ex.Message);
            return;
        }

        Check($"{sample.Label}：识别为 {sample.ExpectedFormat}",
            document.FormatId == sample.ExpectedFormat, $"实际 {document.FormatId}");

        if (document.FormatId != sample.ExpectedFormat)
        {
            var expectedFormat = service.Registry.Find(sample.ExpectedFormat);
            float score = expectedFormat?.Detect(sample.Data, sample.FileName) ?? 0f;
            string error = "无";

            try { expectedFormat?.Read(sample.Data, sample.FileName); }
            catch (Exception ex) { error = ex.Message; }

            Console.WriteLine($"    [debug] {sample.ExpectedFormat} 检测得分 {score:0.00}，读取错误：{error}");
        }

        Check($"{sample.Label}：解析出节点", document.Roots.Count > 0, "根节点为空");

        var format = service.Registry.Find(document.FormatId)
            ?? throw new InvalidOperationException("格式未注册");

        if (!format.CanWrite)
        {
            Check($"{sample.Label}：只读标记", document.IsReadOnly);

            bool blocked = false;
            try { service.Save(document, path + ".out"); }
            catch (NotSupportedException) { blocked = true; }

            Check($"{sample.Label}：只读格式拒绝写回", blocked);
            return;
        }

        // 不做修改直接写回，确认读→写闭环不破坏结构
        // 注意保留扩展名：部分格式（RPG Maker）靠扩展名识别
        string extension = Path.GetExtension(path);
        string stem = Path.GetFileNameWithoutExtension(path);
        string roundtripPath = Path.Combine(directory, $"{stem}.roundtrip{extension}");

        try
        {
            service.Save(document, roundtripPath);
            document.FilePath = path;

            var reopened = service.Open(roundtripPath);
            Check($"{sample.Label}：写回后可重新识别", reopened.FormatId == sample.ExpectedFormat,
                $"实际 {reopened.FormatId}");

            if (sample.ByteStable)
            {
                byte[] written = File.ReadAllBytes(roundtripPath);
                Check($"{sample.Label}：无修改时字节一致",
                    written.SequenceEqual(sample.Data),
                    $"{written.Length} 字节 vs 原始 {sample.Data.Length} 字节");
            }
        }
        catch (Exception ex)
        {
            Check($"{sample.Label}：无修改写回", false, ex.Message);
            return;
        }

        if (sample.TargetKey is null) return;

        var node = FindLeaf(document, sample.TargetKey);
        if (node is null)
        {
            Check($"{sample.Label}：找到字段 {sample.TargetKey}", false, "节点不存在");
            return;
        }

        object newValue = node.Kind == ValueKind.String ? "424242" : 424242L;

        try
        {
            node.SetValue(newValue);
            service.Save(document);

            var verify = service.Open(path);
            var verifyNode = FindLeaf(verify, sample.TargetKey);

            Check($"{sample.Label}：修改 {sample.TargetKey} 后重新打开生效",
                verifyNode is not null && verifyNode.DisplayValue.Contains("424242"),
                $"实际 {verifyNode?.DisplayValue ?? "null"}");
        }
        catch (Exception ex)
        {
            Check($"{sample.Label}：修改并保存", false, ex.Message);
        }
    }

    // ====================================================================
    // 压缩包裹
    // ====================================================================

    private static void RunWrapperTests(string root)
    {
        const string json =
            """{"player":{"gold":100,"name":"Hero"},"world":{"day":3}}""";

        byte[] payload = Utf8(json);

        var wrappers = new ISaveWrapper[]
        {
            new GzipWrapper(),
            new ZlibWrapper(),
            new ZstdWrapper(),
            new Lz4Wrapper(),
            new Bzip2Wrapper(),
            new BrotliWrapper(),
            new Base64Wrapper(),
            new PakoUtf8Wrapper(),
        };

        foreach (var wrapper in wrappers)
        {
            string directory = Path.Combine(root, "wrappers");
            Directory.CreateDirectory(directory);

            string path = Path.Combine(directory, $"wrapped-{wrapper.Id}.dat");
            File.WriteAllBytes(path, wrapper.Wrap(payload));

            var service = SaveFileService.Default;

            try
            {
                var document = service.Open(path);

                Check($"{wrapper.Id}：解包后识别为 JSON", document.FormatId == "json",
                    $"实际 {document.FormatId}");

                Check($"{wrapper.Id}：记录包裹层",
                    document.Wrapper?.Id == wrapper.Id, $"实际 {document.Wrapper?.Id ?? "null"}");

                var node = FindLeaf(document, "gold");
                if (node is null)
                {
                    Check($"{wrapper.Id}：找到 gold", false);
                    continue;
                }

                node.SetValue(424242L);
                service.Save(document);

                var verify = service.Open(path);
                var verifyNode = FindLeaf(verify, "gold");

                Check($"{wrapper.Id}：修改后写回并保留包裹",
                    verify.FormatId == "json" &&
                    verify.Wrapper?.Id == wrapper.Id &&
                    verifyNode?.DisplayValue == "424242",
                    $"格式 {verify.FormatId} / 包裹 {verify.Wrapper?.Id ?? "null"} / 值 {verifyNode?.DisplayValue}");
            }
            catch (Exception ex)
            {
                Check($"{wrapper.Id}：完整流程", false, ex.Message);
            }
        }
    }

    // ====================================================================
    // 真实存档
    // ====================================================================

    private static void RunRealSaveTests(string root)
    {
        string source = Path.Combine(RepoDemo, "file9.rmmzsave.bak");
        if (!File.Exists(source))
        {
            Check("真实 MZ 存档样例存在", false, source);
            return;
        }

        string directory = Path.Combine(root, "real-game");
        string dataDirectory = Path.Combine(directory, "data");
        string saveDirectory = Path.Combine(directory, "save");

        Directory.CreateDirectory(dataDirectory);
        Directory.CreateDirectory(saveDirectory);
        Directory.CreateDirectory(Path.Combine(directory, "img"));

        File.Copy(Path.Combine(RepoDemo, "data", "Items.json"),
            Path.Combine(dataDirectory, "Items.json"), overwrite: true);

        string path = Path.Combine(saveDirectory, "file9.rmmzsave");
        File.Copy(source, path, overwrite: true);

        var service = SaveFileService.Default;

        try
        {
            var document = service.Open(path);

            Console.WriteLine("    [debug] 解包后前 80 字节：" +
                Encoding.UTF8.GetString(document.PayloadBytes, 0,
                    Math.Min(80, document.PayloadBytes.Length)).ReplaceLineEndings(" "));

            string payloadText = Encoding.UTF8.GetString(document.PayloadBytes);
            int references = CountOccurrences(payloadText, "\"@\"");
            int floatRefs = CountOccurrences(payloadText, "\"@r\"");
            Console.WriteLine($"    [debug] JsonEx 引用 {@references} 个，浮点引用 {floatRefs} 个，长度 {payloadText.Length}");

            Check("MZ 存档：识别为 RPG Maker MV/MZ", document.FormatId == "rpgmaker-mv",
                $"实际 {document.FormatId}");

            Check("MZ 存档：解包 pako 层", document.Wrapper?.Id == "pako-utf8",
                $"实际 {document.Wrapper?.Id ?? "null"}");

            int nodes = document.Roots.Sum(r => r.Descendants().Count());
            Console.WriteLine($"    [debug] 节点数 {nodes}");
            Check("MZ 存档：解析出完整字段树", nodes > 20_000, $"仅 {nodes} 个节点");

            var database = GameDatabase.TryAutoLoad(path, document.FormatId);
            Check("MZ 存档：自动找到游戏数据表", database is not null,
                "未从 .demo 找到 Items.json");
            Check("MZ 存档：读到物品名称", database?.Items.Count > 100,
                $"物品表 {database?.Items.Count ?? 0} 项");

            int mapped = database?.ApplyTo(document) ?? 0;
            Check("MZ 存档：物品 ID 映射到名称", mapped > 20, $"仅映射 {mapped} 项");

            var inventory = InventoryScanner.Scan(document, database);
            Check("MZ 存档：库存扫描到条目", inventory.Count > 20, $"仅 {inventory.Count} 条");

            var trout = inventory.FirstOrDefault(e => e.Id == "51");
            Check("MZ 存档：51 号物品命名为「樱花鳟」", trout?.Name == "樱花鳟",
                $"实际 {trout?.Name ?? "null"}");

            Check("MZ 存档：库存条目全部有名称",
                inventory.All(e => e.Name is not null),
                $"{inventory.Count(e => e.Name is null)} 条没有名称");

            var target = inventory.FirstOrDefault(e => e.Category == InventoryCategory.Items);
            if (target is not null)
            {
                target.Node.SetValue(42L);
                service.Save(document);

                var verify = service.Open(path);
                var verifyInventory = InventoryScanner.Scan(verify, database);
                var verifyTarget = verifyInventory.FirstOrDefault(e => e.Id == target.Id
                    && e.Category == target.Category);

                Check("MZ 存档：物品数量修改后重新打开生效",
                    verifyTarget?.Quantity == 42,
                    $"实际 {verifyTarget?.Quantity.ToString() ?? "null"}");
            }
        }
        catch (Exception ex)
        {
            Check("MZ 存档：完整流程", false, ex.Message);
        }
    }

    // ====================================================================
    // 游戏根目录
    // ====================================================================

    private static void RunGameRootTests(string root)
    {
        var candidates = GameRootLocator.Locate(RepoDemo);

        Check("游戏根目录：找到数据表", candidates.Count >= 1, $"{candidates.Count} 个候选");

        var pick = GameRootLocator.Prefer(candidates, Path.Combine(RepoDemo, "file9.rmmzsave.bak"));
        Check("游戏根目录：优先选中存档所在的游戏",
            pick is not null &&
            Path.GetFullPath(pick.DataDirectory)
                .Equals(Path.GetFullPath(Path.Combine(RepoDemo, "data")), StringComparison.OrdinalIgnoreCase),
            pick?.DataDirectory ?? "null");

        // 没有任何 RPG Maker 特征的目录不应该被自动探测采用（防串档）
        string fakeGame = Path.Combine(root, "fake-game");
        Directory.CreateDirectory(Path.Combine(fakeGame, "data"));
        Directory.CreateDirectory(Path.Combine(fakeGame, "saves"));

        File.Copy(Path.Combine(RepoDemo, "data", "Items.json"),
            Path.Combine(fakeGame, "data", "Items.json"), overwrite: true);

        string fakeSave = Path.Combine(fakeGame, "saves", "slot1.rmmzsave");
        File.WriteAllBytes(fakeSave, Utf8(
            """[{"party":{"_gold":100}},[],{}]"""));

        Check("游戏根目录：缺少游戏特征时不自动套用数据库",
            GameDatabase.TryAutoLoad(fakeSave, "rpgmaker-mv") is null);

        // 用户显式指定的目录仍然尊重（这就是「游戏根目录定位」的意义）
        Check("游戏根目录：显式指定时可用",
            GameRootLocator.Locate(fakeGame).Count >= 1);
    }

    // ====================================================================
    // 样本构造
    // ====================================================================

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;

        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static byte[] BuildNbt()
    {
        var writer = new EndianWriter(Endianness.Big);

        writer.WriteByte(10);   // TAG_Compound
        writer.WriteUInt16(0);  // root name

        writer.WriteByte(3);    // TAG_Int
        WriteNbtName(writer, "gold");
        writer.WriteInt32(100);

        writer.WriteByte(8);    // TAG_String
        WriteNbtName(writer, "name");
        WriteNbtName(writer, "Hero");

        writer.WriteByte(0);    // TAG_End

        return writer.ToArray();
    }

    private static void WriteNbtName(EndianWriter writer, string name)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(name);
        writer.WriteUInt16((ushort)bytes.Length);
        writer.WriteBytes(bytes);
    }

    private static byte[] BuildMessagePack()
    {
        using var stream = new MemoryStream();

        stream.WriteByte(0x82);            // fixmap, 2 pairs
        WriteMessagePackString(stream, "gold");
        stream.WriteByte(0x64);            // positive fixint 100
        WriteMessagePackString(stream, "name");
        WriteMessagePackString(stream, "Hero");

        return stream.ToArray();
    }

    private static void WriteMessagePackString(Stream stream, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        stream.WriteByte((byte)(0xA0 | bytes.Length));   // fixstr
        stream.Write(bytes);
    }

    private static byte[] BuildCbor()
    {
        using var stream = new MemoryStream();

        stream.WriteByte(0xA2);            // map, 2 pairs
        WriteCborText(stream, "gold");
        stream.WriteByte(0x18);            // uint8
        stream.WriteByte(100);
        WriteCborText(stream, "name");
        WriteCborText(stream, "Hero");

        return stream.ToArray();
    }

    private static void WriteCborText(Stream stream, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        stream.WriteByte((byte)(0x60 | bytes.Length));   // text string
        stream.Write(bytes);
    }

    private static byte[] BuildRenpy()
    {
        var store = new PickleDict
        {
            new KeyValuePair<object?, object?>("gold", 100L),
            new KeyValuePair<object?, object?>("name", "Hero"),
        };

        var root = new PickleTuple(new object?[] { store, new PickleList() });
        byte[] pickle = PickleWriter.Write(root);

        byte[] compressed;
        using (var output = new MemoryStream())
        {
            using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(pickle, 0, pickle.Length);
            compressed = output.ToArray();
        }

        using var zipStream = new MemoryStream();
        using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("log", CompressionLevel.NoCompression);
            using var entryStream = entry.Open();
            entryStream.Write(compressed, 0, compressed.Length);
        }

        return zipStream.ToArray();
    }

    private static byte[] BuildRubyMarshal()
    {
        var items = new RubyHash
        {
            new KeyValuePair<object?, object?>(51L, 3L),
            new KeyValuePair<object?, object?>(91L, 2L),
        };

        var party = new RubyObject("Game_Party");
        party.Ivars.Add(new("@items", items));
        party.Ivars.Add(new("@gold", 100L));

        return RubyMarshalWriter.Write(party, useEncodingIvars: false);
    }

    /// <summary>
    /// SVDT 容器：4 字节魔数 + 1 字节版本 + 高熵负载。
    /// 只读格式，测试识别、预览与拒绝写回。
    /// </summary>
    private static byte[] BuildSvdt()
    {
        using var stream = new MemoryStream();

        stream.Write(Encoding.ASCII.GetBytes("SVDT"));
        stream.WriteByte(1);

        for (int i = 0; i < 64; i++)
            stream.WriteByte((byte)((i * 73 + 19) & 0xFF));

        return stream.ToArray();
    }

    /// <summary>
    /// 手工拼一份最小 GVAS：只有文件头，没有任何属性。
    /// 用于验证 UE 适配器的识别 / 读取 / 无损写回。
    /// </summary>
    private static byte[] BuildGvas()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, true);

        writer.Write(Encoding.ASCII.GetBytes("GVAS"));

        writer.Write(2);        // SaveGameFileVersion.AddedCustomVersions
        writer.Write(522);      // EObjectUE4Version

        writer.Write((short)4); // EngineVersion
        writer.Write((short)27);
        writer.Write((short)2);
        writer.Write(0);
        WriteFString(writer, "++UE4+Release-4.27");

        writer.Write(3);        // CustomFormatData.Version
        writer.Write(0);        // 没有自定义格式

        WriteFString(writer, "SaveGame");
        WriteFString(writer, "None");   // 属性列表结束
        writer.Write(0);                // null terminator

        writer.Flush();
        return stream.ToArray();
    }

    private static void WriteFString(BinaryWriter writer, string text)
    {
        if (text.Length == 0)
        {
            writer.Write(0);
            return;
        }

        byte[] bytes = Encoding.ASCII.GetBytes(text);
        writer.Write(bytes.Length + 1);
        writer.Write(bytes);
        writer.Write((byte)0);
    }

    // ====================================================================
    // 断言
    // ====================================================================

    private static SaveNode? FindLeaf(SaveDocument document, string key)
    {
        string wanted = Normalize(key);

        foreach (var root in document.Roots)
        {
            foreach (var node in root.Descendants())
            {
                if (node.IsContainer) continue;
                if (Normalize(node.Name) == wanted) return node;
            }
        }

        return null;
    }

    private static string Normalize(string name) =>
        name.TrimStart('@', '_').Replace("_", string.Empty).ToLowerInvariant();

    private static void Check(string name, bool condition, string? detail = null)
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine($"  [PASS] {name}");
            return;
        }

        _failed++;
        string message = detail is null ? name : $"{name}（{detail}）";
        Failures.Add(message);
        Console.WriteLine($"  [FAIL] {message}");
    }
}
