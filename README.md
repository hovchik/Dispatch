<p align="center"><img src="src/Dispatch.App/Assets/dispatch-logo.svg" width="96" alt="Dispatch logo" /></p>

# Dispatch

A fast, cross-platform API client for developers and QA engineers, built on **.NET 10** and **Avalonia 11**.
Dispatch talks HTTP, GraphQL, gRPC, SOAP, WebSocket, SSE, Socket.IO, MQTT, Kafka, AMQP and raw TCP/UDP. It also has
the testing tools QA work needs: assertions, chaining, a collection runner, data-driven runs, CI reports, a
headless CLI, a mock server, response diffs, contract checks and load tests.

## Protocols

| Protocol | What you get |
|---|---|
| **HTTP / REST** | All methods, URL ⇄ params sync, headers, JSON / XML / text / form-urlencoded / multipart (files) / binary bodies, HTTP/1.1 · 2 · 3 preference |
| **GraphQL** | Query + variables editors, schema introspection with a field explorer, operation name |
| **gRPC** | Unary, server, client and bidirectional streaming; schema from **server reflection** (v1 / v1alpha) or **.proto files**; message template generation; metadata and trailers; TLS or h2c; deadlines. Implemented in-house, with no codegen step |
| **SOAP** | Load a **WSDL**, pick an operation, and get a generated envelope; SOAP 1.1 / 1.2 with SOAPAction handling; faults shown clearly |
| **WebSocket** | Live session: connect, send text or JSON (saved messages), subprotocols, message log with timestamps |
| **Server-Sent Events** | Live event stream with event names and ids |
| **Socket.IO** | v4 (Engine.IO), namespaces, auth payload, emit events with arguments, listen to chosen events |
| **MQTT** | 3.1.1 / 5, publish and subscribe with QoS, retain, TLS, credentials |
| **Kafka** | Produce (key, headers) and consume (group, offset reset), SASL / SSL |
| **AMQP (RabbitMQ)** | Publish to exchanges with routing keys and properties, consume from queues or a temporary bound queue |
| **TCP / UDP** | Raw sockets with text / hex / base64 payloads, line endings and read timeouts |

## Testing & QA

* **Assertions without code**: status, time, headers, body (JSONPath / XPath / regex), JSON Schema, and
  **OpenAPI contract** checks. There are 15+ operators; "Add assertion from response" pre-fills one.
* **Extraction & chaining**: pull values out of responses (JSONPath, XPath, header, regex, status, body) into
  runtime, collection or environment variables.
* **Scripts**: Postman-compatible `pm` API (`pm.test`, `pm.expect`, `pm.environment.set`, ...) for pre-request and
  test scripts, run on an embedded JavaScript engine.
* **Collection runner**: pick and order requests, run iterations, drive runs from a **CSV / JSON data file**,
  stop on failure, and export **HTML, JUnit XML or JSON reports**.
* **Mock server**: serves the saved examples of a collection over HTTP and gRPC, with latency, jitter, error-rate
  and dropped-connection simulation, CORS and path parameters.
* **Load testing**: virtual users, ramp-up, think time, live requests-per-second chart, p50 / p95 / p99, and
  per-request stats.
* **Response diff**: compare with the previous response or any two history entries. You get a line diff plus a
  structural JSON diff with ignore paths (`$.timestamp`, `$..id`).

## Developer tools

* **Import**: Postman (collections and environments), OpenAPI / Swagger (JSON or YAML), Insomnia, HAR, WSDL,
  `.proto`, `.http` files, cURL commands, and Dispatch files or folders, from a file, a URL or pasted text.
* **Export**: a Dispatch file, a **git-friendly folder** (one file per request), a Postman collection v2.1, or a
  `.http` file.
