namespace SaveForge.Core;

/// <summary>库存分类。RPG Maker 的存档把三类道具分开存。</summary>
public enum InventoryCategory
{
    Items,
    Weapons,
    Armors,
}

/// <summary>
/// 库存里的一条记录：存档里的「ID → 数量」，加上游戏数据表里的名称。
/// 直接持有 SaveNode，界面改动数量即可原地回写。
/// </summary>
public sealed class InventoryEntry
{
    public required InventoryCategory Category { get; init; }

    /// <summary>存档里的键（通常是数字 ID 的字符串形式）。</summary>
    public required string Id { get; init; }

    /// <summary>保存数量的节点。</summary>
    public required SaveNode Node { get; init; }

    /// <summary>游戏数据表里的名称。没有数据库时为 null。</summary>
    public string? Name { get; set; }

    /// <summary>界面上显示的名称，没有数据库时退回「物品 #51」。</summary>
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Name)) return Name!;
            return Category switch
            {
                InventoryCategory.Weapons => $"武器 #{Id}",
                InventoryCategory.Armors => $"防具 #{Id}",
                _ => $"物品 #{Id}",
            };
        }
    }

    /// <summary>当前数量。</summary>
    public long Quantity
    {
        get
        {
            try { return Convert.ToInt64(Node.Value ?? 0L); }
            catch { return 0L; }
        }
    }
}

/// <summary>
/// 在任意存档里寻找库存表。
///
/// 不依赖固定的 JSON 路径，而是按字段名找容器：
///   MZ/MV   party._items / _weapons / _armors
///   XP/VX   @items / @weapons / @armors（Ruby Marshal 的实例变量）
/// 找到的容器必须真的有「数字键 → 数量」的子节点，避免误认同名数组。
/// </summary>
public static class InventoryScanner
{
    /// <summary>扫描全部库存条目，并按数据库补上名称。</summary>
    public static IReadOnlyList<InventoryEntry> Scan(SaveDocument document, GameDatabase? database)
    {
        var result = new List<InventoryEntry>();

        foreach (var root in document.Roots)
        {
            foreach (var node in root.Descendants())
            {
                if (node.Kind != ValueKind.Object) continue;

                foreach (InventoryCategory category in Enum.GetValues<InventoryCategory>())
                {
                    if (!IsCategoryName(node.Name, category)) continue;
                    if (!HasNumericEntries(node)) continue;

                    foreach (var child in node.Children)
                    {
                        if (!long.TryParse(child.Name, out long id)) continue;
                        if (child.IsContainer) continue;
                        if (!IsNumericKind(child.Kind)) continue;

                        var entry = new InventoryEntry
                        {
                            Category = category,
                            Id = child.Name,
                            Node = child,
                        };

                        entry.Name = ResolveName(database, category, id);

                        // 同时把名称写到节点的别名上，字段树里也能看到中文名
                        if (entry.Name is not null && string.IsNullOrEmpty(child.Alias))
                            child.Alias = entry.Name;

                        result.Add(entry);
                    }
                }
            }
        }

        // 同一个节点只保留一次（理论上不会重复，防御性去重）
        return result
            .GroupBy(e => (e.Category, e.Node))
            .Select(g => g.First())
            .ToList();
    }

    /// <summary>取某一类库存的容器节点（没有则 null）。</summary>
    public static SaveNode? FindContainer(SaveDocument document, InventoryCategory category)
    {
        foreach (var root in document.Roots)
        {
            foreach (var node in root.Descendants())
            {
                if (node.Kind != ValueKind.Object) continue;
                if (!IsCategoryName(node.Name, category)) continue;
                if (HasNumericEntries(node)) return node;
            }
        }

        return null;
    }

    /// <summary>类别显示名。</summary>
    public static string DisplayName(InventoryCategory category) => category switch
    {
        InventoryCategory.Weapons => "武器",
        InventoryCategory.Armors => "防具",
        _ => "物品",
    };

    private static string? ResolveName(GameDatabase? database, InventoryCategory category, long id)
    {
        if (database is null || id is < int.MinValue or > int.MaxValue) return null;

        var table = category switch
        {
            InventoryCategory.Weapons => database.Weapons,
            InventoryCategory.Armors => database.Armors,
            _ => database.Items,
        };

        return table.TryGetValue((int)id, out string? name) ? name : null;
    }

    private static bool IsCategoryName(string name, InventoryCategory category)
    {
        string normalized = GameDatabase.NormalizeFieldName(name);

        return category switch
        {
            InventoryCategory.Weapons => normalized == "weapons",
            InventoryCategory.Armors => normalized == "armors",
            _ => normalized == "items",
        };
    }

    private static bool HasNumericEntries(SaveNode node) =>
        node.Children.Any(c => long.TryParse(c.Name, out _) && !c.IsContainer && IsNumericKind(c.Kind));

    private static bool IsNumericKind(ValueKind kind) =>
        kind is ValueKind.Integer or ValueKind.Float;
}
