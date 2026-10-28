namespace SaveForge.Core;

/// <summary>差异类型。</summary>
public enum DiffKind
{
    /// <summary>右侧存档新增的字段。</summary>
    Added,

    /// <summary>右侧存档缺失的字段。</summary>
    Removed,

    /// <summary>两侧都存在但值不同。</summary>
    Modified,
}

/// <summary>一条差异记录。</summary>
public sealed class SaveDiffEntry
{
    /// <summary>字段路径，如 "存档/store/gold"。</summary>
    public required string Path { get; init; }

    public required DiffKind Kind { get; init; }

    /// <summary>左侧（基准）值，新增字段为 null。</summary>
    public string? ValueA { get; init; }

    /// <summary>右侧（对比）值，删除字段为 null。</summary>
    public string? ValueB { get; init; }

    /// <summary>类型描述，如 "Integer" 或 "Struct → String"。</summary>
    public string TypeName { get; init; } = string.Empty;

    public string KindText => Kind switch
    {
        DiffKind.Added => "新增",
        DiffKind.Removed => "删除",
        DiffKind.Modified => "修改",
        _ => "?",
    };
}

/// <summary>
/// 存档差异比较。按节点名称递归对齐两棵树，输出新增 / 删除 / 修改三类差异。
///
/// 只比较叶子节点的值；容器本身不产生差异项，但容器下任一侧缺失的子树会
/// 被整体标记为新增或删除（逐叶子展开，便于直接看到具体字段）。
/// </summary>
public static class SaveDiff
{
    public static IReadOnlyList<SaveDiffEntry> Compare(SaveDocument baseline, SaveDocument target)
    {
        var entries = new List<SaveDiffEntry>();

        var leftRoots = baseline.Roots.ToDictionary(r => r.Name, StringComparer.Ordinal);
        var rightRoots = target.Roots.ToDictionary(r => r.Name, StringComparer.Ordinal);

        foreach (string name in UnionKeys(leftRoots, rightRoots))
        {
            leftRoots.TryGetValue(name, out SaveNode? a);
            rightRoots.TryGetValue(name, out SaveNode? b);

            Walk(a, b, name, entries);
        }

        return entries;
    }

    /// <summary>生成一行统计描述，如 "修改 3 项，新增 1 项"。</summary>
    public static string Summarize(IReadOnlyList<SaveDiffEntry> entries)
    {
        if (entries.Count == 0) return "两份存档内容一致";

        int modified = entries.Count(e => e.Kind == DiffKind.Modified);
        int added = entries.Count(e => e.Kind == DiffKind.Added);
        int removed = entries.Count(e => e.Kind == DiffKind.Removed);

        var parts = new List<string>();

        if (modified > 0) parts.Add($"修改 {modified} 项");
        if (added > 0) parts.Add($"新增 {added} 项");
        if (removed > 0) parts.Add($"删除 {removed} 项");

        return string.Join("，", parts);
    }

    // ====================================================================
    // 递归比较
    // ====================================================================

    private static void Walk(SaveNode? left, SaveNode? right, string path, List<SaveDiffEntry> entries)
    {
        if (left is null && right is null) return;

        if (left is null)
        {
            CollectSubtree(right!, path, DiffKind.Added, entries);
            return;
        }

        if (right is null)
        {
            CollectSubtree(left, path, DiffKind.Removed, entries);
            return;
        }

        bool leftIsContainer = left.IsContainer;
        bool rightIsContainer = right.IsContainer;

        // 结构类型变了（容器 ↔ 叶子），整体视为修改
        if (leftIsContainer != rightIsContainer)
        {
            entries.Add(new SaveDiffEntry
            {
                Path = path,
                Kind = DiffKind.Modified,
                TypeName = $"{left.Kind} → {right.Kind}",
                ValueA = left.DisplayValue,
                ValueB = right.DisplayValue,
            });
            return;
        }

        if (!leftIsContainer)
        {
            if (!ValuesEqual(left.Value, right.Value))
            {
                entries.Add(new SaveDiffEntry
                {
                    Path = path,
                    Kind = DiffKind.Modified,
                    TypeName = right.Kind.ToString(),
                    ValueA = left.DisplayValue,
                    ValueB = right.DisplayValue,
                });
            }
            return;
        }

        // 容器：按子节点名对齐
        var leftChildren = new Dictionary<string, SaveNode>(StringComparer.Ordinal);
        foreach (var child in left.Children) leftChildren[child.Name] = child;

        var rightChildren = new Dictionary<string, SaveNode>(StringComparer.Ordinal);
        foreach (var child in right.Children) rightChildren[child.Name] = child;

        foreach (string name in UnionKeys(leftChildren, rightChildren))
        {
            leftChildren.TryGetValue(name, out SaveNode? a);
            rightChildren.TryGetValue(name, out SaveNode? b);

            Walk(a, b, $"{path}/{name}", entries);
        }
    }

    /// <summary>把一棵子树的所有叶子记为新增或删除。</summary>
    private static void CollectSubtree(SaveNode node, string path, DiffKind kind, List<SaveDiffEntry> entries)
    {
        if (!node.IsContainer)
        {
            entries.Add(new SaveDiffEntry
            {
                Path = path,
                Kind = kind,
                TypeName = node.Kind.ToString(),
                ValueA = kind == DiffKind.Removed ? node.DisplayValue : null,
                ValueB = kind == DiffKind.Added ? node.DisplayValue : null,
            });
            return;
        }

        foreach (var child in node.Children)
            CollectSubtree(child, $"{path}/{child.Name}", kind, entries);
    }

    // ====================================================================
    // 辅助
    // ====================================================================

    private static IEnumerable<string> UnionKeys<T>(
        Dictionary<string, T> left,
        Dictionary<string, T> right)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (string key in left.Keys)
            if (seen.Add(key))
                yield return key;

        foreach (string key in right.Keys)
            if (seen.Add(key))
                yield return key;
    }

    private static bool ValuesEqual(object? left, object? right)
    {
        if (left is null && right is null) return true;
        if (left is null || right is null) return false;

        if (left is byte[] a && right is byte[] b)
            return a.AsSpan().SequenceEqual(b);

        if (IsNumeric(left) && IsNumeric(right))
        {
            try { return Convert.ToDouble(left) == Convert.ToDouble(right); }
            catch { return false; }
        }

        return left.Equals(right);
    }

    private static bool IsNumeric(object value) =>
        value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double;
}
