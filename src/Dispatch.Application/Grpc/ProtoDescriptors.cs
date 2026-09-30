namespace Dispatch.Application.Grpc;

/// <summary>Field types, numbered as in google/protobuf/descriptor.proto.</summary>
public enum ProtoType
{
    Double = 1,
    Float = 2,
    Int64 = 3,
    UInt64 = 4,
    Int32 = 5,
    Fixed64 = 6,
    Fixed32 = 7,
    Bool = 8,
    String = 9,
    Group = 10,
    Message = 11,
    Bytes = 12,
    UInt32 = 13,
    Enum = 14,
    SFixed32 = 15,
    SFixed64 = 16,
    SInt32 = 17,
    SInt64 = 18
}

public enum ProtoLabel
{
    Optional = 1,
    Required = 2,
    Repeated = 3
}

/// <summary>A field as declared; <see cref="TypeName"/> becomes fully qualified (no leading dot) once linked.</summary>
public sealed class FieldDesc
{
    public string Name { get; set; } = "";
    public int Number { get; set; }
    public ProtoLabel Label { get; set; } = ProtoLabel.Optional;
    public ProtoType Type { get; set; }
    public string? TypeName { get; set; }
    public string? JsonName { get; set; }
    public int? OneofIndex { get; set; }
    public bool? Packed { get; set; }
    public bool Proto3Optional { get; set; }
    public string? DefaultValue { get; set; }

    public bool IsRepeated => Label == ProtoLabel.Repeated;
    public bool IsMessage => Type is ProtoType.Message or ProtoType.Group;

    public string EffectiveJsonName => string.IsNullOrEmpty(JsonName) ? ToJsonName(Name) : JsonName;

    /// <summary>protoc's lowerCamelCase conversion: underscores removed, following letter upper-cased.</summary>
    public static string ToJsonName(string name)
    {
        var chars = new System.Text.StringBuilder(name.Length);
        var upper = false;
        foreach (var c in name)
        {
            if (c == '_')
            {
                upper = true;
                continue;
            }
            chars.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }
        return chars.ToString();
    }
}

public sealed class MessageDesc
{
    public string Name { get; set; } = "";
    public string FullName { get; set; } = "";
    public List<FieldDesc> Fields { get; } = [];
    public List<MessageDesc> NestedTypes { get; } = [];
    public List<EnumDesc> EnumTypes { get; } = [];
    public List<string> Oneofs { get; } = [];
    public bool IsMapEntry { get; set; }

    public FieldDesc? FieldByNumber(int number) => Fields.FirstOrDefault(f => f.Number == number);
}

public sealed class EnumDesc
{
    public string Name { get; set; } = "";
    public string FullName { get; set; } = "";
    public List<(string Name, int Number)> Values { get; } = [];

    public string? NameOf(int number)
    {
        foreach (var (name, n) in Values)
            if (n == number)
                return name;
        return null;
    }
}

public sealed class MethodDesc
{
    public string Name { get; set; } = "";
    public string InputType { get; set; } = "";
    public string OutputType { get; set; } = "";
    public bool ClientStreaming { get; set; }
    public bool ServerStreaming { get; set; }

    public string Kind => (ClientStreaming, ServerStreaming) switch
    {
        (false, false) => "unary",
        (false, true) => "server streaming",
        (true, false) => "client streaming",
        _ => "bidirectional streaming"
    };
}

public sealed class ServiceDesc
{
    public string Name { get; set; } = "";
    public string FullName { get; set; } = "";
    public List<MethodDesc> Methods { get; } = [];
}

public sealed class FileDesc
{
    public string Name { get; set; } = "";
    public string Package { get; set; } = "";
    public string Syntax { get; set; } = "proto2";
    public List<string> Dependencies { get; } = [];
    public List<MessageDesc> MessageTypes { get; } = [];
    public List<EnumDesc> EnumTypes { get; } = [];
    public List<ServiceDesc> Services { get; } = [];

    public bool IsProto3 => Syntax is "proto3" || Syntax.StartsWith("editions", StringComparison.Ordinal);
}

/// <summary>A set of files whose type references have been resolved to fully-qualified names.</summary>
public sealed class ProtoSchema
{
    private readonly Dictionary<string, MessageDesc> _messages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EnumDesc> _enums = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ServiceDesc> _services = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _messageIsProto3 = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, MessageDesc> Messages => _messages;
    public IReadOnlyDictionary<string, EnumDesc> Enums => _enums;
    public IReadOnlyDictionary<string, ServiceDesc> Services => _services;

    public MessageDesc Message(string fullName) =>
        _messages.TryGetValue(fullName.TrimStart('.'), out var m)
            ? m
            : throw new KeyNotFoundException($"Unknown message type '{fullName}'.");

    public EnumDesc? Enum(string? fullName) =>
        fullName is not null && _enums.TryGetValue(fullName.TrimStart('.'), out var e) ? e : null;

    public bool IsProto3(MessageDesc message) => _messageIsProto3.GetValueOrDefault(message.FullName);

