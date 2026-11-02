using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dispatch.Application.Reporting;
using Dispatch.Application.Requests;
using Dispatch.Application.Testing;
using Dispatch.Domain;

namespace Dispatch.Application.Probes;

/// <summary>How the walker gets from one page to the next.</summary>
public enum PaginationStrategy
{
    /// <summary>Work it out from the first response and the request's query.</summary>
    Auto,

    /// <summary>A <c>Link: &lt;…&gt;; rel="next"</c> header.</summary>
    LinkHeader,

    /// <summary>A "next" URL in the body (<c>$.next</c>, <c>$.links.next</c>, <c>$._links.next.href</c>, …).</summary>
    NextUrl,

    /// <summary>An opaque cursor in the body sent back as a query parameter.</summary>
    Cursor,

    /// <summary>A page number query parameter.</summary>
    Page,

    /// <summary>An offset (skip) query parameter.</summary>
    Offset
}

public sealed class PaginationOptions : ProbeOptions
{
    public PaginationStrategy Strategy { get; init; } = PaginationStrategy.Auto;

    /// <summary>JSONPath to the page's items; empty = detect (root array, $.data, $.items, $.results, …).</summary>
    public string? ItemsPath { get; init; }

    /// <summary>Member of an item that identifies it; empty = detect (id, uuid, _id, key), else the whole item.</summary>
    public string? IdField { get; init; }

    /// <summary>Page or offset parameter; empty = detect.</summary>
    public string? PageParam { get; init; }

    /// <summary>Page size parameter; empty = detect (limit, per_page, page_size, size, …).</summary>
    public string? SizeParam { get; init; }

    /// <summary>Page size to request; null = keep the request's own value (or the server default).</summary>
    public int? PageSize { get; init; }

    /// <summary>Query parameter the cursor is sent in; empty = detect.</summary>
    public string? CursorParam { get; init; }

    /// <summary>JSONPath to the next cursor in the body; empty = detect.</summary>
    public string? CursorPath { get; init; }

    public int MaxPages { get; init; } = 50;

    /// <summary>Also try edge cases: the page after the last, page 0 / negative, size 0 and a huge size.</summary>
    public bool EdgeCases { get; init; } = true;

    /// <summary>Fetch the first page again at the end to check that the order is stable.</summary>
    public bool CheckStability { get; init; } = true;
}

/// <summary>
/// Walks a paginated list endpoint to the end — following Link headers, "next" URLs, cursors, or page / offset
/// parameters — and reports duplicates, gaps, totals that don't add up, loops, unstable ordering and how the endpoint
/// handles out-of-range or invalid paging parameters.
/// </summary>
public sealed partial class PaginationWalker(IRequestSender sender)
{
    private static readonly string[] ItemPaths = ["$.data", "$.items", "$.results", "$.records", "$.entries", "$.content", "$.rows",
        "$.list", "$.values", "$.hits.hits", "$.data.items", "$.data.results", "$.result.items", "$._embedded"];

    // Nested "href" forms come before their parents, so { "links": { "next": { "href": … } } } resolves to the URL.
    private static readonly string[] NextUrlPaths = ["$.next.href", "$.next", "$.links.next.href", "$.links.next", "$._links.next.href", "$.paging.next",
        "$.pagination.next", "$.pagination.next_url", "$.pagination.nextUrl", "$.meta.next", "$.meta.pagination.links.next", "$.next_page_url",
        "$.nextPageUrl", "$.@odata.nextLink", "$['@odata.nextLink']", "$.next_url", "$.nextUrl", "$.links.next_page"];

    private static readonly string[] CursorPaths = ["$.next_cursor", "$.nextCursor", "$.cursor.next", "$.meta.next_cursor", "$.meta.nextCursor",
        "$.pagination.next_cursor", "$.pagination.nextCursor", "$.paging.cursors.after", "$.response_metadata.next_cursor", "$.nextPageToken",
        "$.next_page_token", "$.continuationToken", "$.continuation_token", "$.page_info.end_cursor", "$.pageInfo.endCursor", "$.next_token", "$.nextToken"];

    private static readonly string[] TotalPaths = ["$.total", "$.total_count", "$.totalCount", "$.totalItems", "$.total_items", "$.totalElements",
        "$.count", "$.meta.total", "$.meta.total_count", "$.meta.totalCount", "$.pagination.total", "$.pagination.total_count", "$.pagination.totalCount",
        "$.paging.total", "$.hits.total.value", "$.hits.total", "$.@odata.count", "$['@odata.count']"];

