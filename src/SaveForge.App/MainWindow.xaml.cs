using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using SaveForge.App.ViewModels;
using SaveForge.Core;

namespace SaveForge.App;

/// <summary>
/// 主窗口：打开存档 → 浏览/编辑 → 保存（自动备份）。
///
/// 左侧顶部是按存档内容动态生成的菜单：
///   概览 / 全部字段 / 物品 / 武器 / 防具 …
/// 没有对应数据的菜单项不会出现。
/// </summary>
public partial class MainWindow : Window
{
    private readonly SaveFileService _service = SaveFileService.Default;
    private readonly ObservableCollection<FieldNodeVm> _nodes = new();
    private readonly ObservableCollection<InventoryRowVm> _inventoryRows = new();

    /// <summary>文件选择器过滤器，打开与对比共用。</summary>
    private const string SaveFileFilter =
        "存档文件|*.sav;*.save;*.json;*.dat;*.xml;*.plist;*.nbt;*.ini;*.cfg;*.conf;*.properties;*.txt;*.gz;*.br;*.rxdata;*.rvdata;*.rvdata2;*.rpgsave;*.rmmzsave;*.msgpack;*.mpack;*.cbor|" +
        "全部支持的格式|*.sav;*.save;*.json;*.dat;*.xml;*.plist;*.nbt;*.ini;*.cfg;*.conf;*.properties;*.txt;*.gz;*.br;*.rxdata;*.rvdata;*.rvdata2;*.rpgsave;*.rmmzsave;*.msgpack;*.mpack;*.cbor;*.gvas;*.bin;*.bak|" +
        "所有文件|*.*";

    private SaveDocument? _document;
    private FieldNodeVm? _selected;
    private GameDatabase? _database;
    private int _databaseMapped;

    private ICollectionView? _inventoryView;
    private InventoryRowVm? _selectedItem;
    private InventoryCategory _inventoryCategory = InventoryCategory.Items;
    private string _navKey = "fields";
    private readonly Dictionary<string, Button> _navButtons = new();

    private bool _suppressValueChange;
    private bool _suppressSearchChange;
    private bool _suppressOverviewValue;
    private bool _suppressItemQty;

