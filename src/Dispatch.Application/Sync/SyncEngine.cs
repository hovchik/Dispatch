namespace Dispatch.Application.Sync;

/// <summary>What a sync does with one item (a request, or the collection's metadata).</summary>
public enum SyncAction
{
    /// <summary>Both sides are equal.</summary>
    None,

    /// <summary>Changed (or new) in the app: write it to the folder.</summary>
    ToFolder,

    /// <summary>Changed (or new) in the folder: apply it to the app.</summary>
    ToApp,

    /// <summary>Deleted in the folder: delete it in the app.</summary>
    DeleteInApp,

    /// <summary>Deleted in the app: delete its file.</summary>
    DeleteInFolder,

    /// <summary>Changed on both sides (or changed on one and deleted on the other) since the last sync.</summary>
    Conflict
}

public enum SyncSide
{
    App,
    Folder
}

/// <summary>The decision for one key; the three texts are the canonical JSON on each side (null = does not exist).</summary>
public sealed record SyncDecision(string Key, SyncAction Action, string? App, string? Folder, string? Base);

/// <summary>
/// Three-way comparison between the app's copy, the folder's copy and the state recorded at the last sync. Pure:
/// callers load the three maps (key → canonical JSON) and apply the decisions. With no recorded state, every item
/// present on only one side is copied to the other and items that differ on both sides are conflicts.
/// </summary>
public static class SyncEngine
{
    public static IReadOnlyList<SyncDecision> Plan(IReadOnlyDictionary<string, string> app, IReadOnlyDictionary<string, string> folder,
        IReadOnlyDictionary<string, string> last)
    {
        var keys = app.Keys.Concat(folder.Keys).Concat(last.Keys).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal);
        var decisions = new List<SyncDecision>();
        foreach (var key in keys)
        {
            var a = app.GetValueOrDefault(key);
            var f = folder.GetValueOrDefault(key);
            var b = last.GetValueOrDefault(key);
            decisions.Add(new SyncDecision(key, Decide(a, f, b), a, f, b));
        }
        return decisions;
    }

    private static SyncAction Decide(string? app, string? folder, string? last)
    {
        if (app == folder)
            return SyncAction.None;
        var appChanged = app != last;
        var folderChanged = folder != last;
        if (appChanged && !folderChanged)
            return app is null ? SyncAction.DeleteInFolder : SyncAction.ToFolder;
        if (folderChanged && !appChanged)
            return folder is null ? SyncAction.DeleteInApp : SyncAction.ToApp;
        return SyncAction.Conflict;
    }

    /// <summary>
    /// The state to record after applying <paramref name="decisions"/>: every non-conflicting key holds the merged
    /// value; a conflicting key keeps what was recorded before, so it stays a conflict until someone resolves it.
    /// </summary>
    public static Dictionary<string, string> NextState(IEnumerable<SyncDecision> decisions)
    {
        var next = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var d in decisions)
        {
            var value = d.Action switch
            {
                SyncAction.None => d.App,
                SyncAction.ToFolder => d.App,
                SyncAction.ToApp => d.Folder,
                SyncAction.DeleteInApp or SyncAction.DeleteInFolder => null,
                _ => d.Base
            };
            if (value is not null)
                next[d.Key] = value;
        }
        return next;
    }

    /// <summary>
    /// Resolving a conflict means pretending the losing side never changed: recording the loser's current text as
    /// the last-synced state makes the next plan copy the winner over it.
    /// </summary>
    public static void Resolve(Dictionary<string, string> last, SyncDecision conflict, SyncSide keep)
    {
        var loser = keep == SyncSide.App ? conflict.Folder : conflict.App;
        if (loser is null)
            last.Remove(conflict.Key);
        else
            last[conflict.Key] = loser;
    }
}
