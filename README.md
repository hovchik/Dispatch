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
  stop on failure. A **report tab** shows the pass rate, insights (flaky / always-failing requests), per-request results,
  every failure and the slowest calls; export **HTML, JUnit XML or JSON reports**.
* **Mock server**: serves the saved examples of a collection over HTTP and gRPC, with latency, jitter, error-rate
  and dropped-connection simulation, CORS and path parameters. Routes are listed before you start, with live hit counts,
  and the request log can be filtered to unmatched requests and simulated faults. Match rules pick an example by
  query, path parameter, header or body; clients can also ask for one with `Prefer: code=404` or
  `Prefer: example=Not found`. Requests that share a path contribute their examples together, GraphQL operations on
  one endpoint are told apart by operation name or root field, and `HEAD` is answered by the `GET` route.
  **Recorded sessions:** save a WebSocket or SSE session as an example and the mock server replays it with the original
  timing. On WebSocket, each client message plays the part of the recording that answered the matching recorded
  message (exact, same JSON apart from ids, or the next part in order), with the client's ids put into the replies.
  `--session-speed` changes the pace.
* **Load testing**: virtual users, ramp-up, think time, live requests-per-second chart, p50 / p95 / p99, and
  per-request stats. When a test ends you get a **detailed report**: verdict, insights (long tails, degradation over time,
  throttling), latency distribution, status codes and per-request percentiles, exportable as **HTML, JSON or CSV**.
* **Response diff**: compare with the previous response or any two history entries. You get a line diff plus a
  structural JSON diff with ignore paths (`$.timestamp`, `$..id`).
* **Snapshot tests**: an assertion records the first response and checks later ones against it — structural JSON
  compare with ignore paths, line diff for text. The CLI records with `--update-snapshots`.
* **Test flows**: chain requests with control flow — if, repeat, for-each, until (retry with a wait), set-variable,
  script, delay and stop/fail — in a visual builder, sharing one set of variables. Run headless with `dispatch flow`.
  **Fork a run:** every flow run is recorded. Pick any request in the run log, edit its response (500, an empty list,
  a timeout, or any body) and replay the flow. Requests before the fork come from the recording, and the rest are
  replayed offline or sent live. The result shows what changed compared with the original run. From the CLI:
  `dispatch flow --record run.json`, then `--replay run.json --fork 2 --status 500 --offline`.
* **Monitors**: run a collection on an interval or cron schedule and alert via **Slack, a webhook, or email**
  (every-run / on-failure / on-change, with recovery notices and a response-time ceiling). `dispatch monitor --watch`
  runs them as a daemon; runs are kept as history.
* **Security scan**: passive checks (transport, security headers, CORS, banner / stack-trace / secret disclosure) and
  bounded active probes (injection and reflection, boundary input, missing authentication) for APIs you are
  authorised to test. `dispatch scan --fail-on high` gates CI; reports export to HTML / JSON.

### Message checks: cross-protocol consequence assertions

A request's tests can include **messages it must cause on other channels**. For example, after `POST /orders` there
must be a message on the Kafka topic `orders.created` with `$.orderId == {{orderId}}` within 2 s, and no
`payment.failed` event on MQTT. The listener is any saved MQTT, Kafka, AMQP, WebSocket, SSE or Socket.IO request in the
collection. It subscribes *before* the request is sent, so nothing is missed, and only messages that arrive afterwards
count. Expected values can use variables extracted from the response. The checks appear as normal test results in the
app, the collection runner, flows, monitors and `dispatch run`, with how long the message took or what arrived instead.
Edit them in a request's **Checks** tab.

### Client fuzzing

**Fuzz app** in the toolbar (or `dispatch fuzz-client`) fuzzes the client instead of the server. It is a proxy, like
Capture, between your web or mobile app and its API. After a few normal responses per endpoint it changes one response
at a time:

* a null or missing field, an empty or single-item list
* an unexpected enum value, a wrong type, an unknown field, very long text
* a 500 / 503 / 429 / 401, a malformed, empty or slow body

It then watches what the app does next, and flags:

* retry storms
* broken values sent back to the API (`GET /avatars/undefined`, `null`, `NaN`, `[object Object]`)
* calls to error trackers (Sentry, `/errors`, `/log`…)
* an app that goes silent where it normally continues

The result reads like *"breaks when `$.user.avatar` is missing: GET /avatars/undefined"*. Reports export as HTML and
JSON, and `--fail-on-break` gates CI runs of automated UI tests that go through the proxy.

### API laws (inferred invariants)

**Collection menu → API laws…** learns the rules an API keeps from its traffic: recent history, runs of the collection,
or a HAR file from the capture proxy or a browser. These are semantic rules, not a schema:

* required fields and types
* enums, never-negative numbers, and formats (UUID, email, date-time)
* `createdAt ≤ updatedAt`
* `count == items.length` and `total == sum(items[*].price)`
* page sizes within `?limit=`
* request fields echoed back
* created resources that read back with the submitted values
* deleted resources that return 404
* GETs that repeat, minus volatile fields

