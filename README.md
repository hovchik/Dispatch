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

### Webhook receiver

A local HTTP endpoint (**More → Webhook receiver…**) that records every callback it gets, whatever the method or path:
use `http://localhost:9000/hooks/orders` as the callback URL while developing against a payment, messaging or CI
provider. Each hook is listed with its headers and body; **replay** it to your own handler, open it as a request, or
**save it as an example** on the matching request of the collection (created in a *Webhooks* folder when missing), so
it is documented and the mock server can serve the same payload. Choose the status, body and delay every callback is
answered with to see how the sender retries. Save all hooks as JSON or an HTML report. Dispatch bundles no tunnel; for
callbacks from the internet expose the port with `cloudflared tunnel --url http://localhost:9000` or `ngrok http 9000`
and give the provider the public URL.

Webhooks are also a **message check**: in a request's Checks tab pick *Webhook receiver* as the listener, put the
callback path in the filter (`/hooks/orders`, or `POST /hooks/*`) and the condition on the payload, e.g. after
`POST /orders` a webhook arrives at `/hooks/orders` with `$.status == paid` within 5 s. The receiver starts on demand
(port `DISPATCH_WEBHOOK_PORT`, default 9000), only callbacks after the request count, and the result shows with the
other tests in the app, runner, flows, monitors and `dispatch run`. `dispatch webhooks --port 9000 --out hooks.json`
records from the CLI; `--expect n --path /hooks/orders --duration 30s` makes it exit 1 when fewer arrive.

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

### Spec coverage and breaking-change detector

**Spec coverage** (right-click a collection → *Spec coverage…*) scores a collection, a run or captured traffic against
its OpenAPI 3 / Swagger 2 document on five metrics, each a percentage with the gaps listed: operations never called,
documented status codes never seen, parameters never varied, required inputs never omitted (no negative test) and
calls the spec does not document. Score the saved requests as defined, run the collection for real responses, or add a
HAR from the capture proxy / browser or a `dispatch run -r json` report. `dispatch coverage api.yaml <collection|run.json|traffic.har> [--run] --min 80`
fails CI below the threshold.

**Compare spec versions** (Tools → *Compare spec versions…*) diffs two versions of a spec and classifies every change
as breaking (removed or moved paths, removed, renamed or retyped fields, new required parameters or body fields,
narrowed request enums, widened response enums, changed success status, new authentication), non-breaking or info.
Pick a collection and the change impact analyzer lists what each change breaks: requests calling removed operations or
missing a new required input, status assertions on a changed code, and the tests, extractions, dependent requests and
flows that read a changed response field (renames come with the corrected path).
`dispatch breaking old.yaml new.yaml --collection c --fail-on breaking` gates CI.

### OpenAPI generator and test scaffolding

**Collection menu → Generate OpenAPI spec…** writes an OpenAPI 3.0 document (YAML or JSON) from what Dispatch already
knows: the collection's requests, their saved examples and the run history. Paths are templated (`/users/42` becomes
`/users/{userId}`, `{{petId}}` becomes `{petId}`), query and header parameters are listed with inferred types, request
and response schemas are merged from every body seen (required fields, `nullable`, `uuid` / `date-time` / `email`
formats, enumerations) with examples, and the requests' auth becomes `securitySchemes`. The same works from captured
traffic: `dispatch openapi traffic.har -o api.yaml`, or `dispatch openapi "My Collection" --format json --server
https://api.example.com`. The generated file imports back into Dispatch (and any other OpenAPI tool).

The other direction, **Import → Also create tests** (or `dispatch scaffold spec.yaml -o tests.dispatch.json`), turns a
spec into a test collection: every operation gets its happy-path request with a status assertion and a JSON-schema
assertion on the documented success body, plus one negative request per required query/header parameter and required
body field that leaves it out and expects a 4xx. `dispatch scaffold spec.yaml --save` puts the collection straight into
the app; `dispatch run tests.dispatch.json` runs it in CI.

### Environment parity, load baselines and streaming load

* **Compare environments**: run a collection against two environments, or two base URLs, side by side. Both sides are
  called at the same moment and every response is diffed: status, the structure of JSON bodies (with ignore paths such
  as `$.timestamp` or `$..id`), non-volatile headers and latency. Each side chains its own extracted variables, so a
  staging login never leaks into production. Rows that differ open a side-by-side body diff; reports export to HTML /
  JSON. `dispatch compare <collection> -e Staging -e Production --ignore $.timestamp --fail-on-diff` gates CI (`-e` also
  accepts an `https://` base URL, which re-points absolute request URLs and defines `{{baseUrl}}`).
