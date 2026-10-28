using System.ComponentModel;
using SaveForge.Core;

namespace SaveForge.App.ViewModels;

/// <summary>
/// 库存表格的一行。数量列的编辑会直接写回 SaveNode，
/// 这样表格、右侧详情、字段树三条路径改的都是同一份数据。
/// </summary>
public sealed class InventoryRowVm : INotifyPropertyChanged
{
    private const long MaxQuantity = 9_999_999;

    public InventoryEntry Entry { get; }

    public InventoryRowVm(InventoryEntry entry)
    {
        Entry = entry;
    }

    /// <summary>数量被修改后通知窗口刷新状态栏 / 保存按钮。</summary>
    public event Action? ValueChanged;

    public string Name => Entry.DisplayName;

    public string Id => Entry.Id;

    public string CategoryLabel => InventoryScanner.DisplayName(Entry.Category);

    public string TypeAndId => $"{CategoryLabel} · ID {Id}";

    public bool IsKnown => Entry.Name is not null;

    public bool IsModified => Entry.Node.IsModified;

    public string Locator => Entry.Node.Locator ?? string.Empty;

    public string QuantityText
    {
        get => Entry.Quantity.ToString();
        set
        {
            string text = value?.Trim() ?? string.Empty;

            if (!long.TryParse(text, out long quantity) || quantity < 0)
            {
                // 非法输入时不改模型，把单元格恢复成原值
                OnPropertyChanged(nameof(QuantityText));
                return;
            }

            if (quantity > MaxQuantity) quantity = MaxQuantity;

            if (quantity == Entry.Quantity)
            {
                OnPropertyChanged(nameof(QuantityText));
                return;
            }

            Entry.Node.SetValue(quantity);
            OnPropertyChanged(nameof(QuantityText));
            OnPropertyChanged(nameof(IsModified));
            ValueChanged?.Invoke();
        }
    }

    /// <summary>外部（右侧详情）修改模型后刷新表格显示。</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(QuantityText));
        OnPropertyChanged(nameof(IsModified));
        OnPropertyChanged(nameof(Name));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
