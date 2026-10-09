namespace Dispatch.Application.Help;

/// <summary>One piece of a help topic: a paragraph, numbered steps, a copyable example or a tip.</summary>
public abstract record HelpBlock;

public sealed record HelpParagraph(string Text) : HelpBlock;

public sealed record HelpSteps(IReadOnlyList<string> Items) : HelpBlock
{
    public IReadOnlyList<HelpStep> Numbered => Items.Select((text, i) => new HelpStep(i + 1, text)).ToList();
}

public sealed record HelpStep(int Number, string Text);

/// <summary>A code or text sample the reader can copy into the app.</summary>
public sealed record HelpExample(string Caption, string Code) : HelpBlock;

public sealed record HelpTip(string Text) : HelpBlock;

/// <summary>
/// A help article. <see cref="TryIt"/> names a request in <see cref="SampleCollection"/> that demonstrates the topic.
/// </summary>
public sealed record HelpTopic(
    string Id,
    string Category,
    string Title,
    string Summary,
    IReadOnlyList<HelpBlock> Blocks,
    IReadOnlyList<string> Keywords,
    string? TryIt = null,
    IReadOnlyList<string>? Related = null)
{
    public bool Matches(string query)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return terms.All(t => Text.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    private string Text => string.Join('\n', new[] { Title, Summary, Category }
        .Concat(Keywords)
        .Concat(Blocks.Select(b => b switch
        {
            HelpParagraph p => p.Text,
            HelpSteps s => string.Join('\n', s.Items),
            HelpExample e => e.Caption + "\n" + e.Code,
            HelpTip t => t.Text,
            _ => ""
        })));
}

/// <summary>The built-in user guide shown in the Help window and linked from hints across the app.</summary>
public static class HelpCatalog
{
    public const string GettingStarted = "getting-started";
    public const string Requests = "requests";
    public const string Variables = "variables";
    public const string Collections = "collections";
    public const string Assertions = "assertions";
    public const string Extraction = "extraction";
    public const string Scripts = "scripts";
    public const string Auth = "auth";
    public const string Runner = "runner";
    public const string Mock = "mock";
    public const string LoadTest = "load-test";
    public const string Flows = "flows";
    public const string Monitors = "monitors";
    public const string SecurityScan = "security-scan";
    public const string MessageChecks = "message-checks";
    public const string ChangeImpact = "change-impact";
    public const string ApiLaws = "api-laws";
    public const string ClientFuzz = "client-fuzz";
    public const string Minimize = "minimize";
    public const string RateLimit = "rate-limit";
    public const string Capture = "capture";
    public const string ImportExport = "import-export";
    public const string Docs = "docs";
    public const string Protocols = "protocols";
    public const string Cli = "cli";
    public const string Shortcuts = "shortcuts";

    public static HelpTopic? Find(string? id) => All.FirstOrDefault(t => t.Id == id);

    public static IEnumerable<HelpTopic> Search(string? query) =>
        string.IsNullOrWhiteSpace(query) ? All : All.Where(t => t.Matches(query));

    public static IReadOnlyList<HelpTopic> All { get; } =
    [
        new(GettingStarted, "Basics", "Getting started", "Send your first request in under a minute.",
        [
            new HelpParagraph("Dispatch sends requests over HTTP, GraphQL, gRPC, SOAP, WebSocket, SSE, Socket.IO, MQTT, Kafka, AMQP and raw TCP/UDP, and checks the responses for you. Everything you open lives in a tab; tabs can be saved into collections."),
            new HelpSteps(
            [
                "Click New (or press Ctrl+T). The arrow next to New lists every protocol.",
                "Pick a method and type a URL, for example https://jsonplaceholder.typicode.com/posts/1.",
                "Press Send or Ctrl+Enter. The response appears on the right with its status, time and size.",
                "Press Ctrl+S to save the request into a collection so you can find it again in the sidebar."
            ]),
            new HelpExample("A URL to try", "https://jsonplaceholder.typicode.com/posts/1"),
            new HelpTip("Load the example collection (Help → Load example collection) for ready-made requests that show tests, variables, chaining and scripts. Every topic with a Try it button opens one of them."),
            new HelpTip("Press Ctrl+K at any time to search requests and commands, and F1 to come back to this guide.")
        ],
        ["start", "first", "welcome", "new", "tutorial", "intro"], "Simple GET", [Requests, Collections, Variables]),

        new(Requests, "Basics", "Building requests", "Methods, query parameters, headers and bodies.",
        [
            new HelpParagraph("The URL bar and the Params tab stay in sync: type ?page=2 in the URL and a row appears in Params, or add a row and the URL updates. Untick a row to leave it out without deleting it."),
            new HelpParagraph("Body offers JSON, XML, text, form-urlencoded, multipart (with files) and binary. Dispatch sets the Content-Type for you unless you add one in Headers."),
            new HelpExample("A JSON body with fake data", "{\n  \"name\": \"{{$randomFullName}}\",\n  \"email\": \"{{$randomEmail}}\",\n  \"age\": {{$randomInt(18,90)}}\n}"),
            new HelpParagraph("The response panel shows the body (pretty, raw, table for JSON arrays, or a preview), headers, cookies, test results and a timeline with DNS, connect, TLS and time to first byte. Filter the body with a JSONPath such as $.items[0].name."),
            new HelpTip("Turn a cURL command into a request with Import → paste. Generate code for any request (cURL, Python, C#, Go, JS…) from the command palette.")
        ],
        ["url", "method", "params", "query", "headers", "body", "json", "form", "multipart", "file", "curl", "response", "timeline"],
        "POST JSON with fake data", [Variables, Auth]),

        new(Variables, "Basics", "Variables & environments", "Reuse values with {{placeholders}}.",
        [
            new HelpParagraph("Write {{name}} anywhere in a request: the URL, headers, body, auth or assertions. Before sending, Dispatch replaces it with the value from the narrowest scope that defines it."),
            new HelpExample("Scopes, from weakest to strongest", "globals < collection < environment < data row < runtime"),
            new HelpSteps(
            [
                "Open the Environments tab in the sidebar and click + to create one, e.g. \"Staging\".",
                "Add variables such as baseUrl = https://staging.example.com. Tick Secret for passwords and tokens: they are encrypted at rest and left out of exports.",
                "Choose the environment in the drop-down at the top right. Requests now resolve {{baseUrl}} from it.",
                "Collection variables live under the collection's menu → Settings & variables."
            ]),
            new HelpExample("A URL that uses variables", "{{baseUrl}}/users/{{userId}}"),
            new HelpParagraph("Dynamic values generate something new on every send. Type {{$ to get autocomplete."),
            new HelpExample("Dynamic values", "{{$guid}}  {{$timestamp}}  {{$randomInt(1,100)}}\n{{$randomEmail}}  {{$randomFullName}}  {{$randomDate(-30,30)}}"),
            new HelpTip("Switching the environment is the quickest way to point a whole collection at dev, staging or production.")
        ],
        ["environment", "variable", "placeholder", "secret", "global", "dynamic", "faker", "random", "scope", "baseurl"],
        "Simple GET", [Extraction, Scripts]),

        new(Collections, "Basics", "Collections, folders & history", "Organise and find your requests.",
        [
            new HelpParagraph("A collection groups saved requests, optionally in nested folders, with its own variables and an OpenAPI document for contract checks. Right-click a collection for the runner, mock server, load test, flows, monitors, security scan, docs and export."),
            new HelpSteps(
            [
                "Press Ctrl+S on a tab and pick a collection (a new one is created if you have none).",
                "Right-click a request → Move to folder… and type a path such as Users/Admin.",
                "Use the search box above the tree to filter requests by name or URL."
            ]),
            new HelpParagraph("History records every request you send. Click an entry to open it again, or right-click two entries to compare their responses side by side."),
            new HelpTip("Unsaved tabs show their title in italics; a dot on a tab means it has unsaved changes.")
        ],
        ["collection", "folder", "save", "history", "organise", "organize", "tree", "compare"],
        null, [ImportExport, Runner]),

        new(Assertions, "Testing", "Assertions (no-code tests)", "Check status, time, headers and body without writing code.",
        [
            new HelpParagraph("Open the Tests tab and click Add. Each row reads as: source, path, operator, expected value. Assertions run after every send, in the collection runner and in the CLI; results appear in the response's Tests tab."),
            new HelpExample("Examples of assertion rows", "Status                         equals        200\nResponse time (ms)             less than     500\nHeader   Content-Type          contains      json\nJSONPath $.data[0].id          exists\nJSONPath $.items               has length    10\nJSON Schema  {\"type\":\"object\"}   is valid"),
            new HelpParagraph("+ Status and + Time pre-fill rows from the last response. + Contract validates the response against the collection's OpenAPI document. + Snapshot records the current response and fails when a later one differs; list changing fields such as $.timestamp as ignore paths."),
            new HelpTip("Expected values can use variables, e.g. JSONPath $.id equals {{userId}}.")
        ],
        ["test", "assert", "check", "expect", "status", "jsonpath", "xpath", "schema", "contract", "openapi", "snapshot"],
        "Simple GET", [Scripts, Runner]),

        new(Extraction, "Testing", "Extraction & chaining", "Pass values from one response to the next request.",
        [
            new HelpParagraph("The Extract tab copies a value out of the response into a variable. Later requests use it as {{name}}. Typical use: log in once, extract the token, and send it as a Bearer token everywhere else."),
            new HelpSteps(
            [
                "In the login request open Extract and click Add.",
                "Variable: token · Source: JSONPath · Path: $.access_token.",
                "Scope: Environment writes it to the active environment (kept), Runtime keeps it for this session only.",
                "In other requests set Auth → Bearer token to {{token}}."
            ]),
            new HelpExample("Paths you can extract from", "JSONPath   $.data.id\nHeader     Location\nXPath      //order/@id\nRegex      token=([a-z0-9]+)"),
            new HelpTip("Try the two \"Chaining\" requests in the example collection: send step 1, then step 2 uses the id it extracted.")
        ],
        ["extract", "chain", "chaining", "token", "login", "capture value", "pass", "variable"],
        "Chaining 1: extract a user id", [Variables, Flows]),

        new(Scripts, "Testing", "Scripts (pm API)", "Pre-request and test scripts in JavaScript, Postman-compatible.",
        [
            new HelpParagraph("Pre-request scripts run before sending and can set variables or change the request. Test scripts run after the response and can define tests with pm.test and pm.expect. Postman scripts usually work unchanged."),
            new HelpExample("Test script", "pm.test(\"Status is 200\", () => pm.response.to.have.status(200));\n\npm.test(\"Has a title\", () => {\n  const json = pm.response.json();\n  pm.expect(json).to.have.property(\"title\");\n});\n\npm.environment.set(\"todoId\", pm.response.json().id);"),
            new HelpExample("Pre-request script", "pm.variables.set(\"requestId\", Date.now().toString());\npm.request.headers.upsert({ key: \"X-Trace\", value: \"dispatch\" });"),
            new HelpTip("The Snippets menu in the Tests tab inserts common checks. console.log output appears in the response's Console tab."),
            new HelpTip("pm.visualizer.set(template, data) renders a custom HTML view of the response.")
        ],
        ["script", "javascript", "js", "pm", "postman", "pre-request", "test script", "expect", "console", "visualizer"],
        "Test script", [Assertions, Extraction]),

        new(Auth, "Basics", "Authorization", "Bearer, Basic, API key, OAuth 2.0, AWS, Digest, NTLM and mTLS.",
        [
            new HelpParagraph("Choose a type in the Auth tab. Dispatch adds the right header (or query parameter) when sending, so you don't have to build it by hand."),
            new HelpSteps(
            [
                "Bearer: paste a token or use {{token}}.",
                "API key: name, value, and whether it goes in a header or the query string.",
                "OAuth 2.0: fill in the token URL, client id, secret and scope. Dispatch fetches the token when you send, caches it and refreshes it when it expires. Authorization code (with PKCE) and device code open your browser.",
                "Client certificates (mTLS) are under the Settings tab."
            ]),
            new HelpTip("Store secrets in an environment variable marked Secret and reference it, so the value never ends up in exports."),
            new HelpTip("JWTs in responses are decoded for you in the response's JWT tab.")
        ],
        ["auth", "authorization", "bearer", "token", "basic", "api key", "oauth", "oauth2", "pkce", "aws", "sigv4", "digest", "ntlm", "mtls", "certificate", "jwt"],
        "Bearer token", [Variables, Extraction]),

        new(Runner, "Testing", "Collection runner & data-driven runs", "Run many requests in order and get a report.",
        [
            new HelpParagraph("The runner sends the requests of a collection one after another, evaluates their assertions and scripts, and shows a pass/fail summary. Open it from Runner in the toolbar or the collection's menu → Run collection…"),
            new HelpSteps(
            [
                "Tick and reorder the requests to run.",
                "Set iterations, a delay between requests, and whether to stop at the first failure.",
                "Optionally choose a CSV or JSON data file: each row is one iteration and its columns become variables.",
                "Click Run. When it finishes, the Report tab shows the pass rate, insights (flaky or always-failing requests), per-request results, every failure and the slowest calls.",
                "Open the report in your browser, or export it as HTML, JUnit XML or JSON."
            ]),
            new HelpExample("users.csv: each column is a {{variable}}", "email,password,expectedStatus\nalice@example.com,secret1,200\nbob@example.com,wrong,401"),
            new HelpTip("The same run works headless: dispatch run \"My Collection\" --data users.csv -r junit,html.")
        ],
        ["runner", "run", "collection run", "iterations", "data", "csv", "data-driven", "report", "junit", "html report"],
        null, [Assertions, Cli, Flows]),

        new(Mock, "Tools", "Mock server", "Serve fake responses before the real API exists.",
        [
            new HelpParagraph("The mock server answers on a local port with the saved examples of a collection. Save a response with \"Save as example\" below the response, or write one in the request's Examples tab."),
            new HelpSteps(
            [
                "Save at least one example on a request.",
                "Open Mock in the toolbar (or the collection's menu → Mock server…) and click Start.",
                "Point your app at the base URL in the header, e.g. http://localhost:3000. The Routes tab lists every path with its hit count; the Request log shows what each call was answered with."
            ]),
            new HelpParagraph("Several examples on one request: match rules (query, path parameter, header, body contains) decide which one answers; a rule value of * only asks for the key to be there. Clients can also ask for a specific example with a Prefer: code=404 or Prefer: example=Name header (or X-Mock-Status / X-Mock-Example). Requests that share a method and path pool their examples, GraphQL operations on one endpoint are told apart by their operation name or root field, and HEAD is answered by the GET route."),
            new HelpParagraph("You can add latency, jitter, an error rate and dropped connections to test how your client copes. Dynamic mode generates fresh fake data from schemas, agreeing with the URL (GET /users/42 answers id 42); stateful mode remembers POST/PUT/PATCH/DELETE like a tiny database: nested resources belong to their parent (POST /users/7/orders stamps userId 7), lists filter (?status=sold, price_gte=10, name_like=re, q=text), sort (sort=-price,name) and page (page/limit, _page/_limit, offset) with an X-Total-Count header, creates answer with a Location header and duplicate ids get 409. The CLI seeds the store from a json-server style file with --state db.json."),
            new HelpExample("Templating in an example body", "{ \"id\": \"{{$guid}}\", \"name\": \"{{body.name}}\", \"agent\": \"{{header.User-Agent}}\" }"),
            new HelpExample("Stateful list queries", "GET /pets?kind=dog&price_lte=20&sort=-price&page=2&limit=10\n# X-Total-Count: 37"),
            new HelpParagraph("Recorded sessions: after a WebSocket or SSE session, click Save as example. The example keeps every message with its timing, and the mock server replays it on the request's path. SSE events stream out as recorded. A WebSocket replay sends the server's opening messages on connect; each client message then plays the part of the recording that followed the matching recorded message (an exact match, the same JSON apart from ids, or else the next part in order). Ids the client sends (id, requestId, correlationId, …) are put into the replies, and replies can copy values with {{message.field}}. Session replay speed sets the pace: 1× as recorded, 0 for no delays."),
            new HelpExample("Replay a recorded ticker twice as fast", "dispatch mock \"My Collection\" --session-speed 2\n# then connect to ws://localhost:3000/feed")
        ],
        ["mock", "stub", "fake", "example", "latency", "server", "crud", "offline", "websocket", "sse", "replay", "record", "session", "stream", "prefer", "filter", "paging", "graphql"],
        "Simple GET", [Collections]),

        new(LoadTest, "Tools", "Load testing", "Virtual users, ramp-up and latency percentiles.",
        [
            new HelpParagraph("Load test sends the chosen requests from many virtual users at once and charts requests per second, p50/p95/p99 latency and errors live."),
            new HelpSteps(
            [
                "Open Load test in the toolbar.",
                "Choose virtual users, duration, ramp-up and think time.",
                "Start and watch the Live tab.",
                "When the test ends (or you stop it), the Report tab shows a verdict, insights, the latency distribution, status codes and per-request percentiles. Open it in your browser or export HTML, JSON or CSV."
            ]),
            new HelpTip("Only load test systems you own or are allowed to test. Start small (5–10 users) and increase."),
            new HelpExample("From CI, failing if p95 is above 300 ms", "dispatch load \"My Collection\" --users 50 --duration 60s --max-p95 300")
        ],
        ["load", "performance", "stress", "virtual users", "rps", "latency", "p95", "percentile", "benchmark", "report"],
        null, [Runner, Cli]),

        new(Flows, "Testing", "Test flows", "Chain requests with conditions, loops and retries.",
        [
            new HelpParagraph("A flow is a visual script of steps that share one set of variables: send a request, if/else, repeat, for-each, until (retry with a wait), set a variable, run a script, delay, or stop/fail."),
            new HelpSteps(
            [
                "Collection menu → Test flows… → New flow.",
                "Add a Request step for login, then an Until step that polls a job until $.status equals done.",
                "Run it and follow each step's result; run it in CI with dispatch flow."
            ]),
            new HelpTip("Use flows when the order or a condition matters; use the plain runner when every request is independent."),
            new HelpParagraph("Fork a run (what if…?): every run is recorded. Click Fork… next to any request in the run log, edit its response (or pick a preset: 500, 404, 401, 429, empty lists, timeout) and replay. The flow runs again from the start: requests before the fork are answered from the recording, the forked one gets your response, and the rest are either replayed from the recording (offline, nothing is sent) or sent live. The result lists what changed compared with the original run, e.g. a step that now fails, or loop steps that silently no longer run."),
            new HelpExample("Fork from the command line", "dispatch flow \"My Collection\" --name \"Order check\" --record run.json\ndispatch flow \"My Collection\" --replay run.json --fork 2 --status 500 --offline\ndispatch flow \"My Collection\" --replay run.json --fork 2 --body @empty-orders.json")
        ],
        ["flow", "workflow", "if", "loop", "repeat", "for-each", "retry", "until", "poll", "scenario", "fork", "replay", "what if", "record"],
        null, [Extraction, Runner]),

        new(Monitors, "Tools", "Monitors", "Run a collection on a schedule and get alerts.",
        [
            new HelpParagraph("A monitor runs a collection every few minutes or on a cron schedule and alerts Slack, a webhook or email on every run, on failure, or when the result changes, with a recovery notice when it passes again."),
            new HelpExample("Cron: every 15 minutes on weekdays", "*/15 * * * 1-5"),
            new HelpTip("Monitors run while the app is open. For an always-on monitor use the CLI daemon: dispatch monitor --watch.")
        ],
        ["monitor", "schedule", "cron", "alert", "slack", "webhook", "email", "uptime"],
        null, [Runner, Cli]),

        new(SecurityScan, "Tools", "Security scan", "Passive checks and bounded active probes.",
        [
            new HelpParagraph("The scanner checks transport security, security headers, CORS and information leaks, and can send bounded probes for injection, reflection, boundary input and missing authentication."),
            new HelpTip("Scan only APIs you are authorised to test. Active probes send unusual input to the server."),
            new HelpExample("Gate a CI build on high-severity findings", "dispatch scan \"My Collection\" --fail-on high -r cli,html")
        ],
        ["security", "scan", "vulnerability", "headers", "cors", "injection", "owasp", "pentest"],
        null, [Cli]),

        new(MessageChecks, "Testing", "Message checks (cross-protocol)", "Assert that a request causes the right Kafka, MQTT, RabbitMQ or WebSocket message.",
        [
            new HelpParagraph("Many APIs do their real work asynchronously: POST /orders returns 201, and then an event appears on a Kafka topic, an MQTT topic, a RabbitMQ queue or a WebSocket. A message check verifies that consequence as part of the request's tests."),
            new HelpSteps(
            [
                "Save a listener in the same collection: a streaming request that subscribes to the channel, e.g. Kafka in Subscribe mode on orders.created, MQTT on orders/#, AMQP on a queue or exchange, or a WebSocket / SSE / Socket.IO URL. A broker request in Publish mode is switched to subscribe automatically.",
                "Open the request that should cause the message (e.g. POST /orders), and in Extract save what you need from the response, e.g. orderId from $.id.",
                "In the Checks tab click Add, pick the listener, and describe the message: a JSONPath (empty = the whole message), an operator and the expected value, e.g. $.orderId == {{orderId}}.",
                "Optionally filter by topic / routing key / event name, set the time limit, or tick none to require that no matching message arrives (e.g. no payment.failed event).",
                "Send. Listeners subscribe before the request goes out, and each check shows ✓ or ✗ in the Tests results, with how long the message took or what arrived instead."
            ]),
            new HelpParagraph("Message checks run everywhere tests run: in the app, the collection runner, test flows, monitors and dispatch run in CI. Only messages that arrive after the request was sent count, so retained or earlier messages can't produce a false pass."),
            new HelpExample("A check, as it appears in the results", "Message on Order events [orders/created] where $.orderId == 1042 within 2000 ms  ✓ after 37 ms"),
            new HelpTip("For Kafka, use a dedicated consumer group and 'latest' offsets on the listener, so old records are not replayed into the check.")
        ],
        ["message", "event", "consequence", "kafka", "mqtt", "amqp", "rabbitmq", "websocket", "sse", "socket.io", "async", "event-driven", "side effect", "topic"],
        null, [Assertions, Extraction, Protocols]),

        new(ChangeImpact, "Testing", "Change impact map", "See what breaks when a response changes shape.",
        [
            new HelpParagraph("When an API renames, removes or retypes a field, the change impact map lists everything in the collection that depends on it. That covers the request's JSONPath assertions and snapshot, the variables its extraction rules will no longer set, every request, script and message check that uses those variables (followed through chains of extractions), saved examples that still show the old shape, and the test flows that run any of them."),
            new HelpSteps(
            [
                "Send the request so its latest response is loaded.",
                "Choose Tools → Change impact… next to the Send button.",
                "Pick what to compare with: the previous response, the recorded snapshot, or a saved example.",
                "Read the shape changes on the left and the affected items on the right. Breaks will fail; Worth checking are heuristic matches. Renames come with the corrected path."
            ]),
            new HelpExample("In CI: fail when the live API breaks the collection's tests", "dispatch impact \"My Collection\" --request \"Get user\" --baseline snapshot -r cli,html"),
            new HelpTip("Renames are detected when a removed and an added field have the same type and value, or are the only same-typed pair under one parent. Check suggested paths before applying them.")
        ],
        ["impact", "breaking change", "rename", "renamed field", "schema change", "blast radius", "dependency", "json shape", "api change", "regression"],
        null, [Assertions, Extraction, Flows, Cli]),

        new(ApiLaws, "Testing", "API laws", "Learn the rules an API keeps from its traffic, and spot the responses that break them.",
        [
            new HelpParagraph("API laws are inferred from real responses rather than a schema. They cover required fields and types, enumerations, never-negative numbers, formats (UUID, email, date-time, URL), date ordering (createdAt ≤ updatedAt), counts and totals that match their items (count == items.length, total == sum of prices), page sizes that respect ?limit=, request fields echoed back, created resources that can be read back, deleted resources that return 404, and GETs that repeat the same body."),
            new HelpParagraph("A rule that held in all but a few of many responses is an anomaly, with the responses that broke it. For example, a status that is \"shipped\" or \"paid\" 40 times and once \"shiped\". Anomalies are usually bugs."),
            new HelpSteps(
            [
                "Open the collection menu → API laws…. Recent history for the collection's endpoints is analysed right away.",
                "For more evidence, click Run collection (it sends every request a few times), or import a HAR file from the capture proxy or your browser.",
                "Review the anomalies first, then the laws. Each law shows how often it was seen and how confident the inference is.",
                "Select laws and click Add selected as tests. They become assertions or pm.test checks on the matching saved requests, so a later violation fails the request's tests in the app, the runner and CI."
            ]),
            new HelpExample("From the command line", "dispatch laws traffic.har --fail-on-anomaly\ndispatch laws \"My Collection\" --runs 5 --write"),
            new HelpTip("Laws need a few successful responses per endpoint (3 by default), and anomalies need at least 10. More varied traffic gives more reliable laws, so review them before adding.")
        ],
        ["laws", "invariants", "properties", "anomaly", "infer", "learn", "property-based", "daikon", "consistency", "rules"],
        null, [Assertions, Capture, Cli]),

        new(ClientFuzz, "Tools", "Client fuzzing", "Find out how your app breaks when the API answers unexpectedly.",
        [
            new HelpParagraph("Client fuzzing tests the app, not the API. Like the capture proxy, it sits between your web or mobile app and its API. It lets a few normal responses per endpoint through, then changes one response at a time: a null or missing field, an empty or single-item list, an unexpected enum value, a wrong type, an unknown extra field, very long text, a 500 / 503 / 429 / 401, a malformed or empty body, or a slow response."),
            new HelpParagraph("After each change it watches what the app does next. It flags retry storms (many repeats of the same call), broken values sent back to the API (GET /users/undefined, null, NaN, [object Object]), calls to error trackers (Sentry, /errors, /log…), and an app that goes silent where it normally continues. The result reads like: the app breaks when $.user.avatar is null: GET /avatars/undefined."),
            new HelpSteps(
            [
                "Click Fuzz app in the toolbar, choose the port and the kinds of variation, and click Start.",
                "Point the app at the proxy address, as with the capture proxy. For HTTPS, export the CA and trust it on the device.",
                "Use the app as usual, revisiting screens so endpoints are requested several times. Each variation is tried once, and you get a live verdict.",
                "Stop and export an HTML or JSON report."
            ]),
            new HelpExample("From the command line", "dispatch fuzz-client --port 8899 --host api.myapp.com --window 5s --duration 10m -r cli,html\ndispatch fuzz-client --kinds NullField,DropField,EmptyArray --fail-on-break"),
            new HelpTip("Use a test account: variations such as 401 or empty lists can make an app sign out or show empty states. Signals are heuristics, so a quiet app isn't proof that it copes. Check flagged screens yourself.")
        ],
        ["fuzz", "fuzzing", "client", "app", "mobile", "frontend", "resilience", "chaos", "null", "crash", "robustness", "proxy"],
        null, [Capture, Mock]),

        new(Minimize, "Tools", "Minimize a request", "Find the parts of a request its outcome really depends on.",
        [
            new HelpParagraph("Minimize takes a request and its current outcome (a 403, a 500, a failing assertion, or a success) and keeps re-sending it with parts taken away: headers, individual cookies, query parameters, auth, form fields and JSON body members. It ends with the smallest request that still gives the same outcome."),
            new HelpSteps(
            [
                "Open a request and choose Tools → Minimize request… next to the Send button.",
                "Pick what must stay the same: the status code, the status class (2xx, 4xx…), the status plus which tests fail, or a text the body has to contain.",
                "Click Minimize. Parts are removed in halves first, then in smaller groups, one level of nesting at a time.",
                "Required lists what the outcome depends on. Not needed lists everything that made no difference.",
                "Open the minimal request in a new tab, copy it as cURL, or export an HTML / JSON report."
            ]),
            new HelpParagraph("Minimize a failure to find exactly what triggers a bug (\"a 500 only with sort=desc and Accept-Language: fr\"). Minimize a success to learn what an endpoint really requires, such as which of 20 copied browser headers and cookies matter."),
            new HelpExample("From the command line", "dispatch minimize \"My Collection\" --request \"Create order\"\ndispatch minimize api.dispatch.json --request Search --match body --contains \"Internal error\" -r cli,html"),
            new HelpTip("Every probe is a real request. For POST, PUT, PATCH or DELETE use a test environment, because each probe can create or change data.")
        ],
        ["minimize", "minimise", "reduce", "delta debugging", "ddmin", "root cause", "bisect", "required headers", "smallest", "reproduce"],
        null, [Assertions, Cli]),

        new(RateLimit, "Tools", "Rate-limit mapper", "Discover an endpoint's real rate limit and check its headers.",
        [
            new HelpParagraph("The rate-limit mapper probes one request to learn its real policy. It sends a burst until the first throttled response to measure capacity, then retries until requests are accepted again to measure recovery. A second burst shows whether capacity comes back all at once (a fixed window) or gradually (a token bucket or sliding window), and for gradual refill it measures the refill rate."),
            new HelpParagraph("Along the way it reads X-RateLimit-*, RateLimit-* and Retry-After headers and checks them against what happened. For example, it flags a Retry-After that is too optimistic, an advertised limit that doesn't match, or a 429 that still claims requests are remaining."),
            new HelpSteps(
            [
                "Open a request (ideally a cheap, read-only GET) and choose Tools → Probe rate limit… next to the Send button.",
                "Set the request cap, time limit and concurrency, then click Probe.",
                "Read the summary (e.g. \"100 requests per window of about 60 s\"), the insights and the timeline, and export an HTML / JSON report if needed."
            ]),
            new HelpExample("Fail CI when an endpoint has no rate limit", "dispatch ratelimit \"My Collection\" --request Login --max-requests 300 --expect-limit"),
            new HelpTip("This deliberately sends many requests to one endpoint. Probe only APIs you own or are authorised to test, and preferably not production.")
        ],
        ["rate limit", "ratelimit", "throttle", "429", "retry-after", "quota", "token bucket", "sliding window", "fixed window", "burst"],
        null, [LoadTest, Cli]),

        new(Capture, "Tools", "Capture proxy", "Record traffic from a browser or app.",
        [
            new HelpParagraph("Capture starts a local proxy. Point a browser, a mobile app or HTTP(S)_PROXY at it and every exchange is recorded. Save the captures to a collection or export a HAR file."),
            new HelpSteps(
            [
                "Open Capture in the toolbar, review the settings on the left (port, other devices, host filter, HTTPS) and click Start.",
                "For HTTPS, export the CA certificate and trust it (only on machines you control).",
                "Set the proxy in your browser or app to the address in the header, e.g. 127.0.0.1:8899.",
                "Filter the traffic, then save the shown requests to a collection or export HAR.",
                "The Report tab summarises the session: errors, hosts, endpoints, status codes, content types and the slowest calls."
            ]),
            new HelpExample("Capture traffic from a terminal command", "HTTPS_PROXY=http://127.0.0.1:8899 curl https://example.com")
        ],
        ["capture", "proxy", "record", "har", "traffic", "intercept", "sniff", "browser", "report"],
        null, [ImportExport]),

        new(ImportExport, "Basics", "Import & export", "Bring in Postman, OpenAPI, Insomnia, Thunder Client, Hoppscotch, Bruno, HAR, WSDL, .proto, .http or cURL.",
        [
            new HelpParagraph("Click Import (Ctrl+O) and choose a file, a URL or paste text. Dispatch detects the format and creates a collection (and environments, for Postman)."),
            new HelpExample("Paste a cURL command into Import", "curl -X POST https://httpbin.org/post \\\n  -H \"Content-Type: application/json\" \\\n  -d '{\"hello\":\"world\"}'"),
            new HelpParagraph("Export a collection from its menu as a Dispatch file, a git-friendly folder (one file per request), a Postman v2.1 collection or a .http file.")
        ],
        ["import", "export", "postman", "openapi", "swagger", "insomnia", "har", "wsdl", "proto", "http file", "curl", "git"],
        null, [Collections, Docs]),

        new(Docs, "Tools", "API documentation", "Generate a reference page from a collection.",
        [
            new HelpParagraph("Describe each request in its Docs tab using Markdown. Then collection menu → Generate API docs creates a self-contained HTML page (with sidebar, search, examples and code samples; secrets redacted) or a Markdown file."),
            new HelpExample("A request description", "## Get a user\n\nReturns one user by id.\n\n| Field | Type |\n|---|---|\n| id | number |\n| name | string |")
        ],
        ["docs", "documentation", "markdown", "reference", "html", "describe", "description"],
        null, [Collections]),

        new(Protocols, "Basics", "Other protocols", "GraphQL, gRPC, SOAP, WebSocket, SSE, Socket.IO, MQTT, Kafka, AMQP, TCP/UDP.",
        [
            new HelpParagraph("Use the arrow next to New to pick a protocol. Each one has its own tab next to Params and Headers."),
            new HelpSteps(
            [
                "GraphQL: write the query and variables; Fetch schema introspects the server and lists its fields.",
                "gRPC: enter host:port, load the schema via server reflection or .proto files, pick a method; a message template is generated.",
                "SOAP: load the WSDL, choose an operation and fill in the generated envelope.",
                "WebSocket, SSE, Socket.IO: Connect opens a live session; messages appear in a timestamped log.",
                "MQTT, Kafka, AMQP: publish/produce and subscribe/consume against a broker address.",
                "TCP / UDP: send text, hex or base64 payloads to tcp://host:port or udp://host:port."
            ]),
            new HelpExample("GraphQL query (try it on https://countries.trevorblades.com/)", "query {\n  country(code: \"AM\") {\n    name\n    capital\n    emoji\n  }\n}"),
            new HelpExample("WebSocket echo server", "wss://echo.websocket.org")
        ],
        ["graphql", "grpc", "soap", "wsdl", "websocket", "ws", "sse", "socket.io", "mqtt", "kafka", "amqp", "rabbitmq", "tcp", "udp", "stream"],
        "GraphQL query", [Requests]),

        new(Cli, "Tools", "Command line (CI)", "Run collections, flows, scans and mocks headlessly.",
        [
            new HelpParagraph("The dispatch command uses the same engine as the app. Point it at a file, a URL, a Dispatch folder or the name of a collection saved in the app. Exit code 0 means everything passed, 1 means failures, 2 a usage error."),
            new HelpExample("Common commands", "dispatch run \"My Collection\" -e Staging -r cli,junit,html -o reports\ndispatch run api.dispatch.json --data users.csv --bail\ndispatch flow \"My Collection\" --name \"Login smoke\"\ndispatch mock petstore.yaml --port 4010 --latency 200\ndispatch docs \"My Collection\" --format html -o api.html\ndispatch minimize \"My Collection\" --request Search\ndispatch ratelimit \"My Collection\" --request Login --expect-limit\ndispatch impact \"My Collection\" --request \"Get user\"\ndispatch laws \"My Collection\" --runs 5\ndispatch help"),
            new HelpTip("Export the collection as a git-friendly folder and run it from your repository in CI.")
        ],
        ["cli", "command line", "terminal", "ci", "pipeline", "headless", "github actions", "jenkins", "exit code"],
        null, [Runner, ImportExport]),

        new(Shortcuts, "Basics", "Keyboard shortcuts", "Work faster without the mouse.",
        [
            new HelpExample("Shortcuts", "F1                      Help\nCtrl+Enter              Send / connect\nCtrl+K or Ctrl+Shift+P  Command palette\nCtrl+T                  New HTTP request\nCtrl+S                  Save\nCtrl+W                  Close tab\nCtrl+O                  Import"),
            new HelpTip("Double-click a tab title to rename it.")
        ],
        ["keyboard", "shortcut", "hotkey", "keys", "palette"],
        null, [GettingStarted])
    ];
}