    private static readonly string[] PageParams = ["page", "_page", "p", "page_number", "pageNumber", "pageIndex", "page[number]"];
    private static readonly string[] OffsetParams = ["offset", "_offset", "skip", "start", "from", "_start", "page[offset]"];
    private static readonly string[] SizeParams = ["limit", "per_page", "perPage", "page_size", "pageSize", "size", "_limit", "count", "top", "$top", "page[size]", "page[limit]"];
    private static readonly string[] CursorParams = ["cursor", "after", "starting_after", "page_token", "pageToken", "next_token", "continuation", "page[after]", "page[cursor]"];
    private static readonly string[] IdFields = ["id", "uuid", "_id", "key", "slug", "guid"];

    [GeneratedRegex("<([^>]*)>\\s*;[^,]*rel\\s*=\\s*\"?([^\",;]*)\"?", RegexOptions.IgnoreCase)]
    private static partial Regex LinkRegex();

    /// <summary>The URL with <c>rel="next"</c> in a Link header, if any.</summary>
    public static string? NextLink(ApiResponse response)
    {
        foreach (var header in ProbeHelpers.Headers(response, "Link"))
            foreach (Match m in LinkRegex().Matches(header))
                if (m.Groups[2].Value.Split(' ').Any(r => r.Equals("next", StringComparison.OrdinalIgnoreCase)))
                    return m.Groups[1].Value;
        return null;
    }

    /// <summary>The items of a page: at <paramref name="itemsPath"/>, the root array, or the first well-known list member.</summary>
    public static (string Path, JsonArray Items)? FindItems(JsonNode? body, string? itemsPath)
    {
        if (body is null)
            return null;
        if (!string.IsNullOrWhiteSpace(itemsPath))
            return JsonPath.Select(body, itemsPath).FirstOrDefault() is JsonArray a ? (itemsPath, a) : null;
        if (body is JsonArray root)
            return ("$", root);
        foreach (var path in ItemPaths)
        {
            var match = JsonPath.Select(body, path).FirstOrDefault();
            if (match is JsonArray arr)
                return (path, arr);
            // HAL: _embedded holds one named list.
            if (match is JsonObject embedded && embedded.Count == 1 && embedded.First().Value is JsonArray inner)
                return ($"{path}.{embedded.First().Key}", inner);
        }
        // Otherwise the largest array member of the root object.
        if (body is JsonObject o && o.Where(p => p.Value is JsonArray).OrderByDescending(p => ((JsonArray)p.Value!).Count).FirstOrDefault() is
            { Value: JsonArray best } pick)
            return ($"$.{pick.Key}", best);
        return null;
    }

    private static string Identity(JsonNode? item, ref string? idField)
    {
        if (item is JsonObject o)
        {
            idField ??= IdFields.FirstOrDefault(f => o[f] is JsonValue);
            if (idField is not null && JsonPath.Select(o, idField).FirstOrDefault() is { } value)
                return JsonPath.ToText(value);
        }
        return ProbeHelpers.Hash(ProbeHelpers.Canonical(item));
    }

