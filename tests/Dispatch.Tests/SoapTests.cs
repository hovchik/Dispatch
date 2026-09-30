using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Dispatch.Application.Requests;
using Dispatch.Application.Soap;
using Dispatch.Domain;
using Dispatch.Infrastructure.Http;
using Dispatch.Infrastructure.Protocols;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using ExecutionContext = Dispatch.Application.Abstractions.ExecutionContext;

namespace Dispatch.Tests;

public class SoapTests
{
    // Document/literal WSDL with a SOAP 1.1 and a SOAP 1.2 binding, complex types, optional/repeated elements,
    // an enumeration, a type extension and an imported schema.
    private const string Wsdl = """
        <definitions xmlns="http://schemas.xmlsoap.org/wsdl/"
                     xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/"
                     xmlns:soap12="http://schemas.xmlsoap.org/wsdl/soap12/"
                     xmlns:xsd="http://www.w3.org/2001/XMLSchema"
                     xmlns:tns="urn:orders" xmlns:common="urn:common"
                     targetNamespace="urn:orders">
          <types>
            <xsd:schema targetNamespace="urn:orders" elementFormDefault="qualified">
              <xsd:import namespace="urn:common" schemaLocation="common.xsd"/>
              <xsd:element name="PlaceOrder">
                <xsd:complexType>
                  <xsd:sequence>
                    <xsd:element name="customer" type="common:Customer"/>
                    <xsd:element name="line" type="tns:Line" maxOccurs="unbounded"/>
                    <xsd:element name="note" type="xsd:string" minOccurs="0"/>
                    <xsd:element name="priority" type="tns:Priority"/>
                  </xsd:sequence>
                  <xsd:attribute name="version" type="xsd:int"/>
                </xsd:complexType>
              </xsd:element>
              <xsd:element name="PlaceOrderResponse">
                <xsd:complexType><xsd:sequence><xsd:element name="id" type="xsd:string"/></xsd:sequence></xsd:complexType>
              </xsd:element>
              <xsd:complexType name="Line">
                <xsd:sequence>
                  <xsd:element name="sku" type="xsd:string"/>
                  <xsd:element name="qty" type="xsd:int"/>
                </xsd:sequence>
              </xsd:complexType>
              <xsd:simpleType name="Priority">
                <xsd:restriction base="xsd:string">
                  <xsd:enumeration value="LOW"/><xsd:enumeration value="HIGH"/>
                </xsd:restriction>
              </xsd:simpleType>
            </xsd:schema>
          </types>
          <message name="PlaceOrderIn"><part name="parameters" element="tns:PlaceOrder"/></message>
          <message name="PlaceOrderOut"><part name="parameters" element="tns:PlaceOrderResponse"/></message>
          <portType name="OrdersPort">
            <operation name="PlaceOrder"><input message="tns:PlaceOrderIn"/><output message="tns:PlaceOrderOut"/></operation>
          </portType>
          <binding name="OrdersSoap" type="tns:OrdersPort">
            <soap:binding style="document" transport="http://schemas.xmlsoap.org/soap/http"/>
            <operation name="PlaceOrder">
              <soap:operation soapAction="urn:orders/PlaceOrder"/>
              <input><soap:body use="literal"/></input><output><soap:body use="literal"/></output>
            </operation>
          </binding>
          <binding name="OrdersSoap12" type="tns:OrdersPort">
            <soap12:binding style="document" transport="http://schemas.xmlsoap.org/soap/http"/>
            <operation name="PlaceOrder">
              <soap12:operation soapAction="urn:orders/PlaceOrder"/>
              <input><soap12:body use="literal"/></input><output><soap12:body use="literal"/></output>
            </operation>
          </binding>
          <service name="Orders">
            <port name="OrdersSoap" binding="tns:OrdersSoap"><soap:address location="http://example.com/orders"/></port>
            <port name="OrdersSoap12" binding="tns:OrdersSoap12"><soap12:address location="http://example.com/orders12"/></port>
          </service>
        </definitions>
        """;

