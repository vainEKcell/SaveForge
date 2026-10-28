namespace SaveForge.Core;

/// <summary>从用户指定的游戏根目录里找到的一份数据表。</summary>
public sealed class GameRootCandidate
{
    /// <summary>实际包含 Items.json 等文件的目录（<game>/data 或 <game>/www/data）。</summary>
    public required string DataDirectory { get; init; }

    public required GameDatabase Database { get; init; }

    /// <summary>数据目录所属的游戏根。</summary>
    public string GameRoot => Path.GetDirectoryName(DataDirectory) ?? DataDirectory;

    public string Summary => $"{Database.Summary} · {DataDirectory}";
}

/// <summary>
/// 游戏根目录定位。
///
/// 用户只需要选游戏文件夹，程序自己下去找数据表：
///   &lt;game&gt;\data\Items.json          （XP/VX/VX Ace/MV 部署后常见）
///   &lt;game&gt;\www\data\Items.json      （MV/MZ 网页版布局）
///   &lt;game&gt;\&lt;子目录&gt;\data\...       （用户选到了上一层）
///
/// 找不到就明确返回空，不猜、不串档。
/// </summary>
public static class GameRootLocator
{
    private const int MaxDepth = 2;
    private const int MaxDirectories = 400;

    /// <summary>扫描一个根目录，返回所有可用的数据表集合。</summary>
    public static IReadOnlyList<GameRootCandidate> Locate(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return [];

        var dataDirectories = new List<string>();
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));

        int visited = 0;

        while (queue.Count > 0 && visited < MaxDirectories)
        {
            var (directory, depth) = queue.Dequeue();
            visited++;

            if (HasTables(directory) && !dataDirectories.Contains(directory))
                dataDirectories.Add(directory);

            if (depth >= MaxDepth) continue;

            foreach (string child in SafeEnumerate(directory))
            {
                // 跳过明显不是游戏数据的目录，避免在大型文件夹里空转
                string name = Path.GetFileName(child);
                if (name is "node_modules" or "$RECYCLE.BIN" or "System Volume Information") continue;

                queue.Enqueue((child, depth + 1));
            }
        }

        return dataDirectories
            .Select(directory => new GameRootCandidate
            {
                DataDirectory = directory,
                Database = GameDatabase.LoadDirectory(directory),
            })
            .Where(candidate => !candidate.Database.IsEmpty)
            .ToList();
    }

    /// <summary>
    /// 多个候选时选一个最可信的：
    ///   1. 当前存档就在这个游戏目录里（避免把别的游戏数据套上来）
    ///   2. 游戏根带 RPG Maker 结构特征（Game.exe / img / js 等）
    ///   3. 否则取第一个
    /// </summary>
    public static GameRootCandidate? Prefer(IReadOnlyList<GameRootCandidate> candidates, string? savePath)
    {
        if (candidates.Count == 0) return null;

        if (!string.IsNullOrEmpty(savePath))
        {
            var match = candidates.FirstOrDefault(c => IsUnder(savePath!, c.GameRoot));
            if (match is not null) return match;
        }

        var signed = candidates.FirstOrDefault(c => GameDatabase.LooksLikeRpgMakerGame(c.GameRoot));
        if (signed is not null) return signed;

        return candidates[0];
    }

    private static bool HasTables(string directory) =>
        File.Exists(Path.Combine(directory, "Items.json")) ||
        File.Exists(Path.Combine(directory, "Weapons.json")) ||
        File.Exists(Path.Combine(directory, "Armors.json"));

    private static bool IsUnder(string filePath, string directory)
    {
        string fullDirectory = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        string fullFile = Path.GetFullPath(filePath);

        return fullFile.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> SafeEnumerate(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory).ToArray();
        }
        catch
        {
            return [];
        }
    }
}
