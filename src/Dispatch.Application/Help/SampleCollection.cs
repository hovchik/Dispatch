using Dispatch.Application.Requests;
using Dispatch.Domain;

namespace Dispatch.Application.Help;

/// <summary>
/// A ready-made collection for new users: each request shows one feature (tests, variables, chaining, scripts, auth,
/// other protocols) against free public echo services, with a Markdown description of what to look at.
/// </summary>
public static class SampleCollection
{
    public const string Name = "Dispatch examples";

    public static RequestCollection Create()
    {
        var collection = new RequestCollection
        {
            Name = Name,
            Description = "Example requests that show what Dispatch can do. Open one, press Send, then read its Docs tab. Press F1 for the full guide.",
            Variables =
            [
                new KeyValueItem("api", "https://jsonplaceholder.typicode.com"),
                new KeyValueItem("echo", "https://httpbin.org")
            ]
        };

        var requests = new List<ApiRequest>
        {
            Http("Simple GET", "1 Basics", HttpVerb.Get, "{{api}}/posts/1",
                """
                Your first request. `{{api}}` is a **collection variable** (collection menu → Settings & variables).

                Press **Send** (Ctrl+Enter) and look at:
                * the status, time and size above the response,
                * the **Tests** tab of the response: the three assertions defined in this request's Tests tab.
                """,
                assertions:
                [
                    new Assertion { Source = ValueSource.Status, Operator = AssertionOperator.Equals, Expected = "200" },
                    new Assertion { Source = ValueSource.JsonPath, Path = "$.id", Operator = AssertionOperator.Equals, Expected = "1" },
                    new Assertion { Source = ValueSource.ResponseTime, Operator = AssertionOperator.LessThan, Expected = "5000" }
                ],
                configure: r => r.Examples.Add(new ResponseExample
                {
                    Name = "A post",
                    Body = "{\n  \"userId\": 1,\n  \"id\": 1,\n  \"title\": \"Hello from the mock server\",\n  \"body\": \"Start the mock server (toolbar → Mock) to serve this example.\"\n}"
                })),

            Http("Query parameters", "1 Basics", HttpVerb.Get, "{{api}}/comments?postId=1",
                """
                Query parameters typed in the URL show up in the **Params** tab and vice versa. Untick a row to leave it out.

                Try changing `postId` to 2 in the Params tab and send again.
                """,
                assertions:
                [
                    new Assertion { Source = ValueSource.Status, Operator = AssertionOperator.Equals, Expected = "200" },
                    new Assertion { Source = ValueSource.JsonPath, Path = "$[0].postId", Operator = AssertionOperator.Equals, Expected = "1" }
                ],
                configure: r => r.QueryParams.Add(new KeyValueItem("postId", "1"))),

            Http("POST JSON with fake data", "1 Basics", HttpVerb.Post, "{{echo}}/post",
                """
                Sends a JSON body. `{{$randomFullName}}` and `{{$randomEmail}}` are **dynamic values**: new fake data on
                every send. Type `{{$` in the body to see all of them.

                httpbin echoes the body back under `json`, so the assertion checks what was sent.
                """,
                assertions:
                [
                    new Assertion { Source = ValueSource.Status, Operator = AssertionOperator.Equals, Expected = "200" },
                    new Assertion { Source = ValueSource.JsonPath, Path = "$.json.email", Operator = AssertionOperator.Contains, Expected = "@" }
                ],
                configure: r => r.Body = new RequestBody
                {
                    Mode = BodyMode.Json,
                    Content = "{\n  \"name\": \"{{$randomFullName}}\",\n  \"email\": \"{{$randomEmail}}\",\n  \"age\": {{$randomInt(18,90)}}\n}"
                }),

            Http("Bearer token", "1 Basics", HttpVerb.Get, "{{echo}}/bearer",
                """
                The **Auth** tab adds an `Authorization: Bearer …` header for you. In real projects put the token in an
                environment variable marked **Secret** and write `{{token}}` here.
                """,
                assertions:
                [
                    new Assertion { Source = ValueSource.Status, Operator = AssertionOperator.Equals, Expected = "200" },
                    new Assertion { Source = ValueSource.JsonPath, Path = "$.authenticated", Operator = AssertionOperator.Equals, Expected = "true" }
                ],
                configure: r => r.Auth = new AuthSettings { Mode = AuthMode.Bearer, Token = "example-token" }),

            Http("Test script", "2 Testing", HttpVerb.Get, "{{api}}/todos/1",
                """
                Besides no-code assertions, the **Tests** tab takes JavaScript with Postman's `pm` API.
                Send it and open the response's **Tests** and **Console** tabs.
                """,
                configure: r => r.TestScript =
                    """
                    pm.test("Status is 200", () => pm.response.to.have.status(200));

                    pm.test("Todo has a title", () => {
                      const todo = pm.response.json();
                      pm.expect(todo).to.have.property("title");
                    });

                    console.log("Completed:", pm.response.json().completed);
                    """),

            Http("Pre-request script", "2 Testing", HttpVerb.Get, "{{echo}}/headers",
                """
                The **Pre-request** script runs before sending. Here it stores a value in `{{traceId}}`, which the
                `X-Trace-Id` header then uses. httpbin echoes the headers back so you can see it arrived.
                """,
                assertions:
                [
                    new Assertion { Source = ValueSource.JsonPath, Path = "$.headers['X-Trace-Id']", Operator = AssertionOperator.Exists }
                ],
                configure: r =>
                {
                    r.PreRequestScript = "pm.variables.set(\"traceId\", \"trace-\" + Date.now());";
                    r.Headers.Add(new KeyValueItem("X-Trace-Id", "{{traceId}}"));
                }),

            Http("Chaining 1: extract a user id", "3 Chaining", HttpVerb.Get, "{{api}}/users/1",
                """
                The **Extract** tab copies `$.id` from the response into the runtime variable `{{userId}}`.
                Send this request first, then open **Chaining 2**, which uses that value.
                """,
                assertions: [new Assertion { Source = ValueSource.Status, Operator = AssertionOperator.Equals, Expected = "200" }],
                configure: r => r.Extractions.Add(new ExtractionRule
                {
                    Variable = "userId", Source = ValueSource.JsonPath, Path = "$.id", Scope = VariableScope.Runtime
                })),

            Http("Chaining 2: use the extracted id", "3 Chaining", HttpVerb.Get, "{{api}}/posts?userId={{userId}}",
                """
                Uses `{{userId}}` extracted by **Chaining 1**. Run the whole folder with the collection runner
                (toolbar → Runner) to see the two steps pass in order.
                """,
                assertions:
                [
                    new Assertion { Source = ValueSource.Status, Operator = AssertionOperator.Equals, Expected = "200" },
                    new Assertion { Source = ValueSource.JsonPath, Path = "$[0].userId", Operator = AssertionOperator.Equals, Expected = "{{userId}}" }
                ],
                configure: r => r.QueryParams.Add(new KeyValueItem("userId", "{{userId}}"))),

            new()
            {
                Name = "GraphQL query",
                Folder = "4 Protocols",
                Kind = RequestKind.GraphQl,
                Method = HttpVerb.Post,
                Url = "https://countries.trevorblades.com/",
                Headers = DefaultHeaders.Create(),
                Protocol = new ProtocolSettings
                {
                    GraphQl = new GraphQlSettings { Query = "query {\n  country(code: \"AM\") {\n    name\n    capital\n    emoji\n    currency\n  }\n}" }
                },
                Assertions = [new Assertion { Source = ValueSource.JsonPath, Path = "$.data.country.name", Operator = AssertionOperator.Exists }],
                Description = "A GraphQL request. Click **Fetch schema** in the Query tab to browse the fields this server offers."
            },

            new()
            {
                Name = "WebSocket echo",
                Folder = "4 Protocols",
                Kind = RequestKind.WebSocket,
                Url = "wss://echo.websocket.org",
                Protocol = new ProtocolSettings
                {
                    Stream = new StreamSettings { SavedMessages = [new KeyValueItem("hello", "{\"hello\":\"Dispatch\"}")] }
                },
                Description = "A live WebSocket session. Click **Connect**, then send a message: the server echoes it back into the log."
            }
        };

        for (var i = 0; i < requests.Count; i++)
        {
            requests[i].CollectionId = collection.Id;
            requests[i].SortOrder = i;
        }
        collection.Requests = requests;
        return collection;
    }

    private static ApiRequest Http(string name, string folder, HttpVerb method, string url, string description,
        List<Assertion>? assertions = null, Action<ApiRequest>? configure = null)
    {
        var request = new ApiRequest
        {
            Name = name,
            Folder = folder,
            Kind = RequestKind.Http,
            Method = method,
            Url = url,
            Headers = DefaultHeaders.Create(),
            Assertions = assertions ?? [],
            Description = description.Trim()
        };
        configure?.Invoke(request);
        return request;
    }
}
