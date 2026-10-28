using System.Text.RegularExpressions;

namespace SaveForge.Core;

/// <summary>
/// 批量修改：按字段名通配符匹配所有可编辑叶子，统一设置新值。
///
/// 典型场景：把所有角色的 Level 改成同一个值、把所有开关置 0 等。
/// 匹配只基于节点名（不含路径），支持 * 与 ? 通配符。
/// </summary>
public static class SaveBatchEdit
{
    /// <summary>按字段名模式匹配可编辑叶子。</summary>
    public static IReadOnlyList<SaveNode> Match(SaveDocument document, string pattern)
    {
        var result = new List<SaveNode>();

        if (string.IsNullOrWhiteSpace(pattern)) return result;

        Regex regex;
        try
        {
            regex = new Regex(WildcardToRegex(pattern), RegexOptions.IgnoreCase);
        }
        catch
        {
            return result;
        }

        foreach (var root in document.Roots)
        {
            foreach (var node in root.Descendants())
            {
                if (node.IsContainer || !node.IsEditable) continue;
                if (regex.IsMatch(node.Name)) result.Add(node);
            }
        }

        return result;
    }

    /// <summary>把原始值按各节点的原类型转换后写入，返回成功修改的数量。</summary>
    public static int Apply(IReadOnlyList<SaveNode> nodes, string rawValue)
    {
        int applied = 0;

        foreach (var node in nodes)
        {
            object? value = Parse(rawValue, node.Kind);
            if (value is null && node.Kind != ValueKind.Null) continue;

            node.SetValue(value);
            applied++;
        }

        return applied;
    }

    /// <summary>按目标类型解析用户输入；失败返回 null（不修改）。</summary>
    public static object? Parse(string raw, ValueKind kind)
    {
        string text = raw.Trim();

        switch (kind)
        {
            case ValueKind.String:
                return raw;

            case ValueKind.Integer:
                if (long.TryParse(text, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out long integer))
                    return integer;
                return null;

            case ValueKind.Float:
                if (double.TryParse(text, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double number))
                    return number;
                return null;

            case ValueKind.Boolean:
                if (bool.TryParse(text, out bool flag)) return flag;
                if (text == "1") return true;
                if (text == "0") return false;
                return null;

            case ValueKind.Null:
                return null;

            default:
                return raw;
        }
    }

    private static string WildcardToRegex(string pattern)
    {
        string escaped = Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".");

        return $"^{escaped}$";
    }
}
