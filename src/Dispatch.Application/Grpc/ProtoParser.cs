using System.Globalization;
using System.Text;

namespace Dispatch.Application.Grpc;

/// <summary>
/// Parses .proto source (proto2, proto3 and editions syntax) into <see cref="FileDesc"/>s. Options are read where
/// they affect encoding (<c>packed</c>, <c>json_name</c>, <c>default</c>) and otherwise skipped; extensions are ignored.
/// </summary>
public sealed class ProtoParser
{
    private readonly List<Token> _tokens;
    private readonly string _fileName;
    private int _index;

    private readonly record struct Token(string Text, bool IsString, int Line);

    private ProtoParser(string source, string fileName)
    {
        _tokens = Tokenize(source, fileName);
        _fileName = fileName;
    }

    public static FileDesc Parse(string source, string fileName = "input.proto") => new ProtoParser(source, fileName).ParseFile();

    /// <summary>
    /// Parses <paramref name="paths"/> and, transitively, their imports. Imports are resolved against the importing
    /// file's directory, then <paramref name="importPaths"/>; google/protobuf well-known types are built in.
    /// </summary>
    public static IReadOnlyList<FileDesc> ParseFiles(IEnumerable<string> paths, IEnumerable<string> importPaths,
        Func<string, string?>? readFile = null)
    {
        readFile ??= p => File.Exists(p) ? File.ReadAllText(p) : null;
        var roots = importPaths.Where(p => p.Length > 0).ToList();
        var parsed = new Dictionary<string, FileDesc>(StringComparer.Ordinal);
        var pending = new Queue<(string ImportName, string? Directory)>();

        foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            var full = Path.GetFullPath(path);
            var source = readFile(full) ?? throw new FileNotFoundException($"Proto file not found: {path}");
            var file = Parse(source, Path.GetFileName(full));
            // Imports are relative to an import root; treat the file's own directory as one.
            var dir = Path.GetDirectoryName(full)!;
            if (!roots.Contains(dir))
                roots.Add(dir);
            parsed[file.Name] = file;
            foreach (var dependency in file.Dependencies)
                pending.Enqueue((dependency, dir));
        }

