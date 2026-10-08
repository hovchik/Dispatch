using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dispatch.Application.Requests;
using Dispatch.Domain;

namespace Dispatch.App.ViewModels;

/// <summary>
/// Editable key/value table (params, headers, form fields, variables). Like Postman, it always keeps
/// one empty "placeholder" row at the bottom; typing into it turns it into a real row.
/// Lists that <see cref="SupportsBulkEdit"/> can also be edited as <c>key: value</c> lines.
/// </summary>
public sealed partial class KeyValueListViewModel : ObservableObject
{
    private bool _suppressChanged;
    private bool _syncingBulk;

    public KeyValueListViewModel(string keyWatermark = "Key", string valueWatermark = "Value", bool supportsBulkEdit = false,
        bool supportsSecret = false, bool supportsFile = false)
    {
        KeyWatermark = keyWatermark;
        ValueWatermark = valueWatermark;
        SupportsBulkEdit = supportsBulkEdit;
        SupportsSecret = supportsSecret;
        SupportsFile = supportsFile;
        EnsurePlaceholderRow();
    }

    public bool SupportsBulkEdit { get; }

    /// <summary>Rows can be marked secret (masked, encrypted at rest, not exported). Environment variables.</summary>
    public bool SupportsSecret { get; }

    /// <summary>Rows can be files instead of text (multipart form fields).</summary>
    public bool SupportsFile { get; }

    /// <summary>Pick a file for a row; set by the view (needs a window for the dialog).</summary>
    public Func<Task<string?>>? PickFile { get; set; }

    [ObservableProperty] private bool _isBulkEdit;
    [ObservableProperty] private string _bulkText = string.Empty;

    public ObservableCollection<KeyValueRowViewModel> Rows { get; } = [];
    public string KeyWatermark { get; }
    public string ValueWatermark { get; }

    /// <summary>Number of enabled rows with a key; shown as a badge on the editor tab.</summary>
    public int ActiveCount => Rows.Count(r => !r.IsPlaceholder && r.Enabled && r.Key.Length > 0);
    public bool HasActive => ActiveCount > 0;

    /// <summary>Raised after any user-visible change (edit, toggle, add, remove, load).</summary>
    public event EventHandler? Changed;

    public void Load(IEnumerable<KeyValueItem> items)
    {
        _suppressChanged = true;
        try
        {
            Rows.Clear();
            foreach (var item in items)
                Rows.Add(new KeyValueRowViewModel(this, item.Key, item.Value, item.Enabled) { IsSecret = item.IsSecret, IsFile = item.IsFile });
            EnsurePlaceholderRow();
        }
        finally
        {
            _suppressChanged = false;
        }
        if (!_syncingBulk)
            RefreshBulkText();
        RaiseChanged();
    }

    /// <summary>Sets the value of the first row named <paramref name="key"/> (case-insensitive), adding the row if missing.</summary>
    public void SetValue(string key, string value)
    {
        var row = Find(key);
        if (row is null)
            Rows.Insert(Rows.Count - 1, new KeyValueRowViewModel(this, key, value, enabled: true));
        else
            row.Value = value;
        EnsurePlaceholderRow();
        RefreshBulkText();
        RaiseChanged();
    }

    public void RemoveKey(string key)
    {
        if (Find(key) is { } row)
            Remove(row);
    }

    public string? GetValue(string key) => Find(key)?.Value;

    private KeyValueRowViewModel? Find(string key) =>
        Rows.FirstOrDefault(r => !r.IsPlaceholder && string.Equals(r.Key.Trim(), key, StringComparison.OrdinalIgnoreCase));

    // ---- Bulk edit ---------------------------------------------------------------------------

    [RelayCommand]
    private void ToggleBulkEdit() => IsBulkEdit = !IsBulkEdit;

    partial void OnIsBulkEditChanged(bool value) => RefreshBulkText();

