namespace Dispatch.Domain;

/// <summary>A toggleable key/value pair used for query params, headers, form fields and variables.</summary>
public sealed class KeyValueItem
{
    public bool Enabled { get; set; } = true;
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;

    public KeyValueItem() { }

    public KeyValueItem(string key, string value, bool enabled = true)
    {
        Key = key;
        Value = value;
        Enabled = enabled;
    }

    /// <summary>True when the row is enabled and has a non-blank key.</summary>
    public bool IsActive => Enabled && !string.IsNullOrWhiteSpace(Key);

    public KeyValueItem Clone() => new(Key, Value, Enabled);
}
