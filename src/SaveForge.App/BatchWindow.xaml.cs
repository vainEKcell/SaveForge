using System.Windows;
using SaveForge.Core;

namespace SaveForge.App;

/// <summary>
/// 批量修改窗口：按字段名通配符匹配，统一设置新值。
/// </summary>
public partial class BatchWindow : Window
{
    private readonly SaveDocument _document;

    /// <summary>实际修改的字段数量（应用成功后有效）。</summary>
    public int AppliedCount { get; private set; }

    public BatchWindow(SaveDocument document)
    {
        InitializeComponent();
        _document = document;
    }

    private void Input_Changed(object sender, RoutedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (TxtPattern is null || TxtValue is null || TxtPreview is null) return;

        string pattern = TxtPattern.Text.Trim();
        if (pattern.Length == 0)
        {
            TxtPreview.Text = "输入字段名模式以预览";
            BtnApply.IsEnabled = false;
            return;
        }

        var matches = SaveBatchEdit.Match(_document, pattern);

        if (matches.Count == 0)
        {
            TxtPreview.Text = "没有匹配的可编辑字段";
            BtnApply.IsEnabled = false;
            return;
        }

        // 预览前几个匹配项的当前值
        var samples = matches.Take(3).Select(n => $"{n.Name} = {n.DisplayValue}");
        string sampleText = string.Join("、", samples);
        string more = matches.Count > 3 ? " 等" : "";

        TxtPreview.Text = $"将修改 {matches.Count} 个字段：{sampleText}{more}";

        bool hasValue = TxtValue.Text.Length > 0;
        bool parseable = matches.All(n => SaveBatchEdit.Parse(TxtValue.Text, n.Kind) is not null
                                          || n.Kind == ValueKind.Null);

        BtnApply.IsEnabled = hasValue && parseable;

        if (hasValue && !parseable)
            TxtPreview.Text += "\n提示：新值与部分字段类型不匹配（如文本填到数值字段），这些字段会被跳过。";
    }

    private void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        var matches = SaveBatchEdit.Match(_document, TxtPattern.Text.Trim());

        AppliedCount = SaveBatchEdit.Apply(matches, TxtValue.Text);

        DialogResult = AppliedCount > 0;

        if (AppliedCount == 0)
        {
            MessageBox.Show(this, "没有字段被修改，请检查字段名与新值的类型。",
                "批量修改", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
