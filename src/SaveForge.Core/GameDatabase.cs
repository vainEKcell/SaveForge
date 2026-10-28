using System.Text.Json.Nodes;

namespace SaveForge.Core;

/// <summary>
/// RPG Maker 游戏数据库（data/Items.json 等）。
///
/// 存档里的库存是 {物品ID: 数量}，单看 ID 毫无意义。
/// 加载游戏自带的数据库后，可以把 ID 映射成名称，
/// 例如 "51" → "樱花鳟"、"140" → "木材"。
///
/// 自动探测路径：从存档所在目录向上最多 4 层，寻找 data/ 或 www/data/。
/// </summary>
public sealed class GameDatabase
{
    /// <summary>物品表（Items.json）。</summary>
    public IReadOnlyDictionary<int, string> Items { get; init; } = new Dictionary<int, string>();

    /// <summary>武器表（Weapons.json）。</summary>
    public IReadOnlyDictionary<int, string> Weapons { get; init; } = new Dictionary<int, string>();

    /// <summary>防具表（Armors.json）。</summary>
    public IReadOnlyDictionary<int, string> Armors { get; init; } = new Dictionary<int, string>();

    /// <summary>数据库所在目录（手动加载单个文件时为空）。</summary>
    public string? DataDirectory { get; init; }

    /// <summary>是否成功读到任何一张表。</summary>
    public bool IsEmpty => Items.Count == 0 && Weapons.Count == 0 && Armors.Count == 0;

    public int TotalEntries => Items.Count + Weapons.Count + Armors.Count;

