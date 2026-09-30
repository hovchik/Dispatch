using System.Collections.Concurrent;
using Dispatch.Application.Auth;
using Dispatch.Application.Grpc;
using Dispatch.Domain;
using Dispatch.Infrastructure.Http;

namespace Dispatch.Infrastructure.Protocols.Grpc;

/// <summary>
/// Loads (and caches) the protobuf schema for a gRPC request, from .proto files or from the server's reflection
/// service (grpc.reflection.v1, falling back to v1alpha).
/// </summary>
public sealed class GrpcSchemaProvider(IHttpClientSource clients)
{
    private static readonly string[] ReflectionServices =
    [
        "grpc.reflection.v1.ServerReflection",
        "grpc.reflection.v1alpha.ServerReflection"
    ];

    private readonly ConcurrentDictionary<string, ProtoSchema> _cache = new();

    public async Task<ProtoSchema> GetSchemaAsync(ApiRequest request, bool refresh, CancellationToken ct)
    {
        var settings = request.Protocol.Grpc;
        var key = settings.SchemaSource == GrpcSchemaSource.ProtoFiles
            ? "files|" + string.Join("|", settings.ProtoFiles.Select(f => $"{f}@{(File.Exists(f) ? File.GetLastWriteTimeUtc(f).Ticks : 0)}"))
              + "|" + string.Join("|", settings.ImportPaths)
            : "reflection|" + GrpcCall.BaseUri(request.Url, settings.UseTls);

        if (!refresh && _cache.TryGetValue(key, out var cached))
            return cached;

        var schema = settings.SchemaSource == GrpcSchemaSource.ProtoFiles
            ? LoadFromFiles(settings)
            : await ReflectAsync(request, ct).ConfigureAwait(false);
        _cache[key] = schema;
        return schema;
    }

    public void Invalidate() => _cache.Clear();

    public static ProtoSchema LoadFromFiles(GrpcSettings settings)
    {
        if (settings.ProtoFiles.Count == 0)
            throw new ArgumentException("Add at least one .proto file, or use server reflection.");
        var files = ProtoParser.ParseFiles(settings.ProtoFiles, settings.ImportPaths);
        return ProtoSchema.Link(files.Concat(WellKnownProtos.Parsed()));
    }

    private async Task<ProtoSchema> ReflectAsync(ApiRequest request, CancellationToken ct)
    {
        GrpcException? last = null;
        foreach (var service in ReflectionServices)
        {
            try
            {
                return await ReflectWithAsync(request, service, ct).ConfigureAwait(false);
            }
            catch (GrpcException ex) when (ex.Status == 12) // UNIMPLEMENTED: try the older version
            {
                last = ex;
            }
        }
        throw new InvalidOperationException(
            "The server does not support gRPC reflection. Load the service's .proto files instead." +
            (last is null ? "" : $" ({last.Message})"));
    }