* **Load baselines**: in the load-test report, **Baseline → Set as baseline** saves the run as a JSON file (any exported
  JSON report works). Later runs choose **Compare with baseline** and show p50 / p95 / p99, throughput and error-rate
  deltas overall and per request, with a verdict: *REGRESSED* when a metric is worse by more than the allowed percentage
  (10 % by default, latency changes under 5 ms are ignored as noise), *IMPROVED* or *UNCHANGED*. From the CLI:
  `dispatch load … --save-baseline base.json`, then `--baseline base.json --max-regression 10%` fails the build on a
  regression; `-r cli,html,json,csv -o reports` writes the reports.
* **Streaming load**: WebSocket, SSE, Socket.IO, MQTT, Kafka, AMQP and gRPC streaming requests can be part of a load-test
  journey. Each virtual user holds one connection at a time, listens for the request's listen time (or message limit),
  then reconnects. The report adds concurrent connections, connect errors, drops, messages per second and message
  latency (reply after a sent message, or the gap between server pushes); the per-request latency columns then measure
  connection setup. Same report format, same baselines.

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

### Collection hygiene: linter and folder sync

**Lint collection** (collection menu, or `dispatch lint`) runs static checks without sending anything: variables used but
defined nowhere (DP001), collection variables nothing uses (DP002), **hard-coded secrets** in URLs, headers, bodies, auth
settings or collection variables (DP003, using the security scan's secret patterns), requests with no assertion, test
script check or message expectation (DP004), duplicate requests (DP005) and names (DP006), saved examples that no longer
match their request (DP007), missing descriptions (DP008, off by default), empty URLs (DP009) and unused extractions
(DP010). Each rule has an id and a severity you can change or switch off per collection; the app remembers the choice
and can export it as `dispatch-lint.json`, which the CLI picks up next to the collection. Problems are clickable and
open the request. `dispatch lint <collection> --fail-on warning -r cli,html,json` gates CI (`--disable DP008`,
`--severity DP004=error`, `--list-rules`).

**Sync with folder** (collection menu, or `dispatch sync <collection> <folder> [--watch]`) keeps a saved collection and
a git-friendly folder (the folder export format, one file per request) the same in both directions. Edit, add or delete
files in your editor or pull them from git and the app follows; save in the app and the files are rewritten. Dispatch
records what both sides agreed on at the last sync, so it knows which side changed; a request changed on both sides is
a **conflict**, left untouched on both sides and shown with both versions until you keep one (`--resolve app|folder` in
the CLI). Watch mode applies file changes within a second and writes app changes back every few seconds. Secret
collection variables are never written to the folder.

### Network conditions and TLS inspector

* **Network conditions in the capture proxy**: the capture and client-fuzzing proxies can impose added **latency**, a
  **bandwidth cap** on responses, random **dropped connections** and periodic **offline windows**, so you can watch a
  browser, mobile app or SDK cope with a bad link. Presets: `2g`, `3g`, `4g`, `dsl`, `satellite`, `flaky-wifi`
  (5% drops, offline 3 s every 30 s) and `offline` (10 s every minute), or a custom mix. Pick a profile in the Capture
  window, or from the CLI: `dispatch capture --network 3g` / `dispatch capture --latency 300ms --bandwidth 1mbps --loss 2%
  --offline-every 60s --offline-for 10s` (the same options work on `dispatch fuzz-client`).
* **TLS / certificate inspector**: one handshake with a host shows the certificate chain (subject, issuer, validity,
  SANs, key and signature), days to expiry, TLS version, cipher suite, ALPN (is HTTP/2 offered?), hostname and trust
  problems and revocation status, with plain-language insights. Open it from Tools → TLS inspector…, from the flask
  menu of any request (prefilled with its host), and the response **Timeline** tab shows the TLS summary of every HTTPS
  send. Monitors can **fail and alert when a certificate expires within N days**. `dispatch tls api.example.com
  --warn-days 21` (or `dispatch tls <collection>` for every HTTPS host it uses) exits 1 on problems and writes
  `-r cli,html,json` reports.

### Endpoint probes: pagination, idempotency and caching

Three more probes in the same flask menu. Each one sends a small number of real requests, lists its findings as
problems, warnings and passes, and exports an HTML or JSON report. In the CLI, `--fail-on bad|warning|never` sets when
the exit code is 1.

* **Pagination walker**: follows a list endpoint to the last page. It works out the paging from a `Link: rel="next"`
  header, a next URL in the body, a cursor (`next_cursor`, `nextPageToken`, …) sent back as a parameter, or
  page / offset parameters. It flags items that appear on two pages, an advertised total (`total`, `totalCount`,
  `X-Total-Count`, …) that doesn't match what can be reached, loops, short pages in the middle, pages larger than the
  requested limit and deep pages that get slower. It fetches page 1 again to check that the order is stable, and tries a
  page past the end, invalid page numbers and page sizes of 0 and 100000.
  `dispatch paginate <collection> --request "List orders" --page-size 5`
* **Idempotency & race probe**: sends a POST, PUT, PATCH or DELETE with an `Idempotency-Key`, retries it with the same
  key, reuses the key with a changed body and sends several copies at the same moment, with one key and with none. It
  compares the resources created (from `Location` or an id in the body) and flags retries that duplicate, reused keys
  that are accepted, and 5xx under concurrency. PUT and DELETE are repeated as HTTP requires them to be idempotent. It
  creates data, so point it at a test environment.
  `dispatch idempotency <collection> --request "Create order" --parallel 8`
* **Caching checker**: reads `Cache-Control`, `Expires`, `Vary`, `ETag` and `Last-Modified`, then checks that
  `If-None-Match` and `If-Modified-Since` really answer 304, that an unknown ETag doesn't, that the ETag is stable while
  the body is, and that HEAD matches GET. It flags authenticated responses that shared caches may store, cookies on
  cacheable responses, compressed bodies without `Vary: Accept-Encoding` and a specific CORS origin without
  `Vary: Origin`, and shows CDN cache headers such as `X-Cache` and `CF-Cache-Status`.
  `dispatch cache <collection> --request "Get product" --fail-on warning`

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
* **Workspace**: collections with nested folders, history with a filter, multiple tabs (an unsaved tab names itself
  after what it sends, e.g. `GET /users`, until you rename it), a **command palette**
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
dotnet run --project src/Dispatch.Cli -- paginate "My Collection" --request "List orders" --page-size 5
dotnet run --project src/Dispatch.Cli -- idempotency "My Collection" --request "Create order" -e staging.json
dotnet run --project src/Dispatch.Cli -- cache "My Collection" --request "Get product" --fail-on warning
dotnet run --project src/Dispatch.Cli -- impact "My Collection" --request "Get user" --baseline snapshot -r cli,html
dotnet run --project src/Dispatch.Cli -- laws traffic.har --fail-on-anomaly
dotnet run --project src/Dispatch.Cli -- coverage api.yaml tests/api.dispatch.json --run --min 80 -r cli,html
dotnet run --project src/Dispatch.Cli -- breaking api-v1.yaml api-v2.yaml --collection tests/api.dispatch.json --fail-on breaking
dotnet run --project src/Dispatch.Cli -- openapi "My Collection" -o api.yaml --server https://api.example.com
dotnet run --project src/Dispatch.Cli -- scaffold api.yaml -o tests.dispatch.json
dotnet run --project src/Dispatch.Cli -- monitor --watch
dotnet run --project src/Dispatch.Cli -- capture --port 8899 --out traffic.har
dotnet run --project src/Dispatch.Cli -- fuzz-client --port 8899 --host api.myapp.com --duration 10m -r cli,html
dotnet run --project src/Dispatch.Cli -- lint tests/api.dispatch.json --fail-on warning -r cli,json
dotnet run --project src/Dispatch.Cli -- sync "My Collection" ./api-tests --watch
dotnet run --project src/Dispatch.Cli -- compare "My Collection" -e Staging -e Production --ignore $.timestamp --fail-on-diff -r cli,html
dotnet run --project src/Dispatch.Cli -- load "My Collection" --users 20 --duration 60s --baseline base.json --max-regression 10% -r cli,json
dotnet run --project src/Dispatch.Cli -- capture --network 3g --out traffic.har
dotnet run --project src/Dispatch.Cli -- tls api.example.com --warn-days 21 -r cli,json
dotnet run --project src/Dispatch.Cli -- webhooks --port 9000 --out hooks.json
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

If the app crashes, the exception and stack trace are appended to `%LOCALAPPDATA%\Dispatch\logs\crash-<date>.log`
(the newest 20 files are kept). Errors caught on the UI thread are shown in the app and logged there too.

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
