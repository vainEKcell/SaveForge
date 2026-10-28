using System.Collections.ObjectModel;
using System.ComponentModel;
using SaveForge.Core;

namespace SaveForge.App.ViewModels;

/// <summary>
/// 树节点的视图模型。包装 Core 的 SaveNode，负责界面展示与变更通知。
/// 支持按关键词过滤：只保留匹配节点及其祖先路径。
/// </summary>
public sealed class FieldNodeVm : INotifyPropertyChanged
{
    public SaveNode Model { get; }

    public ObservableCollection<FieldNodeVm> Children { get; } = new();

    public FieldNodeVm(SaveNode model, int depth = 0, string? keyword = null)
    {
        Model = model;

        // 搜索时全部展开，方便直接看到命中项；平时只展开前两层
        bool parentIsArray = model.Kind == ValueKind.Array;
        IsExpanded = keyword is not null || (depth < 2 && !parentIsArray);

        foreach (var child in model.Children)
        {
            if (keyword is not null && !Matches(child, keyword)) continue;
            Children.Add(new FieldNodeVm(child, depth + 1, keyword));
        }
    }

    /// <summary>节点自身或其任一代后代是否命中关键词。</summary>
    public static bool Matches(SaveNode node, string keyword)
    {
        if (node.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return true;

        if (!node.IsContainer &&
            node.DisplayValue.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var child in node.Children)
            if (Matches(child, keyword))
                return true;

        return false;
    }

    /// <summary>统计一组节点下命中关键词的叶子数量。</summary>
    public static int CountMatches(IEnumerable<SaveNode> nodes, string keyword)
    {
        int count = 0;

        foreach (var node in nodes)
        {
            foreach (var descendant in node.Descendants())
            {
                if (descendant.IsContainer) continue;

                if (descendant.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    descendant.DisplayValue.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    count++;
            }
        }

        return count;
    }

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnPropertyChanged(nameof(IsExpanded));
        }
    }

    public string Name => Model.Name;

    public string KindLabel => Model.Kind switch
    {
        ValueKind.Object => "对象",
        ValueKind.Array => "数组",
        ValueKind.String => "文本",
        ValueKind.Integer => "整数",
        ValueKind.Float => "浮点",
        ValueKind.Boolean => "布尔",
        ValueKind.Binary => "字节",
        ValueKind.Null => "空",
        _ => "未知",
    };

    public string ValueText => Model.DisplayValue;

    public bool IsContainer => Model.IsContainer;

    public bool IsModified => Model.IsModified;

    public bool IsEditable => Model.IsEditable && !Model.IsContainer;

    public string Locator => Model.Locator ?? string.Empty;

    /// <summary>模型变更后刷新受影响的展示属性。</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(ValueText));
        OnPropertyChanged(nameof(IsModified));
    }

    public void RefreshRecursive()
    {
        Refresh();
        foreach (var child in Children) child.RefreshRecursive();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