    private static int? Int(JsonNode? node)
    {
        if (node is not JsonValue v)
            return null;
        if (v.TryGetValue<int>(out var i))
            return i;
        if (v.TryGetValue<long>(out var l))
            return (int)Math.Min(int.MaxValue, l);
        if (v.TryGetValue<double>(out var d))
            return (int)d;
        return v.TryGetValue<string>(out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : null;
    }

    private sealed record Page(int Number, string Url, int Status, List<string> Ids, double LatencyMs, string? Next);

    public async Task<ProbeReport> WalkAsync(ApiRequest request, PaginationOptions options, IProgress<ProbeStep>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var report = new ProbeReport
        {
            Kind = ProbeKind.Pagination, RequestName = request.Name, Method = request.Method.ToString().ToUpperInvariant(), Url = request.Url
        };
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(options.MaxDuration);
        var session = new ProbeSession(sender, report, options, progress, budget.Token);
        try
        {
            await WalkCoreAsync(request, options, report, session).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            report.Stopped = true;
        }
        session.Finish();
        return report;
    }

    private static async Task WalkCoreAsync(ApiRequest request, PaginationOptions options, ProbeReport report, ProbeSession session)
    {
        var sizeParam = options.SizeParam is { Length: > 0 } sp ? sp : SizeParams.FirstOrDefault(p => ProbeHelpers.Query(request, p) is not null);
        var first = request;
        if (options.PageSize is { } size)
        {
            sizeParam ??= "limit";
            first = ProbeHelpers.WithQuery(first, sizeParam, size.ToString(CultureInfo.InvariantCulture));
        }
        var requestedSize = sizeParam is null ? null
            : int.TryParse(ProbeHelpers.Query(first, sizeParam), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rs) ? rs : (int?)null;

        var sent = await session.SendAsync(first, "page 1").ConfigureAwait(false);
        if (sent is not { } firstResult)
            return;
        var response = firstResult.Response;
        if (!response.HasResponse || response.StatusCode is < 200 or >= 300)
        {
            report.Error = response.HasResponse
                ? $"The first page answered HTTP {response.StatusCode}; fix the request before walking its pages."
                : $"The first page got no response ({response.Error}).";
            return;
        }
        var body = ProbeHelpers.TryJson(response.Body);
        var items = FindItems(body, options.ItemsPath);
        if (items is null)
        {
            report.Error = "The first page has no JSON list of items. Set the items path (e.g. $.data) if the list is elsewhere.";
            return;
        }
        var itemsPath = items.Value.Path;

        // ---- Strategy ----
        var strategy = options.Strategy;
        string? pageParam = options.PageParam is { Length: > 0 } pp ? pp : null;
        var cursorParam = options.CursorParam is { Length: > 0 } cp ? cp : CursorParams.FirstOrDefault(p => ProbeHelpers.Query(request, p) is not null);
        var cursorPath = options.CursorPath is { Length: > 0 } cpath ? cpath : null;
        if (strategy == PaginationStrategy.Auto)
        {
            if (NextLink(response) is not null)
                strategy = PaginationStrategy.LinkHeader;
            else if (NextUrlPaths.Select(path => JsonPath.Select(body, path).FirstOrDefault()).OfType<JsonValue>().Select(JsonPath.ToText)
                     .Any(next => next.StartsWith("http", StringComparison.OrdinalIgnoreCase) || next.StartsWith('/') || next.StartsWith('?')))
                strategy = PaginationStrategy.NextUrl;
            else if (cursorPath is not null || ProbeHelpers.FirstMatch(body, CursorPaths) is not null)
                strategy = PaginationStrategy.Cursor;
            else if (pageParam is not null && OffsetParams.Contains(pageParam, StringComparer.OrdinalIgnoreCase)
                     || pageParam is null && OffsetParams.Any(p => ProbeHelpers.Query(request, p) is not null))
                strategy = PaginationStrategy.Offset;
            else
                strategy = PaginationStrategy.Page;
        }
        // Page numbers were only guessed: no paging parameter, size, link, cursor or page count to go on.
        var guessed = strategy == PaginationStrategy.Page && options.Strategy == PaginationStrategy.Auto && pageParam is null && sizeParam is null
                      && !PageParams.Any(p => ProbeHelpers.Query(request, p) is not null);
        var notPaginated = false;
        if (strategy == PaginationStrategy.Page)
            pageParam ??= PageParams.FirstOrDefault(p => ProbeHelpers.Query(request, p) is not null) ?? "page";
        if (strategy == PaginationStrategy.Offset)
            pageParam ??= OffsetParams.FirstOrDefault(p => ProbeHelpers.Query(request, p) is not null) ?? "offset";
        if (strategy == PaginationStrategy.Cursor)
        {
            cursorPath ??= ProbeHelpers.FirstMatch(body, CursorPaths)?.Path;
            cursorParam ??= cursorPath is not null && cursorPath.Contains("after", StringComparison.OrdinalIgnoreCase) ? "after"
                : cursorPath is not null && cursorPath.Contains("token", StringComparison.OrdinalIgnoreCase) ? (cursorPath.Contains('_') ? "page_token" : "pageToken")
                : "cursor";
        }

        // ---- Totals advertised ----
        int? total = Int(ProbeHelpers.FirstMatch(body, TotalPaths)?.Node);
        var totalSource = total is null ? null : ProbeHelpers.FirstMatch(body, TotalPaths)!.Value.Path;
        if (total is null && int.TryParse(ProbeHelpers.Header(response, "X-Total-Count"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var headerTotal))
        {
            total = headerTotal;
            totalSource = "X-Total-Count";
        }
        // "count" is often the size of this page rather than the total; ignore it when it equals the page size and a next page exists.
        int? totalPages = Int(ProbeHelpers.FirstMatch(body, ["$.total_pages", "$.totalPages", "$.pages", "$.page_count", "$.pageCount",
            "$.meta.last_page", "$.meta.total_pages", "$.meta.totalPages", "$.pagination.total_pages", "$.pagination.totalPages", "$.last_page"])?.Node);

        string? idField = options.IdField is { Length: > 0 } f ? f : null;
        var pages = new List<Page>();
        var startPage = 1;
        if (strategy == PaginationStrategy.Page && int.TryParse(ProbeHelpers.Query(first, pageParam!), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sp0))
            startPage = sp0;
        var offset = strategy == PaginationStrategy.Offset && int.TryParse(ProbeHelpers.Query(first, pageParam!), NumberStyles.Integer, CultureInfo.InvariantCulture, out var o0) ? o0 : 0;

        // A walk that starts past the first page can't judge totals for the whole collection.
        var partialStart = strategy == PaginationStrategy.Page && startPage > 1 || strategy == PaginationStrategy.Offset && offset > 0;

        List<string> IdsOf(JsonArray array) => array.Select(i => Identity(i, ref idField)).ToList();

        string? NextOf(ApiResponse r, JsonNode? b) => strategy switch
        {
            PaginationStrategy.LinkHeader => NextLink(r),
            PaginationStrategy.NextUrl => NextUrlPaths.Select(path => JsonPath.Select(b, path).FirstOrDefault())
                .OfType<JsonValue>().Select(JsonPath.ToText).FirstOrDefault(t => t.Length > 0 && t != "null"),
            PaginationStrategy.Cursor => cursorPath is not null && JsonPath.Select(b, cursorPath).FirstOrDefault() is { } c && JsonPath.ToText(c) is { Length: > 0 } t
                                         && t != "null" ? t : null,
            _ => null
        };

        var firstIds = IdsOf(items.Value.Items);
        pages.Add(new Page(1, report.Url, response.StatusCode, firstIds, response.Elapsed.TotalMilliseconds, NextOf(response, body)));
        // The server may cap the requested size (limit=1000 answering 100), so pages are judged by what the first page
        // actually held; the requested size is only used to spot pages that are too large.
        var pageSize = requestedSize is { } req0 && firstIds.Count > 0 ? Math.Min(req0, firstIds.Count) : requestedSize ?? firstIds.Count;
        var seenNext = new HashSet<string>(StringComparer.Ordinal);
        var loop = false;
        string? offOrigin = null;
        var endReason = "";
        var current = first;
        var currentResponse = response;
        var currentBody = body;

        while (true)
        {
            var last = pages[^1];
            if (last.Ids.Count == 0)
            {
                endReason = "empty page";
                break;
            }
            ApiRequest nextRequest;
            switch (strategy)
            {
                case PaginationStrategy.LinkHeader or PaginationStrategy.NextUrl:
                    if (last.Next is null)
                    {
                        endReason = "no next link";
                        goto done;
                    }
                    if (!seenNext.Add(last.Next))
                    {
                        loop = true;
                        endReason = "next link repeats";
                        goto done;
                    }
                    nextRequest = ProbeHelpers.WithUrl(current, last.Next, currentResponse.EffectiveUrl ?? report.Url);
                    // Never send the request's credentials to a host the server pointed at.
                    if (Uri.TryCreate(currentResponse.EffectiveUrl ?? report.Url, UriKind.Absolute, out var from)
                        && Uri.TryCreate(nextRequest.Url, UriKind.Absolute, out var to) && !ProbeHelpers.SameOrigin(from, to))
                    {
                        endReason = "next link to another origin";
                        offOrigin = to.GetLeftPart(UriPartial.Authority);
                        goto done;
                    }
                    break;
                case PaginationStrategy.Cursor:
                    if (last.Next is null)
                    {
                        endReason = "no next cursor";
                        goto done;
                    }
                    if (!seenNext.Add(last.Next))
                    {
                        loop = true;
                        endReason = "cursor repeats";
                        goto done;
                    }
                    nextRequest = ProbeHelpers.WithQuery(current, cursorParam!, Uri.EscapeDataString(last.Next));
                    break;
                case PaginationStrategy.Offset:
                    // Walk until a short or empty page, never until the advertised total: an under-reported total is a finding.
                    if (last.Ids.Count < pageSize)
                    {
                        endReason = "short page";
                        goto done;
                    }
                    offset += last.Ids.Count;
                    nextRequest = ProbeHelpers.WithQuery(current, pageParam!, offset.ToString(CultureInfo.InvariantCulture));
                    break;
                default:
                    if (last.Ids.Count < pageSize)
                    {
                        endReason = "short page";
                        goto done;
                    }
                    nextRequest = ProbeHelpers.WithQuery(current, pageParam!, (startPage + pages.Count).ToString(CultureInfo.InvariantCulture));
                    break;
            }
            if (pages.Count >= options.MaxPages)
            {
                endReason = "page cap";
                report.Stopped = true;
                break;
            }
            var number = pages.Count + 1;
            var nextSent = await session.SendAsync(nextRequest, $"page {number}").ConfigureAwait(false);
            if (nextSent is not { } nr)
            {
                endReason = "request cap";
                report.Stopped = true;
                break;
            }
            current = nextRequest;
            currentResponse = nr.Response;
            if (!currentResponse.HasResponse || currentResponse.StatusCode is < 200 or >= 300)
            {
                pages.Add(new Page(number, nr.Step.Url, currentResponse.StatusCode, [], currentResponse.Elapsed.TotalMilliseconds, null));
                endReason = currentResponse.HasResponse ? $"HTTP {currentResponse.StatusCode}" : "no response";
                report.Insights.Add(new ReportInsight(InsightLevel.Bad,
                    $"Page {number} failed ({(currentResponse.HasResponse ? $"HTTP {currentResponse.StatusCode}" : currentResponse.Error)}): clients can't read past page {number - 1}."));
                break;
            }
            currentBody = ProbeHelpers.TryJson(currentResponse.Body);
            var pageItems = FindItems(currentBody, itemsPath);
            var ids = pageItems is null ? [] : IdsOf(pageItems.Value.Items);
            pages.Add(new Page(number, nr.Step.Url, currentResponse.StatusCode, ids, currentResponse.Elapsed.TotalMilliseconds, NextOf(currentResponse, currentBody)));
            // A page identical to the previous one means the parameter is ignored (or a loop).
            if (ids.Count > 0 && ids.SequenceEqual(last.Ids) && guessed && number == 2)
            {
                notPaginated = true;
                pages.RemoveAt(pages.Count - 1);
                endReason = "not paginated";
                report.Insights.Add(new ReportInsight(InsightLevel.Info,
                    $"No pagination found: {pageParam}=2 returned the same items as the first page. If the endpoint pages another way, " +
                    "set the strategy and parameter names."));
                break;
            }
            if (ids.Count > 0 && ids.SequenceEqual(last.Ids))
            {
                loop = true;
                endReason = "page repeats";
                report.Insights.Add(new ReportInsight(InsightLevel.Bad,
                    $"Page {number} returned exactly the same items as page {number - 1}: the {(strategy is PaginationStrategy.Page or PaginationStrategy.Offset ? $"'{pageParam}' parameter seems to be ignored" : "next link or cursor leads back to the same page")}."));
                break;
            }
        }
        done:

        // ---- Analysis ----
        var walked = pages.Where(p => p.Status is >= 200 and < 300).ToList();
        var all = walked.SelectMany(p => p.Ids.Select(id => (Page: p.Number, Id: id))).ToList();
        var unique = all.Select(a => a.Id).Distinct().Count();
        var duplicates = all.GroupBy(a => a.Id).Where(g => g.Count() > 1).ToList();
        var complete = !report.Stopped && !loop && !partialStart && endReason is "empty page" or "no next link" or "no next cursor" or "short page" or "not paginated";

        var strategyText = strategy switch
        {
            PaginationStrategy.LinkHeader => "Link header",
            PaginationStrategy.NextUrl => "next URL in the body",
            PaginationStrategy.Cursor => $"cursor ({cursorPath} → ?{cursorParam}=)",
            PaginationStrategy.Offset => $"offset (?{pageParam}=)",
            _ => $"page number (?{pageParam}=)"
        };
        report.Stats.Add(new ProbeStat("Strategy", strategy.ToString(), strategyText));
        report.Stats.Add(new ProbeStat("Pages walked", walked.Count.ToString(CultureInfo.InvariantCulture), endReason));
        report.Stats.Add(new ProbeStat("Items", unique.ToString(CultureInfo.InvariantCulture), $"{all.Count} rows, page size {pageSize}"));
        report.Stats.Add(new ProbeStat("Duplicates", duplicates.Count.ToString(CultureInfo.InvariantCulture), null, duplicates.Count > 0));
        report.Stats.Add(new ProbeStat("Advertised total", total?.ToString(CultureInfo.InvariantCulture) ?? "—", totalSource,
            total is { } adv && complete ? adv != unique : null));
        report.Stats.Add(new ProbeStat("Identity", idField is null ? "whole item" : idField, $"items at {itemsPath}"));

        report.Details["strategy"] = strategy.ToString();
        report.Details["itemsPath"] = itemsPath;
        report.Details["idField"] = idField;
        report.Details["pageParam"] = pageParam;
        report.Details["sizeParam"] = sizeParam;
        report.Details["cursorParam"] = cursorParam;
        report.Details["cursorPath"] = cursorPath;
        report.Details["pageSize"] = pageSize;
        report.Details["advertisedTotal"] = total;
        report.Details["pages"] = walked.Count;
        report.Details["items"] = unique;
        report.Details["duplicates"] = new JsonArray(duplicates.Take(100).Select(g => (JsonNode)new JsonObject
        {
            ["id"] = g.Key, ["pages"] = new JsonArray(g.Select(x => (JsonNode)JsonValue.Create(x.Page)).ToArray())
        }).ToArray());
        report.Details["complete"] = complete;

        if (offOrigin is not null)
            report.Insights.Add(new ReportInsight(InsightLevel.Warning,
                $"The next link after page {walked.Count} points to another origin ({offOrigin}). It wasn't followed, so the request's credentials " +
                "aren't sent there; totals are not compared. Clients that follow it hand their credentials to that host unless they strip them."));
        if (partialStart)
            report.Insights.Add(new ReportInsight(InsightLevel.Info,
                $"The walk started at {pageParam}={(strategy == PaginationStrategy.Page ? startPage : offset)}, not the first page, so totals and page counts " +
                "are not compared. Remove the parameter to walk the whole collection."));
        if (walked.Count(p => p.Ids.Count > 0) == 1 && complete && !notPaginated && strategy is PaginationStrategy.Page or PaginationStrategy.Offset)
            report.Insights.Add(new ReportInsight(InsightLevel.Info,
                $"Everything fits on one page ({firstIds.Count} items). Lower the page size (e.g. --page-size 2) to exercise the paging."));

        if (duplicates.Count > 0)
        {
            var examples = string.Join("; ", duplicates.Take(3).Select(g => $"{g.Key} on pages {string.Join(", ", g.Select(x => x.Page))}"));
            report.Insights.Add(new ReportInsight(InsightLevel.Bad,
                $"{duplicates.Count} item(s) appear on more than one page ({examples}). Clients that page through will show or process them twice; " +
                "usually the sort order is not unique or not stable."));
        }
        else if (walked.Count > 1)
            report.Insights.Add(new ReportInsight(InsightLevel.Good, $"No duplicates across {walked.Count} pages ({unique} items)."));

        if (loop && !report.Insights.Any(i => i.Text.Contains("same items", StringComparison.Ordinal)))
            report.Insights.Add(new ReportInsight(InsightLevel.Bad, $"Pagination loops ({endReason}): a client following it never finishes."));

        if (total is { } advertised && complete)
        {
            if (advertised != unique && !(totalSource == "$.count" && advertised == firstIds.Count))
                report.Insights.Add(new ReportInsight(InsightLevel.Bad,
                    $"The advertised total ({advertised}, from {totalSource}) doesn't match the {unique} distinct items found by walking every page" +
                    (advertised > unique ? $": {advertised - unique} item(s) can't be reached." : ".")));
            else if (advertised == unique)
                report.Insights.Add(new ReportInsight(InsightLevel.Good, $"The advertised total ({advertised}) matches the items found."));
        }
        if (totalPages is { } pagesAdvertised && complete && strategy == PaginationStrategy.Page && pagesAdvertised != walked.Count(p => p.Ids.Count > 0))
            report.Insights.Add(new ReportInsight(InsightLevel.Warning,
                $"The response says there are {pagesAdvertised} pages, but {walked.Count(p => p.Ids.Count > 0)} had items."));

        // Short pages in the middle: items may be skipped (or filtered after paging).
        var shortMiddle = walked.Take(Math.Max(0, walked.Count - 1)).Where(p => p.Ids.Count > 0 && p.Ids.Count < pageSize).Select(p => p.Number).ToList();
        if (shortMiddle.Count > 0 && strategy is PaginationStrategy.LinkHeader or PaginationStrategy.NextUrl or PaginationStrategy.Cursor)
            report.Insights.Add(new ReportInsight(InsightLevel.Warning,
                $"Page(s) {string.Join(", ", shortMiddle.Take(8))} returned fewer than {pageSize} items but weren't the last: items may be filtered after paging, " +
                "so clients that stop at a short page miss data."));
        var oversize = walked.Where(p => requestedSize is { } req && p.Ids.Count > req).Select(p => p.Number).ToList();
        if (oversize.Count > 0)
            report.Insights.Add(new ReportInsight(InsightLevel.Bad,
                $"Page(s) {string.Join(", ", oversize.Take(8))} returned more items than the requested {sizeParam}={requestedSize}."));

        var lastWalked = walked.LastOrDefault();
        if (lastWalked is not null && lastWalked.Ids.Count == 0 && walked.Count > 1 && strategy is PaginationStrategy.LinkHeader or PaginationStrategy.NextUrl or PaginationStrategy.Cursor)
            report.Insights.Add(new ReportInsight(InsightLevel.Info,
                "The last next link or cursor led to an empty page: clients make one extra request at the end."));

        // Deep pages slower than the first ones: often OFFSET scans.
        if (walked.Count >= 6)
        {
            var head = walked.Take(3).Average(p => p.LatencyMs);
            var tail = walked.TakeLast(3).Average(p => p.LatencyMs);
            if (tail > Math.Max(head * 3, head + 150))
                report.Insights.Add(new ReportInsight(InsightLevel.Warning,
                    $"Later pages are much slower ({tail:0} ms vs {head:0} ms for the first pages). Offset paging that scans skipped rows gets slower " +
                    "the deeper a client goes; a cursor (keyset) avoids that."));
        }
        if (strategy == PaginationStrategy.Offset && walked.Count > 1)
            report.Insights.Add(new ReportInsight(InsightLevel.Info,
                "Offset paging skips or repeats items when rows are inserted or deleted during a walk; cursor paging is stable under writes."));

        // ---- Stability: page 1 again ----
        if (options.CheckStability && walked.Count > 0 && firstIds.Count > 1)
        {
            var again = await session.SendAsync(first, "page 1 again (stability)").ConfigureAwait(false);
            if (again is { } a && a.Response.StatusCode is >= 200 and < 300 && FindItems(ProbeHelpers.TryJson(a.Response.Body), itemsPath) is { } againItems)
            {
                var againIds = IdsOf(againItems.Items);
                if (!againIds.SequenceEqual(firstIds))
                {
                    var sameSet = againIds.Order().SequenceEqual(firstIds.Order());
                    report.Insights.Add(new ReportInsight(sameSet ? InsightLevel.Warning : InsightLevel.Info,
                        sameSet
                            ? "Fetching page 1 again returned the same items in a different order: the sort is not deterministic, so pages can overlap or skip items. Add a unique tie-breaker (e.g. id) to the sort."
                            : "Fetching page 1 again returned different items. The data may have changed during the walk; if not, the order is not deterministic."));
                }
                else
                    report.Insights.Add(new ReportInsight(InsightLevel.Good, "Page 1 is stable: fetching it again returned the same items in the same order."));
            }
        }

        // ---- Edge cases ----
        if (options.EdgeCases && strategy is PaginationStrategy.Page or PaginationStrategy.Offset && complete && !notPaginated)
        {
            var beyondValue = strategy == PaginationStrategy.Page ? startPage + walked.Count + 5 : offset + Math.Max(pageSize, 1) * 10 + total.GetValueOrDefault();
            await EdgeAsync(session, report, ProbeHelpers.WithQuery(first, pageParam!, beyondValue.ToString(CultureInfo.InvariantCulture)),
                $"{pageParam}={beyondValue} (past the end)", itemsPath, expectEmpty: true).ConfigureAwait(false);
            await EdgeAsync(session, report, ProbeHelpers.WithQuery(first, pageParam!, "-1"), $"{pageParam}=-1", itemsPath, expectEmpty: false).ConfigureAwait(false);
            await EdgeAsync(session, report, ProbeHelpers.WithQuery(first, pageParam!, "abc"), $"{pageParam}=abc", itemsPath, expectEmpty: false).ConfigureAwait(false);
        }
        if (options.EdgeCases && sizeParam is not null && complete && !notPaginated)
        {
            await EdgeAsync(session, report, ProbeHelpers.WithQuery(first, sizeParam, "0"), $"{sizeParam}=0", itemsPath, expectEmpty: false).ConfigureAwait(false);
            var huge = await session.SendAsync(ProbeHelpers.WithQuery(first, sizeParam, "100000"), $"{sizeParam}=100000").ConfigureAwait(false);
            if (huge is { } h)
            {
                var hugeItems = FindItems(ProbeHelpers.TryJson(h.Response.Body), itemsPath);
                if (h.Response.StatusCode >= 500 || !h.Response.HasResponse)
                    report.Insights.Add(new ReportInsight(InsightLevel.Bad, $"{sizeParam}=100000 crashed the endpoint ({h.Step.StatusText}); validate and cap the page size."));
                else if (hugeItems is { } hi && hi.Items.Count > Math.Max(1000, pageSize * 20))
                    report.Insights.Add(new ReportInsight(InsightLevel.Warning,
                        $"{sizeParam}=100000 returned {hi.Items.Count} items in one response: the page size is not capped, so one request can fetch the whole table."));
                else if (h.Response.StatusCode is >= 200 and < 300 && hugeItems is { } capped)
                    report.Insights.Add(new ReportInsight(InsightLevel.Good, $"{sizeParam}=100000 is capped ({capped.Items.Count} items returned)."));
                else if (h.Response.StatusCode is >= 400 and < 500)
                    report.Insights.Add(new ReportInsight(InsightLevel.Good, $"{sizeParam}=100000 is rejected with HTTP {h.Response.StatusCode}."));
            }
        }

        report.Summary = report.Error ?? (notPaginated
            ? $"{unique} items on one page; no pagination detected"
            : loop
            ? $"Pagination loops after {walked.Count} pages ({strategyText})"
            : $"{unique} items over {walked.Count} page(s) by {strategyText}" +
              (duplicates.Count > 0 ? $", {duplicates.Count} duplicate(s)" : "") +
              (total is { } tt && complete && tt != unique ? $", total says {tt}" : ""));
    }

    private static async Task EdgeAsync(ProbeSession session, ProbeReport report, ApiRequest request, string label, string itemsPath, bool expectEmpty)
    {
        var sent = await session.SendAsync(request, label).ConfigureAwait(false);
        if (sent is not { } s)
            return;
        var r = s.Response;
        if (!r.HasResponse || r.StatusCode >= 500)
        {
            report.Insights.Add(new ReportInsight(InsightLevel.Bad, $"{label} crashed the endpoint ({s.Step.StatusText}); invalid paging input should be a 400 or an empty page."));
            return;
        }
        if (expectEmpty && r.StatusCode is >= 200 and < 300)
        {
            var items = FindItems(ProbeHelpers.TryJson(r.Body), itemsPath);
            if (items is { } i && i.Items.Count > 0)
                report.Insights.Add(new ReportInsight(InsightLevel.Warning,
                    $"{label} still returned {i.Items.Count} items: out-of-range pages should be empty, otherwise clients that stop on an empty page never stop."));
            else
                report.Insights.Add(new ReportInsight(InsightLevel.Good, $"{label} returns an empty page."));
        }
        else if (expectEmpty && r.StatusCode == 404)
            report.Insights.Add(new ReportInsight(InsightLevel.Info, $"{label} answers 404; an empty page (200) is easier for clients to handle."));
    }
}