    partial void OnBulkTextChanged(string value)
    {
        if (_syncingBulk || !IsBulkEdit)
            return;

        _syncingBulk = true;
        try
        {
            // Bulk text only carries key/value/enabled; keep the secret/file flags of rows that still exist.
            var flags = Rows.Where(r => !r.IsEmpty && (r.IsSecret || r.IsFile))
                .GroupBy(r => r.Key.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var items = KeyValueBulkText.Parse(value);
            foreach (var item in items)
                if (flags.TryGetValue(item.Key.Trim(), out var row))
                {
                    item.IsSecret = row.IsSecret;
                    item.IsFile = row.IsFile;
                }
            Load(items);
        }
        finally
        {
            _syncingBulk = false;
        }
    }

    /// <summary>Rewrites the bulk text from the rows (only while it's shown, so the user's typing isn't reformatted).</summary>
    private void RefreshBulkText()
    {
        if (!IsBulkEdit || _syncingBulk)
            return;

        _syncingBulk = true;
        try
        {
            BulkText = KeyValueBulkText.Format(ToItems());
        }
        finally
        {
            _syncingBulk = false;
        }
    }

    public List<KeyValueItem> ToItems() => Rows
        .Where(r => !r.IsEmpty)
        .Select(r => new KeyValueItem(r.Key, r.Value, r.Enabled) { IsSecret = r.IsSecret, IsFile = r.IsFile })
        .ToList();

    internal void OnRowChanged(KeyValueRowViewModel row)
    {
        if (_suppressChanged)
            return;

        EnsurePlaceholderRow();
        RaiseChanged();
    }

    internal void Remove(KeyValueRowViewModel row)
    {
        Rows.Remove(row);
        EnsurePlaceholderRow();
        RefreshBulkText();
        RaiseChanged();
    }

    private void EnsurePlaceholderRow()
    {
        if (Rows.Count == 0 || !Rows[^1].IsEmpty)
            Rows.Add(new KeyValueRowViewModel(this, string.Empty, string.Empty, enabled: true));

        for (var i = 0; i < Rows.Count; i++)
            Rows[i].IsPlaceholder = i == Rows.Count - 1 && Rows[i].IsEmpty;
    }

    private void RaiseChanged()
    {
        OnPropertyChanged(nameof(ActiveCount));
        OnPropertyChanged(nameof(HasActive));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public sealed partial class KeyValueRowViewModel : ObservableObject
{
    private readonly KeyValueListViewModel _owner;

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _key;
    [ObservableProperty] private string _value;
    [ObservableProperty] private bool _isPlaceholder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaskChar))]
    private bool _isSecret;

    [ObservableProperty] private bool _isFile;

    /// <summary>Secret values are masked until the row is un-marked.</summary>
    public char MaskChar => IsSecret ? '•' : '\0';
    public bool SupportsSecret => _owner.SupportsSecret;
    public bool SupportsFile => _owner.SupportsFile;

    public KeyValueRowViewModel(KeyValueListViewModel owner, string key, string value, bool enabled)
    {
        _owner = owner;
        _key = key;
        _value = value;
        _enabled = enabled;
    }

    public string KeyWatermark => _owner.KeyWatermark;
    public string ValueWatermark => _owner.ValueWatermark;
    public bool IsEmpty => Key.Length == 0 && Value.Length == 0;

    partial void OnEnabledChanged(bool value) => _owner.OnRowChanged(this);
    partial void OnKeyChanged(string value) => _owner.OnRowChanged(this);
    partial void OnValueChanged(string value) => _owner.OnRowChanged(this);
    partial void OnIsSecretChanged(bool value) => _owner.OnRowChanged(this);
    partial void OnIsFileChanged(bool value) => _owner.OnRowChanged(this);

    [RelayCommand]
    private void Remove() => _owner.Remove(this);

    [RelayCommand]
    private async Task ChooseFileAsync()
    {
        if (_owner.PickFile is null)
            return;
        var path = await _owner.PickFile();
        if (path is null)
            return;
        IsFile = true;
        Value = path;
        if (Key.Length == 0)
            Key = "file";
    }
}