    private const string CommonXsd = """
        <xsd:schema xmlns:xsd="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:common" elementFormDefault="qualified"
                    xmlns:c="urn:common">
          <xsd:complexType name="Party"><xsd:sequence><xsd:element name="name" type="xsd:string"/></xsd:sequence></xsd:complexType>
          <xsd:complexType name="Customer">
            <xsd:complexContent>
              <xsd:extension base="c:Party">
                <xsd:sequence><xsd:element name="email" type="xsd:string"/><xsd:element name="vip" type="xsd:boolean"/></xsd:sequence>
              </xsd:extension>
            </xsd:complexContent>
          </xsd:complexType>
        </xsd:schema>
        """;

    private static Task<WsdlDocument> Load() => WsdlDocument.LoadAsync("/wsdl/orders.wsdl", (location, _) =>
        Task.FromResult(location.EndsWith("common.xsd") ? CommonXsd : Wsdl));

    [Fact]
    public async Task Lists_operations_per_binding_with_actions_and_versions()
    {
        var wsdl = await Load();

        Assert.Equal(2, wsdl.Operations.Count);
        var op11 = wsdl.Operations.Single(o => o.Version == SoapVersion.Soap11);
        Assert.Equal("urn:orders/PlaceOrder", op11.SoapAction);
        Assert.Equal("http://example.com/orders", op11.Endpoint);
        Assert.Equal("http://example.com/orders12", wsdl.Operations.Single(o => o.Version == SoapVersion.Soap12).Endpoint);
    }

    [Fact]
    public async Task Generates_envelope_from_schema_including_imported_and_extended_types()
    {
        var wsdl = await Load();
        var envelope = XDocument.Parse(wsdl.BuildEnvelope(wsdl.Operations.Single(o => o.Version == SoapVersion.Soap11)));
        XNamespace orders = "urn:orders";
        XNamespace common = "urn:common";

        var body = envelope.Root!.Element(WsdlDocument.Soap11Envelope + "Body")!;
        var place = body.Element(orders + "PlaceOrder")!;
        Assert.Equal("0", (string?)place.Attribute("version"));
        Assert.Equal("?", place.Element(orders + "customer")!.Element(common + "name")!.Value); // from the base type
        Assert.Equal("false", place.Element(orders + "customer")!.Element(common + "vip")!.Value);
        Assert.Equal("0", place.Element(orders + "line")!.Element(orders + "qty")!.Value);
        Assert.Equal("LOW", place.Element(orders + "priority")!.Value);
        Assert.Contains(place.Nodes().OfType<XComment>(), c => c.Value.Contains("Optional"));
    }

    [Fact]
    public void Rpc_style_wraps_parts_in_the_operation_element()
    {
        const string rpc = """
            <definitions xmlns="http://schemas.xmlsoap.org/wsdl/" xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/"
                         xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:tns="urn:calc" targetNamespace="urn:calc">
              <message name="AddIn"><part name="a" type="xsd:int"/><part name="b" type="xsd:int"/></message>
              <portType name="Calc"><operation name="Add"><input message="tns:AddIn"/></operation></portType>
              <binding name="CalcSoap" type="tns:Calc">
                <soap:binding style="rpc" transport="http://schemas.xmlsoap.org/soap/http"/>
                <operation name="Add"><soap:operation soapAction="Add"/><input><soap:body use="literal" namespace="urn:calc"/></input></operation>
              </binding>
              <service name="CalcService"><port name="CalcPort" binding="tns:CalcSoap"><soap:address location="http://c/calc"/></port></service>
            </definitions>
            """;
        var wsdl = WsdlDocument.Parse(rpc);
        var envelope = XDocument.Parse(wsdl.BuildEnvelope(wsdl.Operations.Single()));

        var add = envelope.Descendants(XName.Get("Add", "urn:calc")).Single();
        Assert.Equal(["a", "b"], add.Elements().Select(e => e.Name.LocalName));
        Assert.Equal("0", add.Element("a")!.Value);
    }

