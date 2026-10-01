using Dispatch.Domain;

namespace Dispatch.Application.Interop;

/// <summary>Gives imported requests fresh ids while keeping references between them (message listeners) intact.</summary>
public static class RequestIdentity
{
    /// <summary>Assigns new ids to <paramref name="requests"/> and rewrites expectations that point at one of them.</summary>
    public static IReadOnlyDictionary<Guid, Guid> Reassign(IReadOnlyList<ApiRequest> requests)
    {
        var map = new Dictionary<Guid, Guid>();
        foreach (var request in requests)
        {
            var fresh = Guid.NewGuid();
            map.TryAdd(request.Id, fresh);
            request.Id = fresh;
        }
        foreach (var expectation in requests.SelectMany(r => r.Expectations))
            if (map.TryGetValue(expectation.ListenerId, out var listener))
                expectation.ListenerId = listener;
        return map;
    }
}
