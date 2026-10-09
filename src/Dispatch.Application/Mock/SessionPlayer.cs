using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dispatch.Application.Testing;
using Dispatch.Domain;

namespace Dispatch.Application.Mock;

/// <summary>A server message to send after a delay (relative to the previous one in the same batch).</summary>
public sealed record ScheduledMessage(TimeSpan Delay, string Content, string? Label);

/// <summary>What the player did with a client message, for the mock server log.</summary>
public sealed record SessionReply(IReadOnlyList<ScheduledMessage> Messages, string How);

public sealed class SessionReplayOptions
{
    /// <summary>1 = original timing, 2 = twice as fast, 0 = no delays.</summary>
    public double Speed { get; init; } = 1;

    /// <summary>Longest pause between two replayed messages, so idle stretches of a recording don't stall the mock.</summary>
    public TimeSpan MaxGap { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>Turns a live session's message log into a recording, and recordings into replayable segments.</summary>
public static class SessionRecording
{
    /// <summary>Sent and received messages of a session, timed from the first message (connection notices are dropped).</summary>
    public static List<SessionMessage> From(IReadOnlyList<StreamMessage> messages)
    {
        var relevant = messages.Where(m => m.Direction is MessageDirection.Sent or MessageDirection.Received).ToList();
        if (relevant.Count == 0)
            return [];
        // Time from connecting when the log has it, so the delay before the first server message is kept.
        var start = messages.FirstOrDefault(m => m.Direction == MessageDirection.Info)?.Timestamp ?? relevant[0].Timestamp;
        if (start > relevant[0].Timestamp)
            start = relevant[0].Timestamp;
        return relevant.Select(m => new SessionMessage
        {
            AtMs = (long)Math.Max(0, (m.Timestamp - start).TotalMilliseconds), Direction = m.Direction, Content = m.Content, Label = m.Label
        }).ToList();
    }
}

/// <summary>
/// Replays a recorded WebSocket / SSE session for one connection. The recording is split into an opening (server
/// messages before the client's first message) and segments: each recorded client message followed by the server
/// messages that answered it. A live client message plays the segment whose recorded trigger matches it — exactly, or as
/// JSON with the same fields and values apart from ids — then a matching segment already played (so a second ping gets
/// its pong again), or else the next segment not yet played. Ids the client sends
/// (id, requestId, correlationId, …) are substituted into the replies where the recording echoed them, and replies may
/// use <c>{{message.path}}</c> to copy values from the client's message.
/// </summary>
public sealed partial class SessionPlayer
{
    private sealed record Segment(SessionMessage Trigger, List<SessionMessage> Replies);

    private static readonly HashSet<string> IdKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "requestId", "request_id", "correlationId", "correlation_id", "msgId", "messageId", "message_id", "reqId", "ref", "nonce", "seq", "cid", "txId"
    };

    private readonly SessionReplayOptions _options;
    private readonly List<SessionMessage> _opening;
    private readonly List<Segment> _segments;
    private readonly bool[] _played;

    public SessionPlayer(IReadOnlyList<SessionMessage> session, SessionReplayOptions? options = null)
    {
        _options = options ?? new SessionReplayOptions();
        var ordered = session.OrderBy(m => m.AtMs).ToList();
        _opening = ordered.TakeWhile(m => m.Direction != MessageDirection.Sent).Where(m => m.Direction == MessageDirection.Received).ToList();
        _segments = [];
        Segment? current = null;
        foreach (var message in ordered.Skip(_opening.Count))
        {
            if (message.Direction == MessageDirection.Sent)
                _segments.Add(current = new Segment(message, []));
            else if (message.Direction == MessageDirection.Received && current is not null)
                current.Replies.Add(message);
        }
        // A client that sends on connect (before any server message) records the server's greeting as part of the first
        // reply. When the first reply has a message correlated with the trigger (echoing its id), whatever the server sent
        // before that wasn't an answer: move it to the opening, so clients that wait for the greeting get it.
        if (_opening.Count == 0 && _segments.Count > 0 && Parse(_segments[0].Trigger.Content) is JsonObject trigger
            && trigger.Where(kv => IdKeys.Contains(kv.Key) && kv.Value is JsonValue).Select(kv => kv.Value!.ToJsonString()).ToList() is { Count: > 0 } ids)
        {
            var first = _segments[0];
            var answer = first.Replies.FindIndex(r => Parse(r.Content) is { } reply && ContainsValue(reply, ids));
            if (answer > 0)
            {
                _opening.AddRange(first.Replies.Take(answer));
                first.Replies.RemoveRange(0, answer);
            }
        }
        _played = new bool[_segments.Count];
    }

    private static bool ContainsValue(JsonNode node, IReadOnlyCollection<string> values) => node switch
    {
        JsonObject o => o.Any(kv => IdKeys.Contains(kv.Key) && kv.Value is JsonValue v && values.Contains(v.ToJsonString())
                                    || kv.Value is not null && ContainsValue(kv.Value, values)),
        JsonArray a => a.Any(i => i is not null && ContainsValue(i, values)),
        _ => false
    };

    public int SegmentCount => _segments.Count;

    /// <summary>The server messages to send right after the client connects.</summary>
    public IReadOnlyList<ScheduledMessage> Opening() => Schedule(_opening, startMs: 0, trigger: null, recordedTrigger: null);

    /// <summary>The replies to a client message, or none when the recording has nothing left to answer with.</summary>
    public SessionReply OnClientMessage(string text)
    {
        var live = Parse(text);
        int index;
        string how;
        if ((index = FindUnplayed(s => s.Trigger.Content == text)) >= 0)
            how = "exact match";
        else if (live is not null && (index = FindUnplayed(s => Parse(s.Trigger.Content) is { } recorded && SameExceptIds(recorded, live))) >= 0)
            how = "matched ignoring ids";
        // A message the recording already answered (a second ping, a re-subscribe) gets the same answer again rather
        // than the next unrelated segment.
        else if ((index = FindPlayed(s => s.Trigger.Content == text)) >= 0)
            how = "exact match (repeated)";
        else if (live is not null && (index = FindPlayed(s => Parse(s.Trigger.Content) is { } recorded && SameExceptIds(recorded, live))) >= 0)
            how = "matched ignoring ids (repeated)";
        else if ((index = FindUnplayed(_ => true)) >= 0)
            how = "next in recorded order";
        else
            return new SessionReply([], "no recorded reply left");

        _played[index] = true;
        var segment = _segments[index];
        return new SessionReply(Schedule(segment.Replies, segment.Trigger.AtMs, live, Parse(segment.Trigger.Content)),
            $"{how} → {segment.Replies.Count} message(s)");
    }

    private int FindUnplayed(Func<Segment, bool> predicate) => Find(played: false, predicate);

    private int FindPlayed(Func<Segment, bool> predicate) => Find(played: true, predicate);

    private int Find(bool played, Func<Segment, bool> predicate)
    {
        for (var i = 0; i < _segments.Count; i++)
            if (_played[i] == played && predicate(_segments[i]))
                return i;
        return -1;
    }

    private IReadOnlyList<ScheduledMessage> Schedule(List<SessionMessage> messages, long startMs, JsonNode? trigger, JsonNode? recordedTrigger)
    {
        var list = new List<ScheduledMessage>();
        var previous = startMs;
        foreach (var message in messages)
        {
            var gap = TimeSpan.FromMilliseconds(Math.Max(0, message.AtMs - previous));
            previous = message.AtMs;
            var delay = _options.Speed <= 0 ? TimeSpan.Zero : TimeSpan.FromTicks((long)(Math.Min(gap.Ticks, _options.MaxGap.Ticks) / _options.Speed));
            list.Add(new ScheduledMessage(delay, Render(message.Content, trigger, recordedTrigger), message.Label));
        }
        return list;
    }

    /// <summary>Applies <c>{{message.path}}</c> templates and swaps recorded ids for the live client's ids.</summary>
    internal static string Render(string content, JsonNode? trigger, JsonNode? recordedTrigger)
    {
        if (trigger is not null)
            content = TemplateRegex().Replace(content, m =>
                JsonPath.SelectText(trigger, "$." + m.Groups[1].Value) is { } value ? value : m.Value);

        if (trigger is not JsonObject live || recordedTrigger is not JsonObject recorded || Parse(content) is not { } reply)
            return content;
        var swaps = recorded.Where(kv => IdKeys.Contains(kv.Key) && kv.Value is JsonValue && live[kv.Key] is JsonValue)
            .Select(kv => (Key: kv.Key, From: kv.Value!.ToJsonString(), To: live[kv.Key]!))
            .Where(s => s.From != s.To.ToJsonString()).ToList();
        if (swaps.Count == 0)
            return content;
        var changed = false;
        void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var (key, value) in obj.ToList())
                    {
                        if (IdKeys.Contains(key) && value is JsonValue v && swaps.FirstOrDefault(s => s.From == v.ToJsonString()) is { To: { } to })
                        {
                            obj[key] = to.DeepClone();
                            changed = true;
                        }
                        else
                            Walk(value);
                    }
                    break;
                case JsonArray array:
                    foreach (var item in array)
                        Walk(item);
                    break;
            }
        }
        Walk(reply);
        return changed ? reply.ToJsonString() : content;
    }

    /// <summary>Same JSON structure and values, ignoring id-like fields (which change from one connection to the next).</summary>
    internal static bool SameExceptIds(JsonNode recorded, JsonNode live) => (recorded, live) switch
    {
        (JsonObject a, JsonObject b) => a.Count == b.Count && a.All(kv => b.ContainsKey(kv.Key)
                                                                         && (IdKeys.Contains(kv.Key) || SameExceptIds(kv.Value!, b[kv.Key]!))),
        (JsonArray a, JsonArray b) => a.Count == b.Count && a.Zip(b).All(p => SameExceptIds(p.First!, p.Second!)),
        _ => JsonNode.DeepEquals(recorded, live)
    };

    private static JsonNode? Parse(string text)
    {
        var t = text.TrimStart();
        if (!(t.StartsWith('{') || t.StartsWith('[')))
            return null;
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"\{\{\s*message\.([^}\s]+)\s*\}\}")]
    private static partial Regex TemplateRegex();
}