Rules that held in all but a few of many responses are reported as **anomalies**, together with the responses that
broke them, e.g. *status is "paid" or "shipped", but once "shiped"*. Selected laws become assertions or `pm.test`
checks on the matching saved requests, so a later violation fails the tests. `dispatch laws traffic.har
--fail-on-anomaly` gates CI, and `dispatch laws <collection> --runs 5 --write` adds the laws as tests.

### Change impact map

When a response changes shape (a field renamed, removed or retyped), **Tools → Change impact…** compares it with the
previous response, the recorded snapshot or a saved example. It then lists everything in the collection that depends
on the change: the request's JSONPath assertions and snapshot, the variables its extraction rules will no longer set,
and every request, script, message check and test flow that uses those variables. Chains are followed (a request that
uses a broken variable can break what it extracts too), and saved examples that still serve the old shape are flagged.
Renames come with the corrected path, e.g. *"renamed `$.user.id` → `$.user.userId` breaks 3 tests, 2 extractions,
2 requests and 1 flow"*. `dispatch impact <collection> --request "Get user"` checks the live API against the snapshot
in CI and fails when something breaks.

### Experimental: request forensics

Two investigative tools that go beyond what API clients usually offer. Open them from the flask button next to
**Send** and from the CLI:

* **Request minimizer** (delta debugging for HTTP): give it a request and its outcome, such as a 403, a 500, a failing
  assertion, a success, or a body containing some text. It keeps re-sending the request with headers, individual
  cookies, query parameters, auth, form fields and JSON members removed (in halves, then smaller groups, one nesting
  level at a time). It ends with the smallest request that gives the same outcome. Minimize a failure to see exactly
  what triggers it; minimize a success to see what an endpoint really requires. The result is re-sent to confirm it,
  and you can open it in a tab, copy it as cURL, or export an HTML / JSON report.
  `dispatch minimize <collection> --request "Create order" [--match status|class|tests|body --contains text]`
* **Rate-limit mapper**: it probes one endpoint to find its real policy. It measures **burst capacity**, **recovery
  time**, and whether capacity comes back **all at once (fixed window)** or **gradually (token bucket / sliding window)**,
  including the refill rate. It also checks the `X-RateLimit-*`, `RateLimit-*` and `Retry-After` headers against what
  actually happened: a Retry-After that is too optimistic, an advertised limit that doesn't match, or a 429 that still
  reports remaining quota. `dispatch ratelimit <collection> --request Login --expect-limit` fails CI when an endpoint
  has no rate limit.

## Developer tools

* **Import**: Postman (collections and environments), OpenAPI / Swagger (JSON or YAML), Insomnia (v4 JSON and v5 YAML),
  Thunder Client, Hoppscotch, Bruno (a `.bru` collection folder, a single `.bru` file or a JSON export), HAR, WSDL,
  `.proto`, `.http` files, cURL commands, and Dispatch files or folders, from a file, a URL or pasted text.
* **Export**: a Dispatch file, a **git-friendly folder** (one file per request), a Postman collection v2.1, or a
  `.http` file.
* **Code generation**: cURL, HTTPie, C#, Python, JavaScript, Go, grpcurl and websocat.
* **Auth**: Bearer, Basic, API key, **OAuth 2.0** (client credentials, password, authorization code with PKCE,
  device code; tokens are cached and refreshed), **AWS Signature v4**, Digest, NTLM / Negotiate, and **mTLS** client
  certificates. A JWT decoder shows the claims of tokens found in responses.
* **Network**: per-request timeout, redirects, TLS verification, proxy, HTTP version, and a cookie jar with a
  manager. A **timeline** shows DNS, connect, TLS, time to first byte and download, plus the raw request.
* **Capture proxy**: point a browser, app or `HTTP(S)_PROXY` at Dispatch and it records traffic — HTTP, and HTTPS
  decrypted with an on-the-fly per-host certificate from a local CA you install. Filter traffic, save it to a collection
  or export **HAR**, and get a **traffic report** (hosts, endpoints, status codes, content types, slowest and failed calls)
  as HTML or JSON. `dispatch capture` records from the CLI.
* **Fake data**: 100+ Postman-compatible dynamic values — `{{$randomFullName}}`, `{{$randomEmail}}`,
  `{{$randomCompanyName}}`, `{{$randomInt(1,100)}}`, `{{$randomDate(-30,30)}}` and more, with `{{$` autocomplete.
* **Documentation**: generate a self-contained **HTML reference** (sidebar, search, examples, code samples; secrets
  redacted) or Markdown from a collection. Request descriptions are Markdown. `dispatch docs` generates from the CLI.
* **Rich responses**: a sortable **table** view for JSON arrays (with CSV export), inline image preview, HTML as text,
  PDF / binary opened externally, **Save to file**, and `pm.visualizer.set(template, data)` custom views.
