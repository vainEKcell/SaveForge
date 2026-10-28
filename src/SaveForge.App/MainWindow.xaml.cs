using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using SaveForge.App.ViewModels;
using SaveForge.Core;

namespace SaveForge.App;

/// <summary>
/// 主窗口：打开存档 → 浏览树 → 编辑字段 → 保存（自动备份）。
/// </summary>
public partial class MainWindow : Window
{
    private readonly SaveFileService _service = SaveFileService.Default;
    private readonly ObservableCollection<FieldNodeVm> _nodes = new();

    /// <summary>文件选择器过滤器，打开与对比共用。</summary>
    private const string SaveFileFilter =
        "存档文件|*.sav;*.save;*.json;*.dat;*.xml;*.plist;*.nbt;*.ini;*.cfg;*.conf;*.properties;*.txt;*.gz;*.br;*.rxdata;*.rvdata;*.rvdata2;*.rpgsave;*.rmmzsave;*.msgpack;*.mpack;*.cbor|" +
        "全部支持的格式|*.sav;*.save;*.json;*.dat;*.xml;*.plist;*.nbt;*.ini;*.cfg;*.conf;*.properties;*.txt;*.gz;*.br;*.rxdata;*.rvdata;*.rvdata2;*.rpgsave;*.rmmzsave;*.msgpack;*.mpack;*.cbor;*.gvas;*.bin;*.bak|" +
        "所有文件|*.*";

    private SaveDocument? _document;
    private FieldNodeVm? _selected;
    private GameDatabase? _database;
    private bool _suppressValueChange;
    private bool _suppressSearchChange;

    public MainWindow()
    {
        InitializeComponent();
        FieldTree.ItemsSource = _nodes;

        // 小屏幕自适应：窗口不超过可用工作区
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(1200, workArea.Width * 0.94);
        Height = Math.Min(760, workArea.Height * 0.92);
    }

    // ====================================================================
    // 打开
    // ====================================================================

    private void BtnOpen_Click(object sender, RoutedEventArgs e) => OpenFile();