    [Fact]
    public void Ws_security_digest_follows_the_username_token_profile()
    {
        var nonce = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        var created = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var xml = SoapEnvelope.ApplyWsSecurity(
            """<soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/"><soapenv:Body/></soapenv:Envelope>""",
            new SoapSettings { WsSecurity = WsSecurityMode.UsernameTokenDigest, WsUsername = "bob", WsPassword = "secret", AddTimestamp = true },
            created, nonce);

        var doc = XDocument.Parse(xml);
        var token = doc.Descendants(SoapEnvelope.Wsse + "UsernameToken").Single();
        var expected = Convert.ToBase64String(SHA1.HashData(nonce.Concat(Encoding.UTF8.GetBytes("2024-01-02T03:04:05.000Zsecret")).ToArray()));
        Assert.Equal("bob", token.Element(SoapEnvelope.Wsse + "Username")!.Value);
        Assert.Equal(expected, token.Element(SoapEnvelope.Wsse + "Password")!.Value);
        Assert.Single(doc.Descendants(SoapEnvelope.Wsu + "Timestamp"));
        // The header was created before the body.
        Assert.Equal("Header", doc.Root!.Elements().First().Name.LocalName);
    }

    [Theory]
    [InlineData("""<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><s:Fault><faultcode>s:Client</faultcode><faultstring>Bad qty</faultstring></s:Fault></s:Body></s:Envelope>""", "s:Client", "Bad qty")]
    [InlineData("""<e:Envelope xmlns:e="http://www.w3.org/2003/05/soap-envelope"><e:Body><e:Fault><e:Code><e:Value>e:Sender</e:Value></e:Code><e:Reason><e:Text xml:lang="en">Nope</e:Text></e:Reason></e:Fault></e:Body></e:Envelope>""", "e:Sender", "Nope")]
    public void Parses_soap11_and_soap12_faults(string body, string code, string reason)
    {
        var fault = SoapEnvelope.ParseFault(body)!;
        Assert.Equal(code, fault.Code);
        Assert.Equal(reason, fault.Reason);
        Assert.Null(SoapEnvelope.ParseFault("<ok/>"));
    }

    [Fact]
    public async Task Executor_sets_soap_headers_and_reports_faults()
    {
        string? soapAction = null, contentType = null;
        await using var server = await TestServer.StartAsync(app => app.MapPost("/svc", async (HttpContext ctx) =>
        {
            soapAction = ctx.Request.Headers["SOAPAction"];
            contentType = ctx.Request.ContentType;
            var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
            ctx.Response.ContentType = "text/xml";
            ctx.Response.StatusCode = body.Contains("fail") ? 500 : 200;
            await ctx.Response.WriteAsync(body.Contains("fail")
                ? """<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><s:Fault><faultcode>s:Server</faultcode><faultstring>Boom</faultstring></s:Fault></s:Body></s:Envelope>"""
                : """<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><r>ok</r></s:Body></s:Envelope>""");
        }));

        using var pool = new HttpClientPool();
        var executor = new SoapExecutor(new HttpProtocolExecutor(new RequestMessageBuilder(), new HttpRequestExecutor(pool)));
        ApiRequest Request(string payload) => new()
        {
            Kind = RequestKind.Soap,
            Url = server.BaseUrl + "/svc",
            Body = new RequestBody { Content = $"""<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><{payload}/></s:Body></s:Envelope>""" },
            Protocol = new ProtocolSettings { Soap = new SoapSettings { Action = "urn:do" } }
        };

        var ok = await executor.ExecuteAsync(Request("go"), new ExecutionContext(), CancellationToken.None);
        var failed = await executor.ExecuteAsync(Request("fail"), new ExecutionContext(), CancellationToken.None);

        Assert.True(ok.IsSuccess);
        Assert.Equal("\"urn:do\"", soapAction);
        Assert.StartsWith("text/xml", contentType);
        Assert.False(failed.IsSuccess);
        Assert.Contains("s:Server: Boom", failed.ReasonPhrase);
    }
}