    public (ServiceDesc Service, MethodDesc Method) FindMethod(string service, string method)
    {
        service = service.Trim().TrimStart('.');
        if (!_services.TryGetValue(service, out var svc))
        {
            // Accept the short name when it is unambiguous ("Greeter" for "helloworld.Greeter").
            var matches = _services.Values.Where(s => s.Name == service || s.FullName.EndsWith("." + service, StringComparison.Ordinal)).ToList();
            svc = matches.Count == 1
                ? matches[0]
                : throw new KeyNotFoundException(matches.Count == 0
                    ? $"Service '{service}' not found. Available: {string.Join(", ", _services.Keys)}"
                    : $"Service name '{service}' is ambiguous.");
        }
        var m = svc.Methods.FirstOrDefault(x => x.Name == method.Trim())
                ?? throw new KeyNotFoundException($"Method '{method}' not found in {svc.FullName}. Available: {string.Join(", ", svc.Methods.Select(x => x.Name))}");
        return (svc, m);
    }

    /// <summary>Links files: registers every type and resolves relative type names using protobuf scoping rules.</summary>
    public static ProtoSchema Link(IEnumerable<FileDesc> files)
    {
        var schema = new ProtoSchema();
        var fileList = files.GroupBy(f => f.Name).Select(g => g.First()).ToList();

        foreach (var file in fileList)
        {
            var prefix = file.Package.Length > 0 ? file.Package + "." : "";
            foreach (var m in file.MessageTypes)
                schema.Register(m, prefix, file.IsProto3);
            foreach (var e in file.EnumTypes)
            {
                e.FullName = prefix + e.Name;
                schema._enums[e.FullName] = e;
            }
            foreach (var s in file.Services)
            {
                s.FullName = prefix + s.Name;
                schema._services[s.FullName] = s;
            }
        }

        foreach (var file in fileList)
        {
            foreach (var m in file.MessageTypes)
                schema.ResolveMessage(m);
            foreach (var s in file.Services)
            {
                var scope = file.Package;
                foreach (var method in s.Methods)
                {
                    method.InputType = schema.ResolveType(method.InputType, scope, out _)
                                       ?? throw new FormatException($"Unknown type '{method.InputType}' in {s.FullName}.{method.Name}");
                    method.OutputType = schema.ResolveType(method.OutputType, scope, out _)
                                        ?? throw new FormatException($"Unknown type '{method.OutputType}' in {s.FullName}.{method.Name}");
                }
            }
        }
        return schema;
    }

    private void Register(MessageDesc message, string prefix, bool proto3)
    {
        message.FullName = prefix + message.Name;
        _messages[message.FullName] = message;
        _messageIsProto3[message.FullName] = proto3;
        foreach (var nested in message.NestedTypes)
            Register(nested, message.FullName + ".", proto3);
        foreach (var e in message.EnumTypes)
        {
            e.FullName = message.FullName + "." + e.Name;
            _enums[e.FullName] = e;
        }
    }

    private void ResolveMessage(MessageDesc message)
    {
        foreach (var field in message.Fields)
        {
            if (field.Type is ProtoType.Message or ProtoType.Enum or ProtoType.Group || (field.Type == 0 && field.TypeName is not null))
            {
                var resolved = ResolveType(field.TypeName!, message.FullName, out var isEnum)
                               ?? throw new FormatException($"Unknown type '{field.TypeName}' for field {message.FullName}.{field.Name}");
                field.TypeName = resolved;
                if (field.Type == 0)
                    field.Type = isEnum ? ProtoType.Enum : ProtoType.Message;
            }
        }
        foreach (var nested in message.NestedTypes)
            ResolveMessage(nested);
    }

    /// <summary>Resolves <paramref name="name"/> relative to <paramref name="scope"/> (innermost scope first).</summary>
    private string? ResolveType(string name, string scope, out bool isEnum)
    {
        isEnum = false;
        if (name.StartsWith('.'))
            return Lookup(name[1..], out isEnum);

        var firstPart = name.Split('.')[0];
        var current = scope;
        while (true)
        {
            var candidate = current.Length == 0 ? firstPart : current + "." + firstPart;
            if (_messages.ContainsKey(candidate) || _enums.ContainsKey(candidate) || IsPackagePrefix(candidate))
            {
                var full = current.Length == 0 ? name : current + "." + name;
                if (Lookup(full, out isEnum) is { } found)
                    return found;
            }
            if (current.Length == 0)
                return null;
            var dot = current.LastIndexOf('.');
            current = dot < 0 ? "" : current[..dot];
        }
    }

    private bool IsPackagePrefix(string candidate) =>
        _messages.Keys.Concat(_enums.Keys).Any(k => k.StartsWith(candidate + ".", StringComparison.Ordinal));

    private string? Lookup(string fullName, out bool isEnum)
    {
        isEnum = _enums.ContainsKey(fullName);
        return _messages.ContainsKey(fullName) || isEnum ? fullName : null;
    }
}