    private async Task<ProtoSchema> ReflectWithAsync(ApiRequest request, string reflectionService, CancellationToken ct)
    {
        var baseUri = GrpcCall.BaseUri(request.Url, request.Protocol.Grpc.UseTls);
        await using var call = GrpcCall.Start(clients, request.Settings, baseUri, $"{reflectionService}/ServerReflectionInfo",
            AuthHeaders.Combined(request), TimeSpan.FromSeconds(30), ct);
        await using var responses = call.ReadResponsesAsync(ct).GetAsyncEnumerator(ct);

        async Task<ReflectionResponse> Ask(int field, string value)
        {
            var writer = new ProtoWriter();
            writer.WriteTag(field, WireType.LengthDelimited);
            writer.WriteString(value);
            await call.WriteAsync(writer.ToArray()).ConfigureAwait(false);
            if (!await responses.MoveNextAsync().ConfigureAwait(false))
            {
                var (code, message, _) = call.GetStatus();
                throw new GrpcException(code, code == 0 ? "Reflection stream ended unexpectedly." : $"{GrpcCall.StatusName(code)}: {message}");
            }
            return ReflectionResponse.Parse(responses.Current);
        }

        var services = await Ask(7, "*").ConfigureAwait(false);
        services.ThrowIfError();

        var files = new Dictionary<string, FileDesc>(StringComparer.Ordinal);
        foreach (var name in services.ServiceNames.Where(n => !n.StartsWith("grpc.reflection.", StringComparison.Ordinal)))
        {
            var response = await Ask(4, name).ConfigureAwait(false);
            response.ThrowIfError();
            foreach (var bytes in response.FileDescriptors)
            {
                var file = DescriptorReader.ReadFile(bytes);
                files.TryAdd(file.Name, file);
            }
        }

        // Fetch dependencies the server didn't include (well-known types are built in).
        var pending = new Queue<string>(files.Values.SelectMany(f => f.Dependencies));
        while (pending.Count > 0)
        {
            var dependency = pending.Dequeue();
            if (files.ContainsKey(dependency) || WellKnownProtos.Sources.ContainsKey(dependency))
                continue;
            var response = await Ask(3, dependency).ConfigureAwait(false);
            if (response.ErrorCode is not null)
                continue; // best effort: types from it will fail to link with a clear message
            foreach (var bytes in response.FileDescriptors)
            {
                var file = DescriptorReader.ReadFile(bytes);
                if (files.TryAdd(file.Name, file))
                    foreach (var next in file.Dependencies)
                        pending.Enqueue(next);
            }
        }

        call.CompleteRequests();
        return ProtoSchema.Link(files.Values.Concat(WellKnownProtos.Parsed()));
    }

    private sealed class ReflectionResponse
    {
        public List<string> ServiceNames { get; } = [];
        public List<byte[]> FileDescriptors { get; } = [];
        public int? ErrorCode { get; private set; }
        public string ErrorMessage { get; private set; } = "";

        public void ThrowIfError()
        {
            if (ErrorCode is { } code)
                throw new GrpcException(code, $"Reflection error {GrpcCall.StatusName(code)}: {ErrorMessage}");
        }

        public static ReflectionResponse Parse(byte[] data)
        {
            var result = new ReflectionResponse();
            var reader = new ProtoReader(data);
            while (!reader.End)
            {
                var (field, wire) = reader.ReadTag();
                switch (field)
                {
                    case 4: // file_descriptor_response
                        var fdr = new ProtoReader(reader.ReadLengthDelimited());
                        while (!fdr.End)
                        {
                            var (f, w) = fdr.ReadTag();
                            if (f == 1) result.FileDescriptors.Add(fdr.ReadLengthDelimited().ToArray());
                            else fdr.Skip(w, f);
                        }
                        break;
                    case 6: // list_services_response
                        var lsr = new ProtoReader(reader.ReadLengthDelimited());
                        while (!lsr.End)
                        {
                            var (f, w) = lsr.ReadTag();
                            if (f != 1)
                            {
                                lsr.Skip(w, f);
                                continue;
                            }
                            var svc = new ProtoReader(lsr.ReadLengthDelimited());
                            while (!svc.End)
                            {
                                var (sf, sw) = svc.ReadTag();
                                if (sf == 1) result.ServiceNames.Add(svc.ReadString());
                                else svc.Skip(sw, sf);
                            }
                        }
                        break;
                    case 7: // error_response
                        var err = new ProtoReader(reader.ReadLengthDelimited());
                        result.ErrorCode = 2;
                        while (!err.End)
                        {
                            var (f, w) = err.ReadTag();
                            if (f == 1) result.ErrorCode = (int)err.ReadVarint();
                            else if (f == 2) result.ErrorMessage = err.ReadString();
                            else err.Skip(w, f);
                        }
                        break;
                    default:
                        reader.Skip(wire, field);
                        break;
                }
            }
            return result;
        }
    }
}
