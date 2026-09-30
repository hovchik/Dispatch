using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Dispatch.Domain;

namespace Dispatch.Application.Soap;

public sealed record SoapOperation(
    string Name,
    string SoapAction,
    SoapVersion Version,
    string Style,
    string Endpoint,
    XName? InputElement,
    IReadOnlyList<(string Name, XName? Element, XName? Type)> InputParts,
    string RpcNamespace,
    string PortName,
    string ServiceName)
{
    public string DisplayName => $"{ServiceName} / {PortName} / {Name}";
}

/// <summary>
/// Reads a WSDL 1.1 document (with its imported WSDLs and XSDs) and generates sample SOAP envelopes for its
/// operations from the XML Schema types.
/// </summary>
public sealed class WsdlDocument
{
    private static readonly XNamespace WsdlNs = "http://schemas.xmlsoap.org/wsdl/";
    private static readonly XNamespace Soap11BindingNs = "http://schemas.xmlsoap.org/wsdl/soap/";
    private static readonly XNamespace Soap12BindingNs = "http://schemas.xmlsoap.org/wsdl/soap12/";
    private static readonly XNamespace Xsd = "http://www.w3.org/2001/XMLSchema";
    public static readonly XNamespace Soap11Envelope = "http://schemas.xmlsoap.org/soap/envelope/";
    public static readonly XNamespace Soap12Envelope = "http://www.w3.org/2003/05/soap-envelope";

    private readonly List<XElement> _schemas = [];
    private readonly Dictionary<XName, XElement> _messages = [];
    private readonly Dictionary<XName, XElement> _portTypes = [];
    private readonly Dictionary<XName, XElement> _bindings = [];
    private readonly List<XElement> _services = [];

    public IReadOnlyList<SoapOperation> Operations { get; private set; } = [];

    /// <summary>Parses a WSDL; <paramref name="load"/> fetches imported documents by absolute location.</summary>
    public static async Task<WsdlDocument> LoadAsync(string location, Func<string, CancellationToken, Task<string>> load,
        CancellationToken ct = default)
    {
        var doc = new WsdlDocument();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await doc.AddDocumentAsync(location, load, visited, ct).ConfigureAwait(false);
        doc.Operations = doc.BuildOperations();
        return doc;
    }

    /// <summary>Parses a WSDL given as text (imports are not followed).</summary>
    public static WsdlDocument Parse(string xml)
    {
        var doc = new WsdlDocument();
        doc.AddRoot(XDocument.Parse(xml).Root!, null);
        doc.Operations = doc.BuildOperations();
        return doc;
    }

    private async Task AddDocumentAsync(string location, Func<string, CancellationToken, Task<string>> load,
        HashSet<string> visited, CancellationToken ct)
    {
        if (!visited.Add(location))
            return;
        var text = await load(location, ct).ConfigureAwait(false);
        XDocument xml;
        try
        {
            xml = XDocument.Parse(text);
        }
        catch (XmlException ex)
        {
            throw new FormatException($"{location} is not valid XML: {ex.Message}");
        }

        var imports = AddRoot(xml.Root!, location);
        foreach (var import in imports)
            await AddDocumentAsync(import, load, visited, ct).ConfigureAwait(false);
    }

