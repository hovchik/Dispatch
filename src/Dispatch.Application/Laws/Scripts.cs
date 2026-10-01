using System.Text;
using System.Text.Json;
using Dispatch.Application.Impact;

namespace Dispatch.Application.Laws;

/// <summary>
/// Generates <c>pm.test</c> snippets that check a law on every response. Each snippet is self-contained (its own helper
/// inside the test callback), so several can be appended to one test script. Failures throw with a message naming the
/// offending value, which pm.test reports.
/// </summary>
internal static class Scripts
{
    /// <summary>JS: all values at a path, where "*" spreads arrays: pick(json, ["items","*","price"]).</summary>
    private const string Pick =
        "const pick = (v, path) => path.reduce((acc, k) => acc.flatMap(x => x == null ? [] : k === \"*\" ? (Array.isArray(x) ? x : []) : (typeof x === \"object\" && k in x ? [x[k]] : [])), [v]);";

    private static string Segments(IEnumerable<string> path) => JsonSerializer.Serialize(path.ToArray());

    private static string Test(string name, string body)
    {
        var sb = new StringBuilder();
        sb.Append("pm.test(").Append(JsonSerializer.Serialize("API law: " + name)).Append(", () => {\n");
        foreach (var line in body.Split('\n'))
            sb.Append("    ").Append(line).Append('\n');
        sb.Append("});");
        return sb.ToString();
    }

    /// <summary>Every object at <paramref name="container"/> has these fields with these types.</summary>
    public static string Shape(IReadOnlyList<string> container, IReadOnlyList<(string Key, string Type)> fields)
    {
        var spec = JsonSerializer.Serialize(fields.ToDictionary(f => f.Key, f => f.Type));
        return Test($"{JsonShape.Join(container)} has its usual fields",
            $$"""
              {{Pick}}
              const spec = {{spec}};
              const typeOf = v => Array.isArray(v) ? "array" : v === null ? "null" : typeof v;
              for (const o of pick(pm.response.json(), {{Segments(container)}})) {
                  for (const [k, t] of Object.entries(spec)) {
                      if (!(k in o)) throw new Error(`missing ${k}`);
                      if (o[k] !== null && typeOf(o[k]) !== t) throw new Error(`${k} is ${typeOf(o[k])}, expected ${t}`);
                  }
              }
              """);
    }

    /// <summary>Every value at <paramref name="path"/> satisfies a JS predicate over <c>v</c>.</summary>
    public static string Each(IReadOnlyList<string> path, string what, string predicate) =>
        Test($"{JsonShape.Join(path)} {what}",
            $$"""
              {{Pick}}
              for (const v of pick(pm.response.json(), {{Segments(path)}})) {
                  if (v !== null && !({{predicate}})) throw new Error(`unexpected value ${JSON.stringify(v)}`);
              }
              """);

    /// <summary>Every object at <paramref name="container"/> satisfies a JS predicate over <c>o</c>.</summary>
    public static string PerObject(IReadOnlyList<string> container, string what, string predicate) =>
        Test($"{JsonShape.Join(container)}: {what}",
            $$"""
              {{Pick}}
              for (const o of pick(pm.response.json(), {{Segments(container)}})) {
                  if (!({{predicate}})) throw new Error(`broken for ${JSON.stringify(o).slice(0, 200)}`);
              }
              """);

    /// <summary>The array at <paramref name="arrayPath"/> is no longer than the query parameter.</summary>
    public static string PageSize(string arrayPath, string param)
    {
        var segments = arrayPath == "$" ? "[]" : Segments(JsonShape.Split(arrayPath));
        return Test($"{arrayPath} respects ?{param}=",
            $$"""
              {{Pick}}
              const m = String(pm.request.url).match(/[?&]{{System.Text.RegularExpressions.Regex.Escape(param)}}=(\d+)/);
              const items = pick(pm.response.json(), {{segments}})[0];
              if (m && Array.isArray(items) && items.length > Number(m[1])) throw new Error(`${items.length} items for {{param}}=${m[1]}`);
              """);
    }

    /// <summary>The submitted fields come back unchanged (at the root or under one wrapper object).</summary>
    public static string Echo(IReadOnlyList<string> fields) =>
        Test($"returns the submitted {string.Join(", ", fields)}",
            $$"""
              const sent = JSON.parse(pm.request.body.raw || "{}");
              const got = pm.response.json();
              const wrappers = Object.values(got).filter(v => v && typeof v === "object" && !Array.isArray(v));
              const target = k => k in got ? got : wrappers.length === 1 ? wrappers[0] : {};
              for (const k of {{JsonSerializer.Serialize(fields.ToArray())}}) {
                  if (k in sent && JSON.stringify(target(k)[k]) !== JSON.stringify(sent[k]))
                      throw new Error(`${k}: sent ${JSON.stringify(sent[k])}, got ${JSON.stringify(target(k)[k])}`);
              }
              """);
}