    /// <summary>一句话描述，用于界面提示。</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (Items.Count > 0) parts.Add($"物品 {Items.Count}");
            if (Weapons.Count > 0) parts.Add($"武器 {Weapons.Count}");
            if (Armors.Count > 0) parts.Add($"防具 {Armors.Count}");
            return parts.Count == 0 ? "无数据" : string.Join(" / ", parts);
        }
    }

    // ====================================================================
    // 加载
    // ====================================================================

    /// <summary>
    /// 从存档路径自动探测游戏数据目录。找不到、或不是 RPG Maker 存档时返回 null。
    ///
    /// 两道防线，避免把别的游戏的数据库套到当前存档上（串档）：
    ///   1. 只服务 RPG Maker 存档（扩展名或格式判定）
    ///   2. data 目录的父目录必须像 RPG Maker 游戏根（有 Game.exe / img / js 等特征）
    /// </summary>
    public static GameDatabase? TryAutoLoad(string saveFilePath, string? formatId = null)
    {
        if (string.IsNullOrEmpty(saveFilePath)) return null;

        bool isRpgMaker = saveFilePath.EndsWith(".rmmzsave", StringComparison.OrdinalIgnoreCase)
                       || saveFilePath.EndsWith(".rpgsave", StringComparison.OrdinalIgnoreCase)
                       || formatId is "rpgmaker" or "rpgmaker-mv";

        if (!isRpgMaker) return null;

        string? directory = Path.GetDirectoryName(saveFilePath);
        if (string.IsNullOrEmpty(directory)) return null;

        var current = new DirectoryInfo(directory);

        // 存档通常在 <game>/save/ 下，向上 3 层足够
        for (int depth = 0; depth < 3 && current is not null; depth++)
        {
            foreach (string candidate in new[]
                     {
                         Path.Combine(current.FullName, "data"),
                         Path.Combine(current.FullName, "www", "data"),
                     })
            {
                if (!HasDatabase(candidate)) continue;

                // data 的父目录（<game> 或 <game>/www）必须带 RPG Maker 的结构特征
                string? gameRoot = Path.GetDirectoryName(candidate);
                if (gameRoot is null || !LooksLikeRpgMakerGame(gameRoot)) continue;

                var database = LoadDirectory(candidate);
                if (!database.IsEmpty) return database;
            }

            current = current.Parent;
        }

        return null;
    }

    private static bool HasDatabase(string dataDirectory) =>
        File.Exists(Path.Combine(dataDirectory, "Items.json")) ||
        File.Exists(Path.Combine(dataDirectory, "Weapons.json")) ||
        File.Exists(Path.Combine(dataDirectory, "Armors.json"));

    /// <summary>
    /// 判断目录是否是 RPG Maker 游戏根（或 MV/MZ 的 www 目录）。
    /// 只靠 data/ 存在不够——玩家的存档目录常常和别的游戏混在同一层。
    /// </summary>
    private static bool LooksLikeRpgMakerGame(string root)
    {
        if (Directory.Exists(Path.Combine(root, "img"))) return true;
        if (Directory.Exists(Path.Combine(root, "audio"))) return true;
        if (Directory.Exists(Path.Combine(root, "js"))) return true;

        if (File.Exists(Path.Combine(root, "Game.exe"))) return true;
        if (File.Exists(Path.Combine(root, "Game.rpgproject"))) return true;
        if (File.Exists(Path.Combine(root, "package.json"))) return true;
        if (File.Exists(Path.Combine(root, "nw.dll"))) return true;

        return false;
    }

    /// <summary>加载一个 data 目录下的三张表（缺失的跳过）。</summary>
    public static GameDatabase LoadDirectory(string dataDirectory)
    {
        return new GameDatabase
        {
            DataDirectory = dataDirectory,
            Items = ParseTable(Path.Combine(dataDirectory, "Items.json")),
            Weapons = ParseTable(Path.Combine(dataDirectory, "Weapons.json")),
            Armors = ParseTable(Path.Combine(dataDirectory, "Armors.json")),
        };
    }

    /// <summary>加载单个数据库文件，按文件名判断属于哪张表。</summary>
    public static GameDatabase LoadFile(string jsonPath)
    {
        var table = ParseTable(jsonPath);
        string name = Path.GetFileName(jsonPath).ToLowerInvariant();

        return name switch
        {
            "weapons.json" => new GameDatabase { Weapons = table },
            "armors.json" => new GameDatabase { Armors = table },
            _ => new GameDatabase { Items = table },
        };
    }

    /// <summary>
    /// 解析 RPG Maker 的数组式数据表：[null, {id, name, ...}, ...]
    /// 索引即 ID，但也兼容元素里自带 id 的情况。
    /// </summary>
    private static Dictionary<int, string> ParseTable(string path)
    {
        var result = new Dictionary<int, string>();

        if (!File.Exists(path)) return result;

        try
        {
            string json = File.ReadAllText(path);
            if (JsonNode.Parse(json) is not JsonArray array) return result;

            for (int index = 0; index < array.Count; index++)
            {
                if (array[index] is not JsonObject entry) continue;

                int id = index;

                if (entry["id"] is JsonValue idValue && idValue.TryGetValue(out int declared))
                    id = declared;

                if (entry["name"] is not JsonValue nameValue) continue;
                if (!nameValue.TryGetValue(out string? name)) continue;
                if (string.IsNullOrWhiteSpace(name)) continue;

                result[id] = name.Trim();
            }
        }
        catch
        {
            // 数据库损坏时静默降级，不影响存档本身
        }

        return result;
    }

    // ====================================================================
    // 应用
    // ====================================================================

    /// <summary>
    /// 把数据库名称写到存档节点上（Alias），返回成功映射的字段数。
    /// 只处理 party 下的 _items / _weapons / _armors。
    /// </summary>
    public int ApplyTo(SaveDocument document)
    {
        if (IsEmpty) return 0;

        int applied = 0;

        foreach (var root in document.Roots)
        {
            foreach (var node in root.Descendants())
            {
                if (node.Kind != ValueKind.Object || node.Name != "party") continue;

                applied += ApplyTable(node, "_items", Items);
                applied += ApplyTable(node, "_weapons", Weapons);
                applied += ApplyTable(node, "_armors", Armors);
            }
        }

        return applied;
    }

    private static int ApplyTable(SaveNode party, string fieldName, IReadOnlyDictionary<int, string> table)
    {
        if (table.Count == 0) return 0;

        var container = party.Children.FirstOrDefault(c => c.Name == fieldName);
        if (container is null || !container.IsContainer) return 0;

        int count = 0;

        foreach (var child in container.Children)
        {
            if (!int.TryParse(child.Name, out int id)) continue;
            if (!table.TryGetValue(id, out string? name)) continue;

            child.Alias = name;
            count++;
        }

        return count;
    }
}
