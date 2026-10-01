using Dispatch.Application.Help;
using Dispatch.Application.Interop;
using Dispatch.Domain;

namespace Dispatch.Tests;

public class HelpTests
{
    [Fact]
    public void Topic_ids_are_unique_and_links_resolve()
    {
        var ids = HelpCatalog.All.Select(t => t.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());

        var samples = SampleCollection.Create().Requests.Select(r => r.Name).ToHashSet();
        foreach (var topic in HelpCatalog.All)
        {
            Assert.NotEmpty(topic.Blocks);
            Assert.All(topic.Related ?? [], id => Assert.NotNull(HelpCatalog.Find(id)));
            if (topic.TryIt is not null)
                Assert.Contains(topic.TryIt, samples);
        }
    }

    [Fact]
    public void Search_matches_title_keywords_and_body()
    {
        Assert.Equal(HelpCatalog.All.Count, HelpCatalog.Search("  ").Count());
        Assert.Contains(HelpCatalog.Search("oauth"), t => t.Id == HelpCatalog.Auth);
        Assert.Contains(HelpCatalog.Search("csv data"), t => t.Id == HelpCatalog.Runner);
        Assert.Contains(HelpCatalog.Search("pm.expect"), t => t.Id == HelpCatalog.Scripts);
        Assert.Empty(HelpCatalog.Search("zzz-no-such-topic"));
    }

    [Fact]
    public void Sample_collection_is_self_contained_and_round_trips()
    {
        var collection = SampleCollection.Create();

        Assert.All(collection.Requests, r => Assert.Equal(collection.Id, r.CollectionId));
        Assert.All(collection.Requests, r => Assert.False(string.IsNullOrWhiteSpace(r.Description)));
        Assert.Equal(collection.Requests.Count, collection.Requests.Select(r => r.Name).Distinct().Count());
        Assert.Contains(collection.Requests, r => r.Extractions.Count > 0);
        Assert.Contains(collection.Requests, r => r.TestScript.Length > 0);

        // Every {{variable}} used is defined by the collection, extracted by an earlier request, set by a script,
        // or is a dynamic value.
        var known = collection.Variables.Select(v => v.Key)
            .Concat(collection.Requests.SelectMany(r => r.Extractions).Select(e => e.Variable))
            .Append("traceId").ToHashSet();
        var used = collection.Requests
            .SelectMany(r => new[] { r.Url, r.Body.Content }.Concat(r.Headers.Select(h => h.Value)).Concat(r.QueryParams.Select(p => p.Value)))
            .SelectMany(s => System.Text.RegularExpressions.Regex.Matches(s, @"\{\{([^}$][^}]*)\}\}").Select(m => m.Groups[1].Value));
        Assert.All(used, name => Assert.Contains(name, known));

        var reimported = DispatchFormat.Import(DispatchFormat.ExportCollection(collection));
        Assert.Equal(collection.Requests.Count, reimported.Collections.Single().Requests.Count);
    }
}
