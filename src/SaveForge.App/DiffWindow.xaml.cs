using System.Collections.ObjectModel;
using System.Windows;
using SaveForge.Core;

namespace SaveForge.App;

/// <summary>
/// 存档对比窗口：列出两份存档之间的字段差异。
/// </summary>
public partial class DiffWindow : Window
{
    private readonly ObservableCollection<SaveDiffEntry> _rows = new();

    public DiffWindow(SaveDocument baseline, SaveDocument target)
    {
        InitializeComponent();

        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(1080, workArea.Width * 0.9);
        Height = Math.Min(680, workArea.Height * 0.9);

        TxtFileA.Text = Describe(baseline);
        TxtFileB.Text = Describe(target);

        var entries = SaveDiff.Compare(baseline, target);
        foreach (var entry in entries) _rows.Add(entry);

        DiffList.ItemsSource = _rows;

        int added = entries.Count(e => e.Kind == DiffKind.Added);
        int removed = entries.Count(e => e.Kind == DiffKind.Removed);
        int modified = entries.Count(e => e.Kind == DiffKind.Modified);

        TxtSummary.Text = SaveDiff.Summarize(entries);
        TxtAdded.Text = $"新增 {added}";
        TxtRemoved.Text = $"删除 {removed}";
        TxtModified.Text = $"修改 {modified}";

        TxtHint.Text = entries.Count == 0
            ? "两份存档没有任何字段差异。"
            : $"共 {entries.Count} 处差异。路径按「根/子/孙」展示，点击列头可排序。";
    }

    private static string Describe(SaveDocument document)
    {
        string name = document.FilePath is null
            ? "(未保存)"
            : Path.GetFileName(document.FilePath);

        return $"{name}  ·  {document.FormatName}";
    }
}