* **Code generation**: cURL, HTTPie, C#, Python, JavaScript, Go, grpcurl and websocat.
* **Auth**: Bearer, Basic, API key, **OAuth 2.0** (client credentials, password, authorization code with PKCE,
  device code; tokens are cached and refreshed), **AWS Signature v4**, Digest, NTLM / Negotiate, and **mTLS** client
  certificates. A JWT decoder shows the claims of tokens found in responses.
* **Network**: per-request timeout, redirects, TLS verification, proxy, HTTP version, and a cookie jar with a
  manager. A **timeline** shows DNS, connect, TLS, time to first byte and download, plus the raw request.
* **Variables**: layered scopes (globals < collection < environment < data row < runtime), `{{$guid}}`,
  `{{$timestamp}}`, `{{$randomInt}}` and other dynamic values. **Secret** variables are encrypted at rest with a key
  kept in the OS keychain (DPAPI / Keychain / Secret Service).
* **Workspace**: collections with nested folders, history with a filter, multiple tabs, a **command palette**
  (Ctrl+K) that searches every request and command, JSONPath / XPath filtering of response bodies, and a light or
  dark theme.

### Shortcuts

| Keys | Action |
|---|---|
| `Ctrl+Enter` / `Enter` in URL | Send / connect |
| `Ctrl+K` or `Ctrl+Shift+P` | Command palette |
| `Ctrl+S` | Save |
| `Ctrl+T` | New HTTP request (the arrow next to **New** offers every protocol) |
| `Ctrl+W` | Close tab |
| `Ctrl+O` | Import |

## CLI

`dispatch` runs collections headlessly, for CI pipelines and scripts:

```bash
dotnet run --project src/Dispatch.Cli -- run tests/api.dispatch.json -e staging.json -r cli,junit,html -o reports
dotnet run --project src/Dispatch.Cli -- run "My Collection" --data users.csv --bail
dotnet run --project src/Dispatch.Cli -- mock petstore.yaml --port 4010 --latency 200 --error-rate 5%
dotnet run --project src/Dispatch.Cli -- load "My Collection" --users 50 --duration 60s --max-p95 300
```

`<collection>` can be any importable file, a URL, a Dispatch folder, or the name of a collection saved in the
desktop app. Exit codes: `0` all passed, `1` failures, `2` usage error. Run `dispatch help` for every option. It can
also be packed as a .NET tool (`dotnet pack src/Dispatch.Cli`).

## Architecture

Clean Architecture, dependencies point inwards:

```
Dispatch.App / Dispatch.Cli   Avalonia UI (MVVM, CommunityToolkit.Mvvm) · command-line runner
   └─ Dispatch.Infrastructure   Protocol executors, HTTP transport, EF Core SQLite, scripting, mock server, DI
         └─ Dispatch.Application   Use cases: sending pipeline, variables, assertions, runner, import/export, codecs
               └─ Dispatch.Domain     Entities & value objects (no dependencies)
```

* **Application** has no NuGet dependencies. It holds the protocol-agnostic `RequestSender` pipeline (pre-request
  script → variable resolution → auth → executor → extraction → assertions → tests → history), the collection
  runner and load tester, importers and exporters, the WSDL / proto parsers, and the protobuf JSON codec.
* **Infrastructure** provides one `IProtocolExecutor` per protocol. It also holds the pooled HTTP transport with
  timings and the cookie jar, OAuth 2.0, the Jint-based scripting host, the Kestrel mock server, secret protection
  and persistence.
* **App** views are XAML with compiled bindings; behaviour lives in view models.

Data is stored in `%LOCALAPPDATA%\Dispatch\dispatch.db` (`~/.local/share/Dispatch` on Linux/macOS).

## Build & run

```bash
dotnet build Dispatch.slnx
dotnet test Dispatch.slnx
dotnet run --project src/Dispatch.App
```

Requires the .NET 10 SDK. The Kafka and RabbitMQ integration tests run when `DISPATCH_KAFKA` (e.g.
`localhost:9092`) and `DISPATCH_AMQP` (e.g. `amqp://guest:guest@localhost:5672/`) are set.
