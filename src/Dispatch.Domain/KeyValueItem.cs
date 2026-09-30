namespace Dispatch.Domain;

/// <summary>A toggleable key/value pair used for query params, headers, form fields, metadata and variables.</summary>
public sealed class KeyValueItem
{
    public bool Enabled { get; set; } = true;
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;

    /// <summary>Multipart form fields only: <see cref="Value"/> is a file path to upload.</summary>
    public bool IsFile { get; set; }

    /// <summary>Environment variables only: the value is encrypted at rest, masked in the UI and left out of exports.</summary>
    public bool IsSecret { get; set; }

    public KeyValueItem() { }

    public KeyValueItem(string key, string value, bool enabled = true)
    {
        Key = key;
        Value = value;
        Enabled = enabled;
    }

    /// <summary>True when the row is enabled and has a non-blank key.</summary>
    public bool IsActive => Enabled && !string.IsNullOrWhiteSpace(Key);

    public KeyValueItem Clone() => new(Key, Value, Enabled) { IsFile = IsFile, IsSecret = IsSecret };
}