* **Smart mocks**: beyond saved examples, the mock server can generate fresh, schema-driven fake data per request
  (agreeing with the URL: `GET /users/42` answers `id: 42`) and remember **stateful CRUD** (POST / GET / PUT / PATCH /
  DELETE), with `{{body.x}}`, `{{header.x}}` templating. Stateful lists understand filters (`?status=sold`,
  `price_gte=10`, `name_like=re`, `q=text`), sorting (`sort=-price,name`) and paging (`page`/`limit`, `_page`/`_limit`,
  `offset`) and report `X-Total-Count`; nested resources belong to their parent (`POST /users/7/orders` stamps
  `userId: 7`); creates answer with `Location` and refuse duplicate ids with 409. Seed the store from a json-server
  style file with `dispatch mock … --state db.json`.
* **Variables**: layered scopes (globals < collection < environment < data row < runtime), `{{$guid}}`,
  `{{$timestamp}}`, `{{$randomInt}}` and other dynamic values. **Secret** variables are encrypted at rest with a key
  kept in the OS keychain (DPAPI / Keychain / Secret Service).
* **Workspace**: collections with nested folders, history with a filter, multiple tabs, a **command palette**
  (Ctrl+K) that searches every request and command, JSONPath / XPath filtering of response bodies, and a light or
  dark theme.

### Help & onboarding

* **Welcome screen** on first launch with the four basics (send, save, variables, tests); reopen it from the **?**
  menu at the top right.
* **User guide** (`F1`, the **?** menu, or `Help: …` in the command palette): searchable topics with step-by-step
  instructions, copyable examples and tips, covering every feature from variables to the CLI. Tool windows (runner,
  mock server, flows, ...) have a "How does this work?" link to their topic, and request tabs link to theirs with
  "Learn more".
* **Example collection**: one click adds "Dispatch examples", ten ready-to-send requests against public echo services
  that show assertions, dynamic fake data, auth, test and pre-request scripts, chaining with extraction, GraphQL and
  WebSocket, each with a description in its Docs tab. Topics with a **Try it** button open the matching example.

### Shortcuts

| Keys | Action |
|---|---|
| `Ctrl+Enter` / `Enter` in URL | Send / connect |
| `Ctrl+K` or `Ctrl+Shift+P` | Command palette |
| `Ctrl+S` | Save |
| `Ctrl+T` | New HTTP request (the arrow next to **New** offers every protocol) |
| `Ctrl+W` | Close tab |
| `Ctrl+O` | Import |
| `F1` | User guide |

## CLI

`dispatch` runs collections headlessly, for CI pipelines and scripts:

```bash
dotnet run --project src/Dispatch.Cli -- run tests/api.dispatch.json -e staging.json -r cli,junit,html -o reports
dotnet run --project src/Dispatch.Cli -- run "My Collection" --data users.csv --bail
dotnet run --project src/Dispatch.Cli -- mock petstore.yaml --port 4010 --latency 200 --error-rate 5%
dotnet run --project src/Dispatch.Cli -- mock petstore.yaml --dynamic --state db.json
dotnet run --project src/Dispatch.Cli -- load "My Collection" --users 50 --duration 60s --max-p95 300
dotnet run --project src/Dispatch.Cli -- run tests/api.dispatch.json --update-snapshots
dotnet run --project src/Dispatch.Cli -- flow "My Collection" --name "Login smoke"
dotnet run --project src/Dispatch.Cli -- scan petstore.yaml --fail-on high -r cli,html
dotnet run --project src/Dispatch.Cli -- docs "My Collection" --format html -o api.html
dotnet run --project src/Dispatch.Cli -- minimize "My Collection" --request "Create order" -r cli,html
dotnet run --project src/Dispatch.Cli -- ratelimit "My Collection" --request Login --expect-limit
dotnet run --project src/Dispatch.Cli -- impact "My Collection" --request "Get user" --baseline snapshot -r cli,html
dotnet run --project src/Dispatch.Cli -- laws traffic.har --fail-on-anomaly
dotnet run --project src/Dispatch.Cli -- monitor --watch
dotnet run --project src/Dispatch.Cli -- capture --port 8899 --out traffic.har
dotnet run --project src/Dispatch.Cli -- fuzz-client --port 8899 --host api.myapp.com --duration 10m -r cli,html
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

### UI screenshots without a display

```bash
dotnet run --project tools/UiShot -- shots        # dark theme
dotnet run --project tools/UiShot -- shots light  # light theme
```

Renders the main window and the tool windows with the headless Avalonia platform (an isolated data directory and a
tiny local API are used) and writes PNGs to the given folder, so UI changes can be reviewed on a CI box or over SSH.

### Windows installer

```powershell
.\installer\build-installer.ps1 -Version 1.2.0
```

Builds `installer\out\Dispatch-Setup-1.2.0.exe` with Inno Setup: a self-contained app and `dispatch` CLI (no .NET
needed on the target PC), Start menu shortcuts, optional CLI on `PATH` and "Open with" for collection files. See
[installer/README.md](installer/README.md). The **Windows installer** GitHub workflow builds it on `v*` tags.