    public MainWindow()
    {
        InitializeComponent();

        FieldTree.ItemsSource = _nodes;
        InventoryGrid.ItemsSource = _inventoryRows;

        _inventoryView = CollectionViewSource.GetDefaultView(_inventoryRows);
        _inventoryView.Filter = FilterInventory;
        InventoryGrid.ItemsSource = _inventoryView;

        AppSettings.Load();
        UpdateHeader();

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
            _selectedItem = null;
            _databaseMapped = 0;

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
            BuildInventory();
            BuildOverview();
            BuildNav();

            SwitchTo(_document is not null && HasOverviewData() ? "overview" : "fields");

            UpdateHeader();
            UpdateStatus();

            if (_database is not null)
            {
                TxtStatus.Text = _databaseMapped > 0
                    ? $"已自动载入游戏数据库（{_database.Summary}），识别 {_databaseMapped} 个库存条目"
                    : $"已自动载入游戏数据库（{_database.Summary}），但当前存档里没有匹配的条目";
            }
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

    // ====================================================================
    // 游戏数据表
    // ====================================================================

    private void BtnGameRoot_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择游戏所在的文件夹",
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) != true) return;

        string root = dialog.FolderName;
        var candidates = GameRootLocator.Locate(root);

        if (candidates.Count == 0)
        {
            MessageBox.Show(this,
                "在这个文件夹里没有找到 RPG Maker 的数据表。\n\n" +
                "请选择游戏根目录（其中应包含 data\\Items.json，\n" +
                "网页版布局则是 www\\data\\Items.json）。",
                "SaveForge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var pick = GameRootLocator.Prefer(candidates, _document?.FilePath);
        if (pick is null) return;

        AppSettings.GameRoot = root;
        AppSettings.Save();

        ApplyDatabase(pick.Database);

        string found = candidates.Count > 1 ? $"（共找到 {candidates.Count} 处数据表，已使用 {pick.DataDirectory}）" : string.Empty;
        TxtStatus.Text = _document is null
            ? $"已记住游戏目录：{pick.DataDirectory}。打开该游戏的存档后会自动套用。"
            : _databaseMapped > 0
                ? $"已从游戏目录载入（{pick.Database.Summary}），识别 {_databaseMapped} 个库存条目 {found}"
                : $"已载入数据表（{pick.Database.Summary}），但当前存档里没有匹配的条目 {found}";
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
            ApplyDatabase(GameDatabase.LoadFile(dialog.FileName));

            TxtStatus.Text = _databaseMapped > 0
                ? $"已载入 {Path.GetFileName(dialog.FileName)}（{_database!.Summary}），识别 {_databaseMapped} 个库存条目"
                : $"已载入数据表（{_database!.Summary}），但当前存档里没有匹配的条目";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"载入数据表失败：\n\n{ex.Message}", "SaveForge",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>套用一份游戏数据库：写别名、重建树/库存/概览/菜单。</summary>
    private void ApplyDatabase(GameDatabase database)
    {
        _database = database;
        _databaseMapped = _document is null ? 0 : database.ApplyTo(_document);

        if (_document is null) return;

        RebuildTree();
        BuildInventory();
        BuildOverview();
        BuildNav();
        SwitchTo(_navKey);
        UpdateStatus();
        BtnSave.IsEnabled = CanWriteDocument;
    }

    /// <summary>
    /// RPG Maker 存档打开后，尝试自动找数据表：
    /// 优先用用户上次选择的游戏根目录（且当前存档确实在其目录下），
    /// 否则从存档所在目录向上探测。
    /// </summary>
    private void TryAttachGameDatabase(string path)
    {
        _database = null;
        _databaseMapped = 0;

        if (_document is null || !IsRpgMakerSave(path, _document.FormatId)) return;

        string? savedRoot = AppSettings.GameRoot;

        if (savedRoot is not null && IsUnder(path, savedRoot))
        {
            var candidates = GameRootLocator.Locate(savedRoot);
            var pick = GameRootLocator.Prefer(candidates, path);

            if (pick is not null) _database = pick.Database;
        }

        _database ??= GameDatabase.TryAutoLoad(path, _document.FormatId);

        if (_database is not null)
            _databaseMapped = _database.ApplyTo(_document);
    }

    private static bool IsRpgMakerSave(string path, string formatId)
    {
        return path.EndsWith(".rmmzsave", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".rpgsave", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".rxdata", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".rvdata", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".rvdata2", StringComparison.OrdinalIgnoreCase)
            || formatId is "rpgmaker" or "rpgmaker-mv";
    }

    private static bool IsUnder(string filePath, string directory)
    {
        try
        {
            string fullDirectory = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            return Path.GetFullPath(filePath)
                .StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void BtnAbout_Click(object sender, RoutedEventArgs e)
    {
        new AboutWindow { Owner = this }.ShowDialog();
    }

    // ====================================================================
    // 顶部导航（按存档内容动态生成）
    // ====================================================================

    private void BuildNav()
    {
        NavBar.Children.Clear();
        _navButtons.Clear();

        if (_document is null) return;

        var items = new List<(string Key, string Label, string? Tip)>();

        if (HasOverviewData())
            items.Add(("overview", "概览", "常用数值与游戏数据表"));

        items.Add(("fields", "全部字段", "完整存档树，包含所有引擎的原始字段"));

        foreach (InventoryCategory category in Enum.GetValues<InventoryCategory>())
        {
            int count = _inventoryRows.Count(r => r.Entry.Category == category);
            if (count == 0) continue;

            items.Add((KeyOf(category), $"{InventoryScanner.DisplayName(category)} {count}",
                $"以表格方式编辑{InventoryScanner.DisplayName(category)}数量"));
        }

        foreach (var (key, label, tip) in items)
        {
            var button = new Button
            {
                Content = label,
                Style = (Style)FindResource("NavButton"),
                Tag = key,
            };

            if (tip is not null) button.ToolTip = tip;
            button.Click += (_, _) => SwitchTo((string)button.Tag);

            _navButtons[key] = button;
            NavBar.Children.Add(button);
        }
    }

    private void SwitchTo(string key)
    {
        if (_document is null)
        {
            OverviewPage.Visibility = Visibility.Collapsed;
            FieldsPage.Visibility = Visibility.Visible;
            InventoryPage.Visibility = Visibility.Collapsed;
            _navKey = "fields";
            UpdateRightPanel();
            return;
        }

        _navKey = key;

        OverviewPage.Visibility = key == "overview" ? Visibility.Visible : Visibility.Collapsed;
        FieldsPage.Visibility = key == "fields" ? Visibility.Visible : Visibility.Collapsed;
        InventoryPage.Visibility = key is "items" or "weapons" or "armors"
            ? Visibility.Visible
            : Visibility.Collapsed;

        foreach (var (navKey, button) in _navButtons)
        {
            bool active = navKey == key;
            button.Background = active ? (Brush)FindResource("Brush.Subtle") : Brushes.Transparent;
            button.Foreground = active ? (Brush)FindResource("Brush.Accent") : (Brush)FindResource("Brush.TextMuted");
            button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }

        if (key is "items" or "weapons" or "armors")
        {
            _inventoryCategory = key switch
            {
                "weapons" => InventoryCategory.Weapons,
                "armors" => InventoryCategory.Armors,
                _ => InventoryCategory.Items,
            };

            TxtInventoryTitle.Text = InventoryScanner.DisplayName(_inventoryCategory);

            // 从字段树改过数量后，回到库存页要看到最新值
            foreach (var row in _inventoryRows) row.Refresh();

            if (_selectedItem is not null && _selectedItem.Entry.Category != _inventoryCategory)
                _selectedItem = null;

            _inventoryView?.Refresh();
            InventoryGrid.SelectedItem = _selectedItem;
            UpdateInventoryCount();
        }
        else if (key == "overview")
        {
            BuildOverview();
        }

        UpdateRightPanel();
    }

    private static string KeyOf(InventoryCategory category) => category switch
    {
        InventoryCategory.Weapons => "weapons",
        InventoryCategory.Armors => "armors",
        _ => "items",
    };

    private void UpdateRightPanel()
    {
        bool hasDocument = _document is not null;

        EmptyHint.Visibility = hasDocument ? Visibility.Collapsed : Visibility.Visible;

        if (!hasDocument)
        {
            DetailScroll.Visibility = Visibility.Collapsed;
            InventoryDetail.Visibility = Visibility.Collapsed;
            TabHint.Visibility = Visibility.Collapsed;
            return;
        }

        bool showFieldDetail = _navKey == "fields" && _selected is not null;
        bool showItemDetail = _navKey is "items" or "weapons" or "armors"
                              && _selectedItem is not null
                              && _selectedItem.Entry.Category == _inventoryCategory;

        DetailScroll.Visibility = showFieldDetail ? Visibility.Visible : Visibility.Collapsed;
        InventoryDetail.Visibility = showItemDetail ? Visibility.Visible : Visibility.Collapsed;
        TabHint.Visibility = showFieldDetail || showItemDetail ? Visibility.Collapsed : Visibility.Visible;

        if (TabHint.Visibility != Visibility.Visible) return;

        switch (_navKey)
        {
            case "overview":
                TxtTabHintTitle.Text = "常用数值都在这里";
                TxtTabHintBody.Text =
                    "金钱、步数、存档次数可以直接在卡片里输入新值。\n\n" +
                    "改了以后点上方「保存修改」写入存档，原文件会自动备份为 .bak。";
                break;

            case "items":
            case "weapons":
            case "armors":
                TxtTabHintTitle.Text = "选择一条库存";
                TxtTabHintBody.Text =
                    "双击左侧「数量」单元格可以直接输入新数值；\n" +
                    "选中某一行后，这里会出现大号输入框和快捷按钮。\n\n" +
                    "名称来自游戏自带的物品表。没有名称时，\n" +
                    "请用工具栏「游戏目录」选择游戏文件夹。";
                break;

            default:
                TxtTabHintTitle.Text = "选择一个字段";
                TxtTabHintBody.Text =
                    "左侧列出存档的全部字段。\n" +
                    "带中文名的行是常用属性（金钱、等级、物品…），\n" +
                    "右侧小字是它在文件里的原始键名。\n\n" +
                    "点一行即可在右侧修改它的值。";
                break;
        }
    }

    // ====================================================================
    // 库存
    // ====================================================================

    private void BuildInventory()
    {
        _inventoryRows.Clear();
        _selectedItem = null;

        if (_document is null) return;

        var entries = InventoryScanner.Scan(_document, _database);

        foreach (var entry in entries)
        {
            var row = new InventoryRowVm(entry);
            row.ValueChanged += () => OnInventoryValueChanged(row);
            _inventoryRows.Add(row);
        }

        _inventoryView?.Refresh();
        UpdateInventoryCount();
    }

    private bool FilterInventory(object item)
    {
        if (item is not InventoryRowVm row) return false;
        if (row.Entry.Category != _inventoryCategory) return false;

        string keyword = TxtItemFilter?.Text.Trim() ?? string.Empty;
        if (keyword.Length == 0) return true;

        return row.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || row.Id.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private void TxtItemFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        _inventoryView?.Refresh();
        UpdateInventoryCount();
    }

    private void UpdateInventoryCount()
    {
        if (TxtInventoryCount is null) return;

        int total = _inventoryRows.Count(r => r.Entry.Category == _inventoryCategory);
        int shown = _inventoryView?.Cast<object>().Count() ?? 0;

        string category = InventoryScanner.DisplayName(_inventoryCategory);
        TxtInventoryCount.Text = total == 0
            ? $"存档里没有{category}"
            : shown == total
                ? $"{total} 种"
                : $"显示 {shown} / {total} 种";
    }

    private void InventoryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedItem = InventoryGrid.SelectedItem as InventoryRowVm;

        if (_selectedItem is not null && _navKey is "items" or "weapons" or "armors")
        {
            _suppressItemQty = true;

            TxtItemName.Text = _selectedItem.Name;
            TxtItemMeta.Text = $"{_selectedItem.TypeAndId} · 存档键 {_selectedItem.Entry.Node.Name}";
            TxtItemQty.Text = _selectedItem.QuantityText;
            TxtItemQty.IsReadOnly = !(_document is not null && CanWriteDocument && _selectedItem.Entry.Node.IsEditable);
            TxtItemLocator.Text = string.IsNullOrEmpty(_selectedItem.Locator) ? "—" : _selectedItem.Locator;

            TxtItemQty.ClearValue(Control.BorderBrushProperty);
            _suppressItemQty = false;

            UpdateItemBadge();
        }

        UpdateRightPanel();
    }

    private void TxtItemQty_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressItemQty || _selectedItem is null) return;

        string text = TxtItemQty.Text.Trim();

        if (!long.TryParse(text, out long quantity) || quantity < 0 || quantity > 9_999_999)
        {
            TxtItemQty.BorderBrush = (Brush)FindResource("Brush.Danger");
            return;
        }

        TxtItemQty.ClearValue(Control.BorderBrushProperty);

        if (quantity == _selectedItem.Entry.Quantity)
        {
            UpdateItemBadge();
            return;
        }

        _selectedItem.Entry.Node.SetValue(quantity);
        _selectedItem.Refresh();
        UpdateItemBadge();
        OnInventoryValueChanged(_selectedItem);
    }

    private void BtnItemQty99_Click(object sender, RoutedEventArgs e) => SetSelectedItemQuantity(99);

    private void BtnItemQty999_Click(object sender, RoutedEventArgs e) => SetSelectedItemQuantity(999);

    private void SetSelectedItemQuantity(long quantity)
    {
        if (_selectedItem is null) return;

        _selectedItem.QuantityText = quantity.ToString(CultureInfo.InvariantCulture);
        _selectedItem.Refresh();

        _suppressItemQty = true;
        TxtItemQty.Text = _selectedItem.QuantityText;
        _suppressItemQty = false;

        OnInventoryValueChanged(_selectedItem);
    }

    private void OnInventoryValueChanged(InventoryRowVm row)
    {
        if (ReferenceEquals(row, _selectedItem))
        {
            _suppressItemQty = true;
            TxtItemQty.Text = row.QuantityText;
            _suppressItemQty = false;

            UpdateItemBadge();
        }

        BtnSave.IsEnabled = CanWriteDocument;
        UpdateStatus();
    }

    private void UpdateItemBadge()
    {
        BadgeItemModified.Visibility = _selectedItem?.IsModified == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void BtnApplyBulk_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null) return;

        if (!long.TryParse(TxtBulkValue.Text.Trim(), out long quantity) || quantity < 0 || quantity > 9_999_999)
        {
            MessageBox.Show(this, "请输入 0 – 9,999,999 之间的整数。", "SaveForge",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var targets = _inventoryView?.Cast<InventoryRowVm>().ToList() ?? new List<InventoryRowVm>();
        if (targets.Count == 0)
        {
            MessageBox.Show(this, "当前筛选条件下没有可修改的条目。", "SaveForge",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        foreach (var row in targets)
            row.QuantityText = quantity.ToString(CultureInfo.InvariantCulture);

        _inventoryView?.Refresh();
        if (_selectedItem is not null) _selectedItem.Refresh();

        BtnSave.IsEnabled = CanWriteDocument;
        UpdateStatus();

        TxtStatus.Text = $"已把 {targets.Count} 条{InventoryScanner.DisplayName(_inventoryCategory)}的数量改为 {quantity}（尚未保存）";
    }

    // ====================================================================
    // 概览
    // ====================================================================

    private bool HasOverviewData()
    {
        if (_document is null) return false;

        var party = FindContainerByNormalizedNames("party");

        if (party is not null &&
            (FindChildLoose(party, "gold") is not null ||
             FindChildLoose(party, "steps") is not null ||
             FindChildLoose(party, "items") is not null ||
             FindChildLoose(party, "weapons") is not null ||
             FindChildLoose(party, "armors") is not null))
        {
            return true;
        }

        var system = FindContainerByNormalizedNames("system");
        return FindChildLoose(system, "savecount") is not null;
    }

    /// <summary>
    /// 构建概览卡片。只针对有稳定字段约定的 RPG Maker 存档；
    /// 其他引擎没有统一结构，硬猜反而误导用户。
    /// </summary>
    private void BuildOverview()
    {
        if (OverviewItems is null) return;

        OverviewItems.Children.Clear();

        if (_document is null || !IsRpgMakerSave(_document.FilePath ?? "", _document.FormatId))
        {
            TxtDatabaseInfo.Text = "当前存档不是 RPG Maker 存档，没有可套用的物品表。";
            return;
        }

        UpdateDatabaseInfo();

        var party = FindContainerByNormalizedNames("party");
        var system = FindContainerByNormalizedNames("system");

        _suppressOverviewValue = true;

        var gold = FindChildLoose(party, "gold");
        if (gold is not null) OverviewItems.Children.Add(BuildNumberCard("💰", "金钱", gold));

        var steps = FindChildLoose(party, "steps");
        if (steps is not null) OverviewItems.Children.Add(BuildNumberCard("👣", "步数", steps));

        var saveCount = FindChildLoose(system, "savecount");
        if (saveCount is not null) OverviewItems.Children.Add(BuildNumberCard("💾", "存档次数", saveCount));

        int itemCount = _inventoryRows.Count(r => r.Entry.Category == InventoryCategory.Items);
        if (itemCount > 0) OverviewItems.Children.Add(BuildCountCard("🎒", "物品", $"{itemCount} 种", "items", "打开物品表格"));

        int weaponCount = _inventoryRows.Count(r => r.Entry.Category == InventoryCategory.Weapons);
        if (weaponCount > 0) OverviewItems.Children.Add(BuildCountCard("⚔", "武器", $"{weaponCount} 种", "weapons", "打开武器表格"));

        int armorCount = _inventoryRows.Count(r => r.Entry.Category == InventoryCategory.Armors);
        if (armorCount > 0) OverviewItems.Children.Add(BuildCountCard("🛡", "防具", $"{armorCount} 种", "armors", "打开防具表格"));

        var actors = FindChildLoose(party, "actors");
        if (actors is not null && actors.Children.Count > 0)
            OverviewItems.Children.Add(BuildCountCard("👥", "队伍", $"{actors.Children.Count} 人", "party", "在字段里查看队伍"));

        _suppressOverviewValue = false;
    }

    private Border BuildNumberCard(string icon, string label, SaveNode node)
    {
        var border = new Border
        {
            Width = 168,
            Background = (Brush)FindResource("Brush.Subtle"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 8, 8),
        };

        var stack = new StackPanel();

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock
        {
            Text = icon,
            FontSize = 14,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        head.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = (Brush)FindResource("Brush.TextMuted"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        stack.Children.Add(head);

        var input = new TextBox
        {
            Text = node.DisplayValue,
            Style = (Style)FindResource("Input"),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("Brush.Accent"),
            Padding = new Thickness(7, 4, 7, 4),
            Margin = new Thickness(0, 6, 0, 0),
            Tag = node,
            IsReadOnly = !(CanWriteDocument && node.IsEditable),
        };

        input.TextChanged += OverviewValue_TextChanged;

        stack.Children.Add(input);
        border.Child = stack;

        return border;
    }

    private void OverviewValue_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressOverviewValue || sender is not TextBox input || input.Tag is not SaveNode node) return;

        if (!long.TryParse(input.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
        {
            input.BorderBrush = (Brush)FindResource("Brush.Danger");
            return;
        }

        input.ClearValue(Control.BorderBrushProperty);

        if (node.Value is long current && current == value) return;

        node.SetValue(value);
        RefreshInventoryNode(node);

        BtnSave.IsEnabled = CanWriteDocument;
        UpdateStatus();
    }

    private Border BuildCountCard(string icon, string label, string value, string navKey, string tip)
    {
        var border = new Border
        {
            Width = 168,
            Background = (Brush)FindResource("Brush.Subtle"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 8, 8),
            Cursor = Cursors.Hand,
            ToolTip = tip,
        };

        var stack = new StackPanel();

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock
        {
            Text = icon,
            FontSize = 14,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        head.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = (Brush)FindResource("Brush.TextMuted"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        stack.Children.Add(head);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
        row.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("Brush.Accent"),
        });
        row.Children.Add(new TextBlock
        {
            Text = "  ›",
            FontSize = 14,
            Foreground = (Brush)FindResource("Brush.TextMuted"),
            VerticalAlignment = VerticalAlignment.Bottom,
        });
        stack.Children.Add(row);

        border.Child = stack;

        border.MouseLeftButtonUp += (_, _) =>
        {
            if (navKey == "party")
            {
                SwitchTo("fields");
                TxtSearch.Text = "actor";
            }
            else
            {
                SwitchTo(navKey);
            }
        };

        return border;
    }

    private void UpdateDatabaseInfo()
    {
        if (_database is null)
        {
            TxtDatabaseInfo.Text = "未载入：物品只会显示 ID。点下面的按钮选择游戏文件夹，程序会自己找 data\\Items.json。";
            return;
        }

        string source = _database.DataDirectory is null
            ? "手动载入的数据表"
            : _database.DataDirectory;

        TxtDatabaseInfo.Text = $"已载入：{_database.Summary}\n来源：{source}";
    }

    private SaveNode? FindContainerByNormalizedNames(params string[] normalizedNames)
    {
        if (_document is null) return null;

        foreach (var root in _document.Roots)
        {
            foreach (var node in root.Descendants())
            {
                if (!node.IsContainer) continue;

                string name = GameDatabase.NormalizeFieldName(node.Name);
                if (normalizedNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
                    return node;
            }
        }

        return null;
    }

    private static SaveNode? FindChildLoose(SaveNode? parent, string canonicalName)
    {
        if (parent is null) return null;

        string wanted = Loose(canonicalName);

        return parent.Children.FirstOrDefault(c => Loose(c.Name) == wanted);
    }

    private static string Loose(string name) =>
        name.TrimStart('@', '_').Replace("_", string.Empty).ToLowerInvariant();

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
                    SwitchTo("fields");
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
            foreach (var row in _inventoryRows) row.Refresh();

            UpdateHeader();
            UpdateStatus();
            UpdateDetailBadge();
            UpdateItemBadge();

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
        foreach (var row in _inventoryRows) row.Refresh();
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
            foreach (var row in _inventoryRows) row.Refresh();
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
            UpdateRightPanel();
            return;
        }

        TxtFieldName.Text = _selected.DisplayName;

        string? hint = _selected.FieldHint;
        TxtFieldHint.Text = hint ?? string.Empty;
        TxtFieldHint.Visibility = hint is null ? Visibility.Collapsed : Visibility.Visible;

        string? rawKey = _selected.RawKey;
        TxtRawKey.Text = rawKey is null ? string.Empty : $"原始字段：{rawKey}";
        TxtRawKey.Visibility = rawKey is null ? Visibility.Collapsed : Visibility.Visible;

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

        UpdateRightPanel();
    }

    private void TxtValue_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressValueChange || _selected is null || _selected.IsContainer) return;
        if (!_selected.IsEditable) return;

        if (TryParseValue(TxtValue.Text, _selected.Model.Kind, out object? value))
        {
            _selected.Model.SetValue(value);
            _selected.Refresh();
            RefreshInventoryNode(_selected.Model);

            TxtValue.ClearValue(Control.BorderBrushProperty);
            UpdateDetailBadge();
            BtnSave.IsEnabled = CanWriteDocument;
            UpdateStatus();
        }
        else
        {
            TxtValue.BorderBrush = (Brush)FindResource("Brush.Danger");
        }
    }

    /// <summary>字段树里改了库存数量时，同步到库存表格 / 详情。</summary>
    private void RefreshInventoryNode(SaveNode node)
    {
        var row = _inventoryRows.FirstOrDefault(r => ReferenceEquals(r.Entry.Node, node));
        if (row is null) return;

        row.Refresh();

        if (ReferenceEquals(row, _selectedItem))
        {
            _suppressItemQty = true;
            TxtItemQty.Text = row.QuantityText;
            _suppressItemQty = false;
            UpdateItemBadge();
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

    private bool CanWriteDocument =>
        _document is not null &&
        _service.Registry.Find(_document.FormatId)?.CanWrite == true &&
        !_document.IsReadOnly;

    private void UpdateHeader()
    {
        if (_document is null)
        {
            Title = "SaveForge · 存档修改器";
            BtnSave.IsEnabled = false;
            BtnBackup.IsEnabled = false;
            BtnDiff.IsEnabled = false;
            BtnBatch.IsEnabled = false;
            BtnExportJson.IsEnabled = false;
            BtnImportJson.IsEnabled = false;
            BtnGameRoot.IsEnabled = true;
            BtnLoadDatabase.IsEnabled = false;
            BtnChooseGameRoot.IsEnabled = true;
            return;
        }

        string fileName = Path.GetFileName(_document.FilePath ?? "(未命名)");
        Title = $"SaveForge · 存档修改器 — {fileName}";

        bool hasFile = _document.FilePath is not null && File.Exists(_document.FilePath);

        BtnSave.IsEnabled = CanWriteDocument;
        BtnBackup.IsEnabled = hasFile;
        BtnDiff.IsEnabled = hasFile;
        BtnBatch.IsEnabled = CanWriteDocument;
        BtnExportJson.IsEnabled = true;
        BtnImportJson.IsEnabled = CanWriteDocument;
        BtnGameRoot.IsEnabled = true;
        BtnLoadDatabase.IsEnabled = IsRpgMakerSave(_document.FilePath ?? "", _document.FormatId);
        BtnChooseGameRoot.IsEnabled = true;
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
        string fileName = Path.GetFileName(_document.FilePath ?? "(未命名)");

        TxtStatus.Text = $"{fileName} · {_document.FormatName} · {FormatSize(_document.OriginalBytes.Length)}{mode}{database}";

        int modified = _document.ModifiedCount;
        TxtModified.Text = modified > 0 ? $"● 已修改 {modified} 项" : string.Empty;
    }

    private void UpdateDetailBadge()
    {
        bool modified = _selected?.IsModified == true;
        BadgeModified.Visibility = modified ? Visibility.Visible : Visibility.Collapsed;
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
