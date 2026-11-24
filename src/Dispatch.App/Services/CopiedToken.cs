using Dispatch.Application.Auth;

namespace Dispatch.App.Services;

/// <summary>A bearer token the user copied, and where it came from.</summary>
public sealed record CopiedToken(string Token, string? Field, Guid? SourceRequestId, string? SourceName)
{
    /// <summary>"access_token from Login", "from Login", or "from the clipboard".</summary>
    public string Origin => (Field, SourceName) switch
    {
        ({ Length: > 0 } f, { Length: > 0 } s) => $"{f} from {s}",
        ({ Length: > 0 } f, _) => f,
        (_, { Length: > 0 } s) => $"from {s}",
        _ => "from the clipboard"
    };
}

/// <summary>
/// Remembers the last JWT copied from a response, so other request tabs can offer it for their Auth. Tokens copied
/// elsewhere (a browser, another app) are picked up from the clipboard when a tab is shown.
/// </summary>
public sealed class CopiedTokenTracker(IClipboardService clipboard)
{
    private CopiedToken? _last;

    public event EventHandler? Changed;

    public CopiedToken? Last => _last;

    /// <summary>Copies the token (bare or as "Bearer &lt;token&gt;") and remembers it.</summary>
    public async Task CopyAsync(string token, bool asBearer, string? field, Guid? sourceRequestId, string? sourceName)
    {
        await clipboard.SetTextAsync(asBearer ? BearerTokens.AsBearer(token) : token);
        _last = new CopiedToken(token, field, sourceRequestId, sourceName);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The token to offer now: the clipboard's when it holds a JWT (so a token copied by hand counts too), else the
    /// last one copied with the response buttons while the clipboard can't be read. An empty clipboard, or one holding
    /// other text or an image, offers nothing: the copied token is no longer what the user has in hand.
    /// </summary>
    public async Task<CopiedToken?> CurrentAsync()
    {
        string? text;
        try
        {
            text = await clipboard.GetTextAsync();
        }
        catch (Exception)
        {
            return _last;
        }

        if (BearerTokens.Normalize(text) is not { } bare)
            return null; // empty, or the user copied something else since: don't offer a stale token
        if (_last is not null && string.Equals(_last.Token, bare, StringComparison.Ordinal))
            return _last;
        return new CopiedToken(bare, null, null, null);
    }
}