    private void OpenFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择存档文件",
            Filter = SaveFileFilter,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) == true)
            LoadFile(dialog.FileName);
    }

    /// <summary>加载存档文件。供打开对话框、拖放与命令行启动共用。</summary>
    public void LoadFile(string path)
    {
        try
        {
            _document = _service.Open(path);
            _selected = null;

            // RPG Maker 存档：尝试从游戏目录自动载入数据库，把物品 ID 显示成名称
            TryAttachGameDatabase(path);

            // 换文件时清空搜索条件，避免旧关键词把新文件的字段过滤掉
            if (TxtSearch.Text.Length > 0)
            {
                _suppressSearchChange = true;
                TxtSearch.Clear();
                _suppressSearchChange = false;
            }

            RebuildTree();
            UpdateHeader();
            UpdateStatus();
            ShowEmptyDetail();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"打开失败：\n\n{ex.Message}", "SaveForge",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RebuildTree()
    {
        _nodes.Clear();
        if (_document is null) return;

        string keyword = TxtSearch.Text.Trim();
        string? filter = keyword.Length == 0 ? null : keyword;

        var roots = _document.Roots;

        // 单一容器根（JSON 常见）时扁平化展示其子节点，避免多一层无意义的 "$"
        IEnumerable<SaveNode> topLevel = roots.Count == 1 && roots[0].IsContainer
            ? roots[0].Children
            : roots;

        var list = topLevel.ToList();

        foreach (var node in list)
        {
            if (filter is not null && !FieldNodeVm.Matches(node, filter)) continue;
            _nodes.Add(new FieldNodeVm(node, 0, filter));
        }

        if (filter is not null)
        {
            int matched = FieldNodeVm.CountMatches(list, filter);
            TxtStatus.Text = matched == 0
                ? $"筛选「{keyword}」：无匹配字段"
                : $"筛选「{keyword}」：匹配 {matched} 个字段";
        }
    }

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressSearchChange || _document is null || TxtSearch is null) return;

        RebuildTree();

        if (TxtSearch.Text.Trim().Length == 0) UpdateStatus();
    }

    private void BtnLoadDatabase_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null) return;

        var dialog = new OpenFileDialog
        {
            Title = "选择 RPG Maker 数据表",
            Filter = "RPG Maker 数据表|Items.json;Weapons.json;Armors.json|JSON 文件|*.json|所有文件|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            _database = GameDatabase.LoadFile(dialog.FileName);
            int mapped = _database.ApplyTo(_document);

            RebuildTree();

            TxtStatus.Text = mapped > 0
                ? $"已载入 {Path.GetFileName(dialog.FileName)}（{_database.Summary}），识别 {mapped} 个库存条目"
                : $"已载入数据库（{_database.Summary}），但当前存档里没有匹配的条目";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"载入数据库失败：\n\n{ex.Message}", "SaveForge",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// RPG Maker 存档打开后，尝试从游戏目录自动找 data/Items.json 等数据表。
    /// 找不到时静默跳过，用户可以手动点「物品库」载入。
    /// </summary>
    private void TryAttachGameDatabase(string path)
    {
        _database = null;

        if (_document is null || !IsRpgMakerSave(path, _document.FormatId)) return;

        _database = GameDatabase.TryAutoLoad(path);
        if (_database is null) return;

        int mapped = _database.ApplyTo(_document);

        if (mapped > 0)
            TxtStatus.Text = $"已自动载入游戏数据库（{_database.Summary}），识别 {mapped} 个库存条目";
    }

    private static bool IsRpgMakerSave(string path, string formatId)
    {
        return path.EndsWith(".rmmzsave", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".rpgsave", StringComparison.OrdinalIgnoreCase)
            || formatId is "rpgmaker" or "rpgmaker-mv";
    }

    private void BtnAbout_Click(object sender, RoutedEventArgs e)
    {
        new AboutWindow { Owner = this }.ShowDialog();
    }

    // ====================================================================
    // 快捷键
    // ====================================================================

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.O:
                    OpenFile();
                    e.Handled = true;
                    return;

                case Key.S:
                    SaveCurrent();
                    e.Handled = true;
                    return;

                case Key.F:
                    TxtSearch.Focus();
                    TxtSearch.SelectAll();
                    e.Handled = true;
                    return;
            }
        }
        else if (e.Key == Key.Escape && TxtSearch.IsKeyboardFocusWithin)
        {
            TxtSearch.Clear();
            e.Handled = true;
            return;
        }
        else if (e.Key == Key.F5 && _document?.FilePath is not null)
        {
            ReloadCurrent();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private void ReloadCurrent()
    {
        if (_document?.FilePath is null) return;

        if (_document.HasChanges)
        {
            var result = MessageBox.Show(this,
                "当前有未保存的修改，重新加载会丢弃这些修改。继续吗？",
                "SaveForge", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

            if (result != MessageBoxResult.OK) return;
        }

        string path = _document.FilePath;
        LoadFile(path);
    }

    // ====================================================================
    // 保存 / 备份
    // ====================================================================

    private void BtnSave_Click(object sender, RoutedEventArgs e) => SaveCurrent();

    private void SaveCurrent()
    {
        if (_document is null) return;

        if (!_document.HasChanges)
        {
            MessageBox.Show(this, "没有需要保存的修改。", "SaveForge",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            _service.Save(_document);

            foreach (var node in _nodes) node.RefreshRecursive();
            UpdateHeader();
            UpdateStatus();
            UpdateDetailBadge();

            TxtStatus.Text = $"已保存：{_document.FilePath}（原文件已备份为 .bak）";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"保存失败：\n\n{ex.Message}", "SaveForge",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnBackup_Click(object sender, RoutedEventArgs e)
    {
        if (_document?.FilePath is null || !File.Exists(_document.FilePath)) return;

        try
        {
            string backupPath = _service.Backup(_document.FilePath);
            TxtStatus.Text = $"已生成备份：{backupPath}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"备份失败：\n\n{ex.Message}", "SaveForge",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnDiff_Click(object sender, RoutedEventArgs e)
    {
        if (_document?.FilePath is null) return;

        var dialog = new OpenFileDialog
        {
            Title = "选择要对比的存档",
            Filter = SaveFileFilter,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var baseline = _document;
            var target = _service.Open(dialog.FileName);

            // 两侧格式差异大时提示但不阻止
            if (baseline.FormatId != target.FormatId)
            {
                var result = MessageBox.Show(this,
                    $"两份存档格式不同（{baseline.FormatName} vs {target.FormatName}），\n" +
                    "字段对齐可能不准确。仍要继续对比吗？",
                    "SaveForge",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning);

                if (result != MessageBoxResult.OK) return;
            }

            var diffWindow = new DiffWindow(baseline, target)
            {
                Owner = this,
            };
            diffWindow.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"对比失败：\n\n{ex.Message}", "SaveForge",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnBatch_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null) return;

        // 选中容器节点时，把批量修改限制在该子树内
        SaveNode? scope = _selected?.IsContainer == true ? _selected.Model : null;
        var window = new BatchWindow(_document, scope) { Owner = this };

        if (window.ShowDialog() != true || window.AppliedCount <= 0) return;

        foreach (var node in _nodes) node.RefreshRecursive();
        UpdateStatus();

        TxtStatus.Text = $"批量修改了 {window.AppliedCount} 个字段（尚未保存）";
        BtnSave.IsEnabled = true;
    }

    private void BtnExportJson_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null) return;

        var dialog = new SaveFileDialog
        {
            Title = "导出为 JSON",
            Filter = "JSON 文件|*.json|所有文件|*.*",
            FileName = Path.GetFileNameWithoutExtension(_document.FilePath ?? "save") + ".export.json",
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            string json = SaveDocumentJson.Export(_document);
            File.WriteAllText(dialog.FileName, json);
            TxtStatus.Text = $"已导出 JSON：{dialog.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"导出失败：\n\n{ex.Message}", "SaveForge",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnImportJson_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null) return;

        var dialog = new OpenFileDialog
        {
            Title = "从 JSON 导入",
            Filter = "JSON 文件|*.json|所有文件|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            string json = File.ReadAllText(dialog.FileName);
            int applied = SaveDocumentJson.Apply(_document, json);

            foreach (var node in _nodes) node.RefreshRecursive();
            UpdateStatus();

            if (applied > 0)
            {
                TxtStatus.Text = $"已导入 {applied} 项修改（尚未保存，点「保存修改」写入存档）";
                BtnSave.IsEnabled = true;
            }
            else
            {
                TxtStatus.Text = "JSON 与当前存档没有可应用的差异";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"导入失败：\n\n{ex.Message}", "SaveForge",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ====================================================================
    // 树交互
    // ====================================================================

    private void FieldTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _selected = e.NewValue as FieldNodeVm;

        if (_selected is null)
        {
            ShowEmptyDetail();
            return;
        }

        DetailScroll.Visibility = Visibility.Visible;
        EmptyHint.Visibility = Visibility.Collapsed;

        TxtFieldName.Text = _selected.Name;
        TxtLocator.Text = string.IsNullOrEmpty(_selected.Locator) ? "—" : _selected.Locator;
        UpdateDetailBadge();

        if (_selected.IsContainer)
        {
            TxtFieldKind.Text = $"{_selected.KindLabel} · {_selected.Model.Children.Count} 项";
            ValueSection.Visibility = Visibility.Collapsed;
            ContainerHint.Visibility = Visibility.Visible;
        }
        else
        {
            TxtFieldKind.Text = _selected.KindLabel;
            ValueSection.Visibility = Visibility.Visible;
            ContainerHint.Visibility = Visibility.Collapsed;

            _suppressValueChange = true;
            TxtValue.Text = _selected.ValueText;
            TxtValue.IsReadOnly = !_selected.IsEditable;
            TxtValue.ClearValue(Control.BorderBrushProperty);
            _suppressValueChange = false;

            TxtValueHint.Text = BuildValueHint(_selected.Model);
        }
    }

    private void TxtValue_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressValueChange || _selected is null || _selected.IsContainer) return;
        if (!_selected.IsEditable) return;

        if (TryParseValue(TxtValue.Text, _selected.Model.Kind, out object? value))
        {
            _selected.Model.SetValue(value);
            _selected.Refresh();

            TxtValue.ClearValue(Control.BorderBrushProperty);
            UpdateDetailBadge();
            UpdateStatus();
        }
        else
        {
            TxtValue.BorderBrush = (Brush)FindResource("Brush.Danger");
        }
    }

    // ====================================================================
    // 拖放
    // ====================================================================

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            LoadFile(files[0]);
    }

    // ====================================================================
    // 界面状态
    // ====================================================================

    private void UpdateHeader()
    {
        if (_document is null)
        {
            Title = "SaveForge · 存档修改器";
            TxtFileName.Text = "未打开文件";
            TxtFormat.Text = "—";
            BtnSave.IsEnabled = false;
            BtnBackup.IsEnabled = false;
            BtnDiff.IsEnabled = false;
            BtnBatch.IsEnabled = false;
            BtnExportJson.IsEnabled = false;
            BtnImportJson.IsEnabled = false;
            BtnLoadDatabase.IsEnabled = false;
            return;
        }

        string fileName = Path.GetFileName(_document.FilePath ?? "(未命名)");
        Title = $"SaveForge · 存档修改器 — {fileName}";
        TxtFileName.Text = fileName;
        TxtFormat.Text = _document.FormatName;

        bool canWrite = _service.Registry.Find(_document.FormatId)?.CanWrite == true && !_document.IsReadOnly;
        bool hasFile = _document.FilePath is not null && File.Exists(_document.FilePath);

        BtnSave.IsEnabled = canWrite;
        BtnBackup.IsEnabled = hasFile;
        BtnDiff.IsEnabled = hasFile;
        BtnBatch.IsEnabled = canWrite;
        BtnExportJson.IsEnabled = true;
        BtnImportJson.IsEnabled = canWrite;
        BtnLoadDatabase.IsEnabled = IsRpgMakerSave(_document.FilePath ?? "", _document.FormatId);
    }

    private void UpdateStatus()
    {
        if (_document is null)
        {
            TxtStatus.Text = "拖入存档文件，或点击「打开存档」";
            TxtModified.Text = string.Empty;
            return;
        }

        string mode = _document.IsReadOnly ? " · 只读预览" : string.Empty;
        string database = _database is not null ? $" · 物品库 {_database.TotalEntries} 项" : string.Empty;

        TxtStatus.Text = $"{_document.FormatName} · {FormatSize(_document.OriginalBytes.Length)}{mode}{database}";

        int modified = _document.ModifiedCount;
        TxtModified.Text = modified > 0 ? $"● 已修改 {modified} 项" : string.Empty;
    }

    private void UpdateDetailBadge()
    {
        bool modified = _selected?.IsModified == true;
        BadgeModified.Visibility = modified ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowEmptyDetail()
    {
        DetailScroll.Visibility = Visibility.Collapsed;
        EmptyHint.Visibility = Visibility.Visible;
    }

    // ====================================================================
    // 辅助
    // ====================================================================

    private static string BuildValueHint(SaveNode node)
    {
        if (!node.IsEditable) return "该字段为只读，仅供浏览。";

        return node.Kind switch
        {
            ValueKind.Integer => "输入整数，如 42",
            ValueKind.Float => "输入小数，如 3.14",
            ValueKind.Boolean => "输入 true 或 false",
            ValueKind.String => "直接输入文本内容",
            ValueKind.Null => "空值，保持 null 即可",
            ValueKind.Binary => "二进制内容不支持直接编辑",
            _ => string.Empty,
        };
    }

    private static bool TryParseValue(string text, ValueKind kind, out object? value)
    {
        value = null;
        string trimmed = text.Trim();

        switch (kind)
        {
            case ValueKind.String:
                value = text;
                return true;

            case ValueKind.Integer:
                if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer))
                {
                    value = integer;
                    return true;
                }
                return false;

            case ValueKind.Float:
                if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                {
                    value = number;
                    return true;
                }
                return false;

            case ValueKind.Boolean:
                if (bool.TryParse(trimmed, out bool flag))
                {
                    value = flag;
                    return true;
                }
                return false;

            case ValueKind.Null:
                value = null;
                return string.IsNullOrWhiteSpace(trimmed) ||
                       trimmed.Equals("null", StringComparison.OrdinalIgnoreCase);

            default:
                return false;
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} 字节",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F2} MB",
    };
}
