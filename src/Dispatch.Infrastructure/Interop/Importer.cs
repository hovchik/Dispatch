using System.Text.Json;
using System.Text.Json.Nodes;
using Dispatch.Application.Grpc;
using Dispatch.Application.Interop;
using Dispatch.Application.Soap;
using Dispatch.Domain;
using Dispatch.Infrastructure.Http;
using Dispatch.Infrastructure.Protocols;

namespace Dispatch.Infrastructure.Interop;

/// <summary>
/// Imports anything a developer or QA is likely to have: Dispatch files/folders, Postman collections and environments,
/// Insomnia exports, HAR, OpenAPI/Swagger (JSON or YAML), WSDL, .proto, .http files and cURL commands.
/// The format is detected from the content.
/// </summary>
public sealed class Importer(IHttpClientSource clients, WsdlLoader wsdlLoader)
{
    public async Task<ImportResult> ImportPathAsync(string path, CancellationToken ct = default)
    {
        if (Directory.Exists(path))
            return DispatchFormat.ImportFolder(path);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Not found: {path}");

        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".proto")
            return ImportProto([path]);
        if (extension == ".wsdl")
            return await ImportWsdlAsync(path, ct).ConfigureAwait(false);

        var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        return await ImportTextAsync(text, Path.GetFileNameWithoutExtension(path), Path.GetFullPath(path), ct).ConfigureAwait(false);
    }

    public async Task<ImportResult> ImportUrlAsync(string url, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var client = clients.GetClient(new RequestSettings());
        using var response = await client.GetAsync(url, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        if (IsWsdl(text) || url.EndsWith("?wsdl", StringComparison.OrdinalIgnoreCase))
            return await ImportWsdlAsync(url, ct).ConfigureAwait(false);
        return await ImportTextAsync(text, new Uri(url).Segments.LastOrDefault()?.Trim('/') ?? "Imported", url, ct).ConfigureAwait(false);
    }

    /// <param name="location">Where the text came from (a file path or URL), used by OpenAPI contract tests.</param>
    public Task<ImportResult> ImportTextAsync(string text, string name = "Imported", string? location = null, CancellationToken ct = default) =>
        Task.FromResult(ImportText(text, name, location));

    public static ImportResult ImportText(string text, string name = "Imported", string? location = null)
    {
        var trimmed = text.TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (trimmed.Length == 0)
            throw new FormatException("Nothing to import.");

        if (Curl.LooksLikeCurl(trimmed))
        {
            var collection = new RequestCollection { Name = "cURL import" };
            foreach (var command in SplitCurlCommands(trimmed))
            {
                var request = Curl.Parse(command);
                request.CollectionId = collection.Id;
                request.SortOrder = collection.Requests.Count;
                collection.Requests.Add(request);
            }
            return ImportResult.Single("cURL", collection);
        }

        if (IsWsdl(trimmed))
        {
            var wsdl = WsdlDocument.Parse(trimmed);
            return FromWsdl(wsdl, name);
        }

        if (trimmed.Contains("syntax", StringComparison.Ordinal) && trimmed.Contains("service ", StringComparison.Ordinal)
                                                                  && trimmed.Contains("rpc ", StringComparison.Ordinal))
        {
            var file = ProtoParser.Parse(trimmed, name + ".proto");
            return FromProto([file], [file], []);
        }

        JsonNode? json = null;
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                json = JsonNode.Parse(trimmed, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            }
            catch (JsonException ex)
            {
                throw new FormatException($"The JSON could not be parsed: {ex.Message}");
            }
        }
        else if (HttpFile.LooksLikeHttpFile(trimmed))
        {
            return HttpFile.Import(trimmed, name);
        }
        else if (trimmed.Contains(':'))
        {
            json = Yaml.ToJson(trimmed);
        }

        if (json is null)
            throw new FormatException("Unrecognized format. Supported: Dispatch, Postman, Insomnia, HAR, OpenAPI/Swagger, WSDL, .proto, .http, cURL.");

        if (DispatchFormat.IsDispatchJson(json))
            return DispatchFormat.Import(trimmed);
        if (OpenApi.IsOpenApi(json))
            return OpenApi.Import(json, location);
        if (Postman.IsCollection(json) || Postman.IsEnvironment(json))
            return Postman.Import(json);
        if (Insomnia.IsExport(json))
            return Insomnia.Import(json);
        if (Har.IsHar(json))
            return Har.Import(json);
        throw new FormatException("Unrecognized JSON/YAML document. Supported: Dispatch, Postman, Insomnia, HAR, OpenAPI/Swagger.");
    }

    public async Task<ImportResult> ImportWsdlAsync(string location, CancellationToken ct)
    {
        var wsdl = await wsdlLoader.LoadAsync(location, new RequestSettings(), refresh: true, ct).ConfigureAwait(false);
        var result = FromWsdl(wsdl, Path.GetFileNameWithoutExtension(location.Split('?')[0]));
        foreach (var request in result.Collections[0].Requests)
            request.Protocol.Soap.WsdlUrl = location;
        return result;
    }

    private static ImportResult FromWsdl(WsdlDocument wsdl, string name)
    {
        var collection = new RequestCollection { Name = wsdl.Operations.FirstOrDefault()?.ServiceName ?? name };
        foreach (var operation in wsdl.Operations)
        {
            collection.Requests.Add(new ApiRequest
            {
                Kind = RequestKind.Soap,
                Name = operation.Name,
                Folder = $"{operation.PortName}",
                Method = HttpVerb.Post,
                Url = operation.Endpoint,
                CollectionId = collection.Id,
                SortOrder = collection.Requests.Count,
                Body = new RequestBody { Mode = BodyMode.Xml, Content = wsdl.BuildEnvelope(operation) },
                Protocol = new ProtocolSettings
                {
                    Soap = new SoapSettings { Version = operation.Version, Action = operation.SoapAction, Operation = operation.Name }
                },
                Assertions = [new Assertion { Source = ValueSource.Status, Expected = "200" }]
            });
        }
        return ImportResult.Single("WSDL", collection);
    }

    public static ImportResult ImportProto(IReadOnlyList<string> paths, IReadOnlyList<string>? importPaths = null)
    {
        var files = ProtoParser.ParseFiles(paths, importPaths ?? []);
        // Services come from the files the user chose, not from their imports.
        var roots = files.Where(f => paths.Any(p => Path.GetFileName(p) == f.Name)).ToList();
        return FromProto(roots.Count > 0 ? roots : [files[0]], files, paths.Select(Path.GetFullPath).ToList(), importPaths);
    }

    private static ImportResult FromProto(IReadOnlyList<FileDesc> roots, IReadOnlyList<FileDesc> allFiles, List<string> protoPaths,
        IReadOnlyList<string>? importPaths = null)
    {
        var schema = ProtoSchema.Link(allFiles.Concat(WellKnownProtos.Parsed()));
        var codec = new ProtoJson(schema);
        var first = roots[0];
        var collection = new RequestCollection { Name = first.Package.Length > 0 ? first.Package : Path.GetFileNameWithoutExtension(first.Name) };
        collection.Variables.Add(new KeyValueItem("grpcHost", "localhost:50051"));

        foreach (var service in roots.SelectMany(f => f.Services))
        {
            foreach (var method in service.Methods)
            {
                var template = codec.Template(method.InputType);
                collection.Requests.Add(new ApiRequest
                {
                    Kind = RequestKind.Grpc,
                    Name = method.Name,
                    Folder = service.Name,
                    Url = "{{grpcHost}}",
                    CollectionId = collection.Id,
                    SortOrder = collection.Requests.Count,
                    Description = $"{service.FullName}/{method.Name} ({method.Kind})",
                    Protocol = new ProtocolSettings
                    {
                        Grpc = new GrpcSettings
                        {
                            SchemaSource = protoPaths.Count > 0 ? GrpcSchemaSource.ProtoFiles : GrpcSchemaSource.ServerReflection,
                            ProtoFiles = protoPaths,
                            ImportPaths = importPaths?.ToList() ?? [],
                            Service = service.FullName,
                            Method = method.Name,
                            Message = method.ClientStreaming ? $"[\n{template}\n]" : template
                        }
                    },
                    Assertions = [new Assertion { Source = ValueSource.Status, Expected = "0" }]
                });
            }
        }
        return ImportResult.Single(".proto", collection);
    }

    private static bool IsWsdl(string text) =>
        text.TrimStart().StartsWith('<') && text.Contains("schemas.xmlsoap.org/wsdl/", StringComparison.Ordinal)
                                       && text.Contains("definitions", StringComparison.Ordinal);

    /// <summary>Several pasted curl commands (one per line or separated by blank lines) become several requests.</summary>
    private static IEnumerable<string> SplitCurlCommands(string text)
    {
        var joined = text.Replace("\\\r\n", " ").Replace("\\\n", " ").Replace("^\r\n", " ").Replace("^\n", " ");
        return joined.Split('\n').Select(l => l.Trim()).Where(l => Curl.LooksLikeCurl(l));
    }
}