    /// <summary>Registers a WSDL or XSD root; returns absolute locations of documents it imports.</summary>
    private List<string> AddRoot(XElement root, string? location)
    {
        var imports = new List<string>();
        string? Resolve(string? relative)
        {
            if (string.IsNullOrWhiteSpace(relative))
                return null;
            if (location is null)
                return Uri.TryCreate(relative, UriKind.Absolute, out _) ? relative : null;
            if (Uri.TryCreate(location, UriKind.Absolute, out var baseUri) && !baseUri.IsFile)
                return new Uri(baseUri, relative).ToString();
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(location)) ?? "", relative));
        }

        if (root.Name == Xsd + "schema")
        {
            AddSchema(root, Resolve, imports);
            return imports;
        }
        if (root.Name != WsdlNs + "definitions")
            throw new FormatException($"Not a WSDL 1.1 document (root element is {root.Name.LocalName}).");

        var targetNamespace = (string?)root.Attribute("targetNamespace") ?? "";
        XName Q(string localName) => XName.Get(localName, targetNamespace);

        foreach (var import in root.Elements(WsdlNs + "import"))
            if (Resolve((string?)import.Attribute("location")) is { } l)
                imports.Add(l);
        foreach (var schema in root.Elements(WsdlNs + "types").Elements(Xsd + "schema"))
            AddSchema(schema, Resolve, imports);
        foreach (var m in root.Elements(WsdlNs + "message"))
            _messages[Q((string)m.Attribute("name")!)] = m;
        foreach (var p in root.Elements(WsdlNs + "portType"))
            _portTypes[Q((string)p.Attribute("name")!)] = p;
        foreach (var b in root.Elements(WsdlNs + "binding"))
            _bindings[Q((string)b.Attribute("name")!)] = b;
        _services.AddRange(root.Elements(WsdlNs + "service"));
        return imports;
    }

    private void AddSchema(XElement schema, Func<string?, string?> resolve, List<string> imports)
    {
        _schemas.Add(schema);
        foreach (var import in schema.Elements(Xsd + "import").Concat(schema.Elements(Xsd + "include")))
            if (resolve((string?)import.Attribute("schemaLocation")) is { } l)
                imports.Add(l);
    }

    private List<SoapOperation> BuildOperations()
    {
        var operations = new List<SoapOperation>();
        foreach (var service in _services)
        {
            var serviceName = (string?)service.Attribute("name") ?? "Service";
            foreach (var port in service.Elements(WsdlNs + "port"))
            {
                var bindingName = ResolveQName(port, (string?)port.Attribute("binding"));
                if (bindingName is null || !_bindings.TryGetValue(bindingName, out var binding))
                    continue;

                var soapBinding = binding.Element(Soap11BindingNs + "binding") ?? binding.Element(Soap12BindingNs + "binding");
                if (soapBinding is null)
                    continue; // HTTP / MIME bindings are not SOAP
                var version = soapBinding.Name.Namespace == Soap12BindingNs ? SoapVersion.Soap12 : SoapVersion.Soap11;
                var defaultStyle = (string?)soapBinding.Attribute("style") ?? "document";
                var address = (string?)port.Elements().FirstOrDefault(e => e.Name.LocalName == "address")?.Attribute("location") ?? "";

                var portTypeName = ResolveQName(binding, (string?)binding.Attribute("type"));
                _portTypes.TryGetValue(portTypeName ?? XName.Get("_"), out var portType);

                foreach (var bindingOp in binding.Elements(WsdlNs + "operation"))
                {
                    var name = (string?)bindingOp.Attribute("name") ?? "";
                    var soapOp = bindingOp.Elements().FirstOrDefault(e => e.Name.LocalName == "operation"
                                                                         && (e.Name.Namespace == Soap11BindingNs || e.Name.Namespace == Soap12BindingNs));
                    var action = (string?)soapOp?.Attribute("soapAction") ?? "";
                    var style = (string?)soapOp?.Attribute("style") ?? defaultStyle;
                    var body = bindingOp.Element(WsdlNs + "input")?.Elements().FirstOrDefault(e => e.Name.LocalName == "body");
                    var rpcNamespace = (string?)body?.Attribute("namespace") ?? "";

                    var abstractOp = portType?.Elements(WsdlNs + "operation").FirstOrDefault(o => (string?)o.Attribute("name") == name);
                    var inputMessage = abstractOp?.Element(WsdlNs + "input") is { } input
                        ? ResolveQName(input, (string?)input.Attribute("message"))
                        : null;

                    var parts = new List<(string, XName?, XName?)>();
                    if (inputMessage is not null && _messages.TryGetValue(inputMessage, out var message))
                    {
                        foreach (var part in message.Elements(WsdlNs + "part"))
                            parts.Add(((string?)part.Attribute("name") ?? "part",
                                ResolveQName(part, (string?)part.Attribute("element")),
                                ResolveQName(part, (string?)part.Attribute("type"))));
                    }

                    operations.Add(new SoapOperation(name, action, version, style, address,
                        parts.FirstOrDefault(p => p.Item2 is not null).Item2, parts, rpcNamespace,
                        (string?)port.Attribute("name") ?? "", serviceName));
                }
            }
        }
        return operations;
    }

    private static XName? ResolveQName(XElement context, string? qname)
    {
        if (string.IsNullOrWhiteSpace(qname))
            return null;
        var colon = qname.IndexOf(':');
        if (colon < 0)
            return XName.Get(qname, context.GetDefaultNamespace().NamespaceName);
        var ns = context.GetNamespaceOfPrefix(qname[..colon]);
        return XName.Get(qname[(colon + 1)..], ns?.NamespaceName ?? "");
    }

    // ---- Envelope generation --------------------------------------------------------------------------

    public string BuildEnvelope(SoapOperation operation)
    {
        var envNs = operation.Version == SoapVersion.Soap12 ? Soap12Envelope : Soap11Envelope;
        var body = new XElement(envNs + "Body");
        var context = new GenerationContext();

        if (operation.Style.Equals("rpc", StringComparison.OrdinalIgnoreCase))
        {
            XNamespace ns = operation.RpcNamespace;
            var wrapper = new XElement(ns + operation.Name);
            foreach (var (name, element, type) in operation.InputParts)
            {
                if (element is not null)
                    wrapper.Add(SampleForElement(element, context, 0));
                else
                    wrapper.Add(SampleForType(new XElement(name), type, context, 0));
            }
            body.Add(wrapper);
        }
        else
        {
            foreach (var (_, element, _) in operation.InputParts)
                if (element is not null)
                    body.Add(SampleForElement(element, context, 0));
        }

        var envelope = new XElement(envNs + "Envelope",
            new XAttribute(XNamespace.Xmlns + "soapenv", envNs.NamespaceName),
            new XElement(envNs + "Header"),
            body);

        // Friendly prefixes for the payload namespaces.
        var index = 0;
        foreach (var ns in body.DescendantsAndSelf().Select(e => e.Name.Namespace).Where(n => n != envNs && n != XNamespace.None)
                     .Distinct())
            envelope.Add(new XAttribute(XNamespace.Xmlns + (index++ == 0 ? "tns" : $"ns{index}"), ns.NamespaceName));

        var settings = new XmlWriterSettings { Indent = true, OmitXmlDeclaration = true, IndentChars = "  " };
        var sb = new StringBuilder();
        using (var writer = XmlWriter.Create(sb, settings))
            envelope.WriteTo(writer);
        return sb.ToString();
    }

    private sealed class GenerationContext
    {
        public HashSet<XName> Visiting { get; } = [];
    }

    private XElement SampleForElement(XName elementName, GenerationContext context, int depth)
    {
        var declaration = FindGlobal("element", elementName);
        if (declaration is null)
            return new XElement(elementName, "?");
        return SampleForDeclaration(declaration, elementName.Namespace, context, depth, qualified: true);
    }

    private XElement SampleForDeclaration(XElement declaration, XNamespace targetNs, GenerationContext context, int depth,
        bool qualified)
    {
        if ((string?)declaration.Attribute("ref") is { } reference)
        {
            var refName = ResolveQName(declaration, reference)!;
            return SampleForElement(refName, context, depth);
        }

        var name = (string?)declaration.Attribute("name") ?? "element";
        var schema = declaration.Ancestors(Xsd + "schema").FirstOrDefault();
        var elementQualified = qualified || (string?)schema?.Attribute("elementFormDefault") == "qualified"
                               || (string?)declaration.Attribute("form") == "qualified";
        var element = new XElement(elementQualified ? targetNs + name : XName.Get(name));

        if (depth > 8)
            return element;

        var inlineComplex = declaration.Element(Xsd + "complexType");
        var inlineSimple = declaration.Element(Xsd + "simpleType");
        if (inlineComplex is not null)
            FillComplex(element, inlineComplex, targetNs, context, depth);
        else if (inlineSimple is not null)
            element.Value = SampleForSimple(inlineSimple);
        else
            return SampleForType(element, ResolveQName(declaration, (string?)declaration.Attribute("type")), context, depth);
        return element;
    }

    private XElement SampleForType(XElement element, XName? type, GenerationContext context, int depth)
    {
        if (type is null)
        {
            element.Value = "?";
            return element;
        }
        if (type.Namespace == Xsd)
        {
            element.Value = SampleForBuiltin(type.LocalName);
            return element;
        }
        var complex = FindGlobal("complexType", type);
        if (complex is not null)
        {
            if (!context.Visiting.Add(type))
                return element; // recursive type: stop here
            FillComplex(element, complex, type.Namespace, context, depth);
            context.Visiting.Remove(type);
            return element;
        }
        var simple = FindGlobal("simpleType", type);
        element.Value = simple is not null ? SampleForSimple(simple) : "?";
        return element;
    }

    private void FillComplex(XElement element, XElement complexType, XNamespace targetNs, GenerationContext context, int depth)
    {
        foreach (var attribute in complexType.Elements(Xsd + "attribute"))
        {
            if ((string?)attribute.Attribute("name") is { } attrName)
                element.SetAttributeValue(attrName, SampleForBuiltin(ResolveQName(attribute, (string?)attribute.Attribute("type"))?.LocalName ?? "string"));
        }

        var content = complexType.Element(Xsd + "complexContent") ?? complexType.Element(Xsd + "simpleContent");
        if (content is not null)
        {
            var derivation = content.Element(Xsd + "extension") ?? content.Element(Xsd + "restriction");
            if (derivation is not null)
            {
                var baseType = ResolveQName(derivation, (string?)derivation.Attribute("base"));
                if (content.Name.LocalName == "simpleContent")
                {
                    element.Value = SampleForBuiltin(baseType?.LocalName ?? "string");
                }
                else if (derivation.Name.LocalName == "extension" && baseType is not null && FindGlobal("complexType", baseType) is { } baseComplex
                         && context.Visiting.Add(baseType))
                {
                    FillComplex(element, baseComplex, baseType.Namespace, context, depth);
                    context.Visiting.Remove(baseType);
                }
                FillParticles(element, derivation, targetNs, context, depth);
            }
            return;
        }
        FillParticles(element, complexType, targetNs, context, depth);
    }

    private void FillParticles(XElement element, XElement container, XNamespace targetNs, GenerationContext context, int depth)
    {
        foreach (var particle in container.Elements())
        {
            switch (particle.Name.LocalName)
            {
                case "sequence" or "all":
                    FillParticles(element, particle, targetNs, context, depth);
                    break;
                case "choice":
                    element.Add(new XComment("Choose one of the following"));
                    if (particle.Elements().FirstOrDefault(e => e.Name.LocalName is "element" or "sequence") is { } first)
                    {
                        var holder = new XElement("holder");
                        FillParticles(holder, new XElement(Xsd + "sequence", first), targetNs, context, depth);
                        element.Add(holder.Nodes());
                    }
                    break;
                case "group" when ResolveQName(particle, (string?)particle.Attribute("ref")) is { } groupName
                                  && FindGlobal("group", groupName) is { } group:
                    FillParticles(element, group, groupName.Namespace, context, depth);
                    break;
                case "element":
                    var optional = (string?)particle.Attribute("minOccurs") == "0";
                    var many = (string?)particle.Attribute("maxOccurs") is { } max && max != "1" && max != "0";
                    if (optional || many)
                        element.Add(new XComment((optional ? "Optional" : "") + (optional && many ? ", " : "") + (many ? "repeatable" : "")));
                    element.Add(SampleForDeclaration(particle, targetNs, context, depth + 1, qualified: false));
                    break;
                case "any":
                    element.Add(new XComment("Any element"));
                    break;
            }
        }
    }

    private static string SampleForSimple(XElement simpleType)
    {
        var restriction = simpleType.Element(Xsd + "restriction");
        var enumeration = restriction?.Elements(Xsd + "enumeration").FirstOrDefault();
        if (enumeration is not null)
            return (string?)enumeration.Attribute("value") ?? "?";
        var baseType = ResolveQName(restriction ?? simpleType, (string?)restriction?.Attribute("base"));
        return SampleForBuiltin(baseType?.LocalName ?? "string");
    }

    private static string SampleForBuiltin(string type) => type switch
    {
        "int" or "integer" or "long" or "short" or "byte" or "nonNegativeInteger" or "positiveInteger" or "unsignedInt"
            or "unsignedLong" or "unsignedShort" or "unsignedByte" or "negativeInteger" or "nonPositiveInteger" => "0",
        "decimal" or "double" or "float" => "0.0",
        "boolean" => "false",
        "dateTime" => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        "date" => DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        "time" => "00:00:00",
        "base64Binary" => "",
        "anyURI" => "http://example.com",
        _ => "?"
    };

    private XElement? FindGlobal(string kind, XName name)
    {
        foreach (var schema in _schemas)
        {
            var targetNs = (string?)schema.Attribute("targetNamespace") ?? "";
            if (targetNs != name.NamespaceName)
                continue;
            var match = schema.Elements(Xsd + kind).FirstOrDefault(e => (string?)e.Attribute("name") == name.LocalName);
            if (match is not null)
                return match;
        }
        return null;
    }
}