        while (pending.Count > 0)
        {
            var (import, fromDirectory) = pending.Dequeue();
            if (parsed.ContainsKey(import) || parsed.Values.Any(f => f.Name == import))
                continue;

            string? source = null;
            if (WellKnownProtos.Sources.TryGetValue(import, out var builtin))
                source = builtin;
            else
            {
                foreach (var root in (fromDirectory is null ? roots : roots.Prepend(fromDirectory)).Distinct())
                {
                    source = readFile(Path.Combine(root, import));
                    if (source is not null)
                        break;
                }
            }
            if (source is null)
                throw new FileNotFoundException($"Import '{import}' not found. Add its root folder to the import paths.");

            var file = Parse(source, import);
            parsed[import] = file;
            foreach (var dependency in file.Dependencies)
                pending.Enqueue((dependency, null));
        }
        return parsed.Values.ToList();
    }

    // ---- Tokenizer ---------------------------------------------------------------------------------

    private static List<Token> Tokenize(string source, string fileName)
    {
        var tokens = new List<Token>();
        var line = 1;
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];
            if (c == '\n')
            {
                line++;
                i++;
            }
            else if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n')
                    i++;
            }
            else if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                    throw new FormatException($"{fileName}:{line}: unterminated comment");
                line += source.AsSpan(i, end - i).Count('\n');
                i = end + 2;
            }
            else if (c is '"' or '\'')
            {
                var sb = new StringBuilder();
                var quote = c;
                i++;
                while (i < source.Length && source[i] != quote)
                {
                    if (source[i] == '\\' && i + 1 < source.Length)
                    {
                        i++;
                        sb.Append(source[i] switch
                        {
                            'n' => '\n',
                            't' => '\t',
                            'r' => '\r',
                            '0' => '\0',
                            _ => source[i]
                        });
                    }
                    else
                    {
                        sb.Append(source[i]);
                    }
                    i++;
                }
                i++;
                // Adjacent string literals are concatenated, as in C.
                if (tokens.Count > 0 && tokens[^1].IsString)
                    tokens[^1] = tokens[^1] with { Text = tokens[^1].Text + sb };
                else
                    tokens.Add(new Token(sb.ToString(), true, line));
            }
            else if (char.IsLetterOrDigit(c) || c is '_' or '.' || (c is '-' or '+' && i + 1 < source.Length && char.IsDigit(source[i + 1])))
            {
                var start = i;
                i++;
                while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] is '_' or '.'
                                             || (source[i] is '-' or '+' && source[i - 1] is 'e' or 'E' && char.IsDigit(source[start]))))
                    i++;
                tokens.Add(new Token(source[start..i], false, line));
            }
            else
            {
                tokens.Add(new Token(c.ToString(), false, line));
                i++;
            }
        }
        return tokens;
    }

    // ---- Parser helpers ----------------------------------------------------------------------------

    private bool AtEnd => _index >= _tokens.Count;
    private Token Peek => AtEnd ? new Token("", false, _tokens.Count > 0 ? _tokens[^1].Line : 0) : _tokens[_index];

    private Token Next()
    {
        if (AtEnd)
            throw Error("unexpected end of file");
        return _tokens[_index++];
    }

    private bool TryConsume(string text)
    {
        if (!AtEnd && !Peek.IsString && Peek.Text == text)
        {
            _index++;
            return true;
        }
        return false;
    }

    private void Expect(string text)
    {
        var token = Next();
        if (token.IsString || token.Text != text)
            throw Error($"expected '{text}' but found '{token.Text}'", token);
    }

    private string Identifier()
    {
        var token = Next();
        if (token.IsString || token.Text.Length == 0 || !(char.IsLetter(token.Text[0]) || token.Text[0] is '_' or '.'))
            throw Error($"expected an identifier but found '{token.Text}'", token);
        return token.Text;
    }

    private string StringLiteral()
    {
        var token = Next();
        if (!token.IsString)
            throw Error($"expected a string but found '{token.Text}'", token);
        return token.Text;
    }

    private int Integer()
    {
        var token = Next();
        return ParseInt(token.Text) ?? throw Error($"expected a number but found '{token.Text}'", token);
    }

    private static int? ParseInt(string text)
    {
        var negative = text.StartsWith('-');
        var digits = text.TrimStart('-', '+');
        long value;
        if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!long.TryParse(digits[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
                return null;
        }
        else if (digits.Length > 1 && digits.StartsWith('0') && digits.All(char.IsDigit))
        {
            value = Convert.ToInt64(digits, 8);
        }
        else if (!long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return digits == "max" ? 536_870_911 : null;
        }
        return (int)(negative ? -value : value);
    }

    private FormatException Error(string message, Token? token = null) =>
        new($"{_fileName}:{(token ?? Peek).Line}: {message}");

    /// <summary>Skips a balanced construct: a statement up to ';' or a block in braces.</summary>
    private void SkipStatementOrBlock()
    {
        var depth = 0;
        while (!AtEnd)
        {
            var t = Next();
            if (t.IsString)
                continue;
            if (t.Text == "{")
                depth++;
            else if (t.Text == "}")
            {
                depth--;
                if (depth <= 0)
                    return;
            }
            else if (t.Text == ";" && depth == 0)
                return;
        }
    }

    // ---- Grammar -----------------------------------------------------------------------------------

    private FileDesc ParseFile()
    {
        var file = new FileDesc { Name = _fileName };
        while (!AtEnd)
        {
            var keyword = Peek.Text;
            switch (keyword)
            {
                case "syntax":
                case "edition":
                    Next();
                    Expect("=");
                    var value = StringLiteral();
                    file.Syntax = keyword == "edition" ? "editions" : value;
                    Expect(";");
                    break;
                case "package":
                    Next();
                    file.Package = Identifier();
                    Expect(";");
                    break;
                case "import":
                    Next();
                    if (Peek.Text is "public" or "weak" && !Peek.IsString)
                        Next();
                    file.Dependencies.Add(StringLiteral());
                    Expect(";");
                    break;
                case "option":
                    SkipStatementOrBlock();
                    break;
                case "message":
                    file.MessageTypes.Add(ParseMessage(file));
                    break;
                case "enum":
                    file.EnumTypes.Add(ParseEnum());
                    break;
                case "service":
                    file.Services.Add(ParseService());
                    break;
                case "extend":
                    SkipStatementOrBlock();
                    break;
                case ";":
                    Next();
                    break;
                default:
                    throw Error($"unexpected '{keyword}'");
            }
        }
        return file;
    }

    private MessageDesc ParseMessage(FileDesc file)
    {
        Expect("message");
        var message = new MessageDesc { Name = Identifier() };
        Expect("{");
        ParseMessageBody(message, file);
        return message;
    }

    private void ParseMessageBody(MessageDesc message, FileDesc file)
    {
        while (!TryConsume("}"))
        {
            switch (Peek.Text)
            {
                case "message":
                    message.NestedTypes.Add(ParseMessage(file));
                    break;
                case "enum":
                    message.EnumTypes.Add(ParseEnum());
                    break;
                case "option":
                case "reserved":
                case "extensions":
                case "extend":
                    SkipStatementOrBlock();
                    break;
                case "oneof":
                    Next();
                    var oneofIndex = message.Oneofs.Count;
                    message.Oneofs.Add(Identifier());
                    Expect("{");
                    while (!TryConsume("}"))
                    {
                        if (Peek.Text == "option")
                        {
                            SkipStatementOrBlock();
                            continue;
                        }
                        var field = ParseField(message, file, ProtoLabel.Optional);
                        field.OneofIndex = oneofIndex;
                    }
                    break;
                case "map":
                    ParseMapField(message, file);
                    break;
                case ";":
                    Next();
                    break;
                default:
                    var label = ProtoLabel.Optional;
                    var proto3Optional = false;
                    if (Peek.Text is "optional" or "required" or "repeated")
                    {
                        var text = Next().Text;
                        label = text == "repeated" ? ProtoLabel.Repeated : text == "required" ? ProtoLabel.Required : ProtoLabel.Optional;
                        proto3Optional = text == "optional" && file.IsProto3;
                    }
                    var f = ParseField(message, file, label);
                    if (proto3Optional)
                    {
                        // protoc models proto3 `optional` as a synthetic oneof.
                        f.Proto3Optional = true;
                        f.OneofIndex = message.Oneofs.Count;
                        message.Oneofs.Add("_" + f.Name);
                    }
                    break;
            }
        }
    }

    private FieldDesc ParseField(MessageDesc message, FileDesc file, ProtoLabel label)
    {
        var typeName = Identifier();
        if (typeName == "group")
            return ParseGroup(message, file, label);

        var field = new FieldDesc { Label = label, Name = Identifier() };
        SetType(field, typeName);
        Expect("=");
        field.Number = Integer();
        ParseFieldOptions(field);
        Expect(";");
        message.Fields.Add(field);
        return field;
    }

    private FieldDesc ParseGroup(MessageDesc message, FileDesc file, ProtoLabel label)
    {
        var groupName = Identifier();
        Expect("=");
        var number = Integer();
        if (Peek.Text == "[")
            ParseFieldOptions(new FieldDesc());
        Expect("{");
        var nested = new MessageDesc { Name = groupName };
        ParseMessageBody(nested, file);
        message.NestedTypes.Add(nested);
        var field = new FieldDesc
        {
            Label = label,
            Name = groupName.ToLowerInvariant(),
            Number = number,
            Type = ProtoType.Group,
            TypeName = groupName
        };
        message.Fields.Add(field);
        return field;
    }

    private void ParseMapField(MessageDesc message, FileDesc file)
    {
        Expect("map");
        Expect("<");
        var keyType = Identifier();
        Expect(",");
        var valueType = Identifier();
        Expect(">");
        var name = Identifier();
        Expect("=");
        var number = Integer();
        var field = new FieldDesc { Name = name, Number = number, Label = ProtoLabel.Repeated, Type = ProtoType.Message };
        ParseFieldOptions(field);
        Expect(";");

        var entryName = string.Concat(name.Split('_').Select(p => p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..])) + "Entry";
        var entry = new MessageDesc { Name = entryName, IsMapEntry = true };
        var key = new FieldDesc { Name = "key", Number = 1 };
        SetType(key, keyType);
        var value = new FieldDesc { Name = "value", Number = 2 };
        SetType(value, valueType);
        entry.Fields.Add(key);
        entry.Fields.Add(value);
        message.NestedTypes.Add(entry);

        field.TypeName = entryName;
        field.Packed = false;
        message.Fields.Add(field);
    }

    private void ParseFieldOptions(FieldDesc field)
    {
        if (!TryConsume("["))
            return;
        while (true)
        {
            var name = new StringBuilder();
            if (TryConsume("("))
            {
                name.Append('(').Append(Identifier()).Append(')');
                Expect(")");
                while (Peek.Text.StartsWith('.'))
                    name.Append(Next().Text);
            }
            else
            {
                name.Append(Identifier());
            }
            Expect("=");
            var valueToken = Next();
            if (!valueToken.IsString && valueToken.Text == "{")
            {
                _index--;
                SkipStatementOrBlock();
            }
            switch (name.ToString())
            {
                case "packed":
                    field.Packed = valueToken.Text == "true";
                    break;
                case "json_name":
                    field.JsonName = valueToken.Text;
                    break;
                case "default":
                    field.DefaultValue = valueToken.Text;
                    break;
            }
            if (TryConsume("]"))
                return;
            Expect(",");
        }
    }

    private static readonly Dictionary<string, ProtoType> ScalarTypes = new(StringComparer.Ordinal)
    {
        ["double"] = ProtoType.Double,
        ["float"] = ProtoType.Float,
        ["int64"] = ProtoType.Int64,
        ["uint64"] = ProtoType.UInt64,
        ["int32"] = ProtoType.Int32,
        ["fixed64"] = ProtoType.Fixed64,
        ["fixed32"] = ProtoType.Fixed32,
        ["bool"] = ProtoType.Bool,
        ["string"] = ProtoType.String,
        ["bytes"] = ProtoType.Bytes,
        ["uint32"] = ProtoType.UInt32,
        ["sfixed32"] = ProtoType.SFixed32,
        ["sfixed64"] = ProtoType.SFixed64,
        ["sint32"] = ProtoType.SInt32,
        ["sint64"] = ProtoType.SInt64
    };

    private static void SetType(FieldDesc field, string typeName)
    {
        if (ScalarTypes.TryGetValue(typeName, out var scalar))
            field.Type = scalar;
        else
        {
            field.Type = 0; // message or enum: decided when linking
            field.TypeName = typeName;
        }
    }

    private EnumDesc ParseEnum()
    {
        Expect("enum");
        var e = new EnumDesc { Name = Identifier() };
        Expect("{");
        while (!TryConsume("}"))
        {
            if (Peek.Text is "option" or "reserved")
            {
                SkipStatementOrBlock();
                continue;
            }
            if (TryConsume(";"))
                continue;
            var name = Identifier();
            Expect("=");
            var number = Integer();
            if (Peek.Text == "[")
                ParseFieldOptions(new FieldDesc());
            Expect(";");
            e.Values.Add((name, number));
        }
        return e;
    }

    private ServiceDesc ParseService()
    {
        Expect("service");
        var service = new ServiceDesc { Name = Identifier() };
        Expect("{");
        while (!TryConsume("}"))
        {
            if (Peek.Text == "option")
            {
                SkipStatementOrBlock();
                continue;
            }
            if (TryConsume(";"))
                continue;
            Expect("rpc");
            var method = new MethodDesc { Name = Identifier() };
            Expect("(");
            if (Peek.Text == "stream" && _index + 1 < _tokens.Count && _tokens[_index + 1].Text != ")")
            {
                Next();
                method.ClientStreaming = true;
            }
            method.InputType = Identifier();
            Expect(")");
            Expect("returns");
            Expect("(");
            if (Peek.Text == "stream" && _index + 1 < _tokens.Count && _tokens[_index + 1].Text != ")")
            {
                Next();
                method.ServerStreaming = true;
            }
            method.OutputType = Identifier();
            Expect(")");
            if (Peek.Text == "{")
                SkipStatementOrBlock();
            else
                Expect(";");
            service.Methods.Add(method);
        }
        return service;
    }
}
