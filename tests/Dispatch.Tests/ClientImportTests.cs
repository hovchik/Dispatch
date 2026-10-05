using Dispatch.Application.Interop;
using Dispatch.Domain;
using Dispatch.Infrastructure.Interop;

namespace Dispatch.Tests;

public class ClientImportTests
{
    [Fact]
    public void Thunder_client_collection_with_folders_body_and_auth()
    {
        var result = Importer.ImportText("""
            {"clientName":"Thunder Client","collectionName":"Shop","version":"1.2",
             "folders":[{"_id":"f1","name":"Users","containerId":""},{"_id":"f2","name":"Admin","containerId":"f1"}],
             "requests":[
               {"_id":"r2","name":"Second","url":"https://x/b","method":"GET","sortNum":20,"containerId":""},
               {"_id":"r1","name":"Create","url":"{{base}}/users?x=1","method":"POST","sortNum":10,"containerId":"f2",
                "headers":[{"name":"X-A","value":"1"},{"name":"X-Off","value":"2","isDisabled":true}],
                "params":[{"name":"x","value":"1"},{"name":"id","value":"5","isPath":true}],
                "body":{"type":"json","raw":"{\"a\":1}","form":[]},
                "auth":{"type":"basic","basic":{"username":"u","password":"p"}}}
             ]}
            """);

        Assert.Equal("Thunder Client", result.Format);
        var collection = result.Collections.Single();
        Assert.Equal("Shop", collection.Name);
        Assert.Equal(["Create", "Second"], collection.Requests.Select(r => r.Name));
        var create = collection.Requests[0];
        Assert.Equal("Users/Admin", create.Folder);
        Assert.Equal(HttpVerb.Post, create.Method);
        Assert.Equal(BodyMode.Json, create.Body.Mode);
        Assert.Equal(AuthMode.Basic, create.Auth.Mode);
        Assert.Equal("u", create.Auth.Username);
        Assert.False(create.Headers.Single(h => h.Key == "X-Off").Enabled);
        Assert.Equal("x", create.QueryParams.Single().Key);
    }

    [Fact]
    public void Thunder_client_environment()
    {
        var result = Importer.ImportText("""{"clientName":"Thunder Client","environmentName":"Dev","data":[{"name":"base","value":"https://x"}]}""");
        Assert.Equal("Dev", result.Environments.Single().Name);
        Assert.Equal("https://x", result.Environments[0].Variables.Single().Value);
    }

    [Fact]
    public void Hoppscotch_collection_with_nested_folders_and_old_templates()
    {
        var result = Importer.ImportText("""
            [{"v":2,"name":"Pets","folders":[{"v":2,"name":"Admin","folders":[],"requests":[
                {"v":"1","name":"Delete","method":"DELETE","endpoint":"<<base>>/pets/1","params":[],"headers":[],
                 "auth":{"authType":"bearer","authActive":true,"token":"<<token>>"},"body":{"contentType":null,"body":null}}]}],
              "requests":[{"v":"1","name":"Add","method":"POST","endpoint":"{{base}}/pets",
                 "params":[{"key":"dry","value":"1","active":true}],
                 "headers":[{"key":"X-A","value":"b","active":false}],
                 "auth":{"authType":"none","authActive":true},
                 "body":{"contentType":"application/x-www-form-urlencoded","body":"name: Rex\n#age: 3"},
                 "preRequestScript":"pw.env.set('a','1')","testScript":""}]}]
            """);

        Assert.Equal("Hoppscotch", result.Format);
        var requests = result.Collections.Single().Requests;
        var add = requests.Single(r => r.Name == "Add");
        Assert.Equal("{{base}}/pets?dry=1", add.Url);
        Assert.Equal(BodyMode.FormUrlEncoded, add.Body.Mode);
        Assert.False(add.Body.FormFields.Single(f => f.Key == "age").Enabled);
        Assert.False(add.Headers.Single().Enabled);
        var delete = requests.Single(r => r.Name == "Delete");
        Assert.Equal("Admin", delete.Folder);
        Assert.Equal("{{base}}/pets/1", delete.Url);
        Assert.Equal("{{token}}", delete.Auth.Token);
    }

    [Fact]
    public void Hoppscotch_environments()
    {
        var result = Importer.ImportText("""[{"name":"Prod","variables":[{"key":"base","value":"https://p"}]}]""");
        Assert.Equal("Prod", result.Environments.Single().Name);
    }

    [Fact]
    public void Insomnia_v5_yaml()
    {
        var result = Importer.ImportText("""
            type: collection.insomnia.rest/5.0
            name: My API
            collection:
              - name: Users
                children:
                  - url: "{{ _.base }}/users"
                    name: List users
                    method: GET
                    headers:
                      - name: Accept
                        value: application/json
                    authentication:
                      type: bearer
                      token: "{{ _.token }}"
            environments:
              name: Base
              data:
                base: https://x
              subEnvironments:
                - name: Prod
                  data:
                    base: https://prod
            """);

        Assert.Equal("Insomnia", result.Format);
        var request = result.Collections.Single().Requests.Single();
        Assert.Equal("Users", request.Folder);
        Assert.Equal("{{base}}/users", request.Url);
        Assert.Equal("{{token}}", request.Auth.Token);
        Assert.Equal(["Base", "Prod"], result.Environments.Select(e => e.Name));
    }

    private const string BruRequest = """
        meta {
          name: Create user
          type: http
          seq: 2
        }

        post {
          url: {{base}}/users
          body: json
          auth: bearer
        }

        params:query {
          dry: 1
          ~skip: 2
        }

        headers {
          X-A: b
          ~X-Off: c
        }

        auth:bearer {
          token: {{token}}
        }

        body:json {
          {
            "name": "Ann"
          }
        }

        tests {
          test("ok", function() { expect(res.status).to.equal(201); });
        }
        """;

    [Fact]
    public void Bruno_single_bru_file()
    {
        var result = Importer.ImportText(BruRequest, "create");
        Assert.Equal("Bruno", result.Format);
        var request = result.Collections.Single().Requests.Single();
        Assert.Equal("Create user", request.Name);
        Assert.Equal(HttpVerb.Post, request.Method);
        Assert.Equal("{{base}}/users?dry=1", request.Url);
        Assert.Equal(BodyMode.Json, request.Body.Mode);
        Assert.Equal("{\n  \"name\": \"Ann\"\n}", request.Body.Content);
        Assert.Equal("{{token}}", request.Auth.Token);
        Assert.False(request.Headers.Single(h => h.Key == "X-Off").Enabled);
        Assert.Contains("res.status", request.TestScript);
        Assert.Contains(Bruno.ScriptWarning, result.Warnings);
    }

    [Fact]
    public async Task Bruno_collection_folder_with_environments()
    {
        var dir = Directory.CreateTempSubdirectory("bruno").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "bruno.json"), """{"version":"1","name":"Shop API","type":"collection"}""");
            Directory.CreateDirectory(Path.Combine(dir, "users"));
            Directory.CreateDirectory(Path.Combine(dir, "environments"));
            File.WriteAllText(Path.Combine(dir, "users", "create.bru"), BruRequest);
            File.WriteAllText(Path.Combine(dir, "users", "list.bru"), "meta {\n  name: List\n  seq: 1\n}\n\nget {\n  url: {{base}}/users\n}\n");
            File.WriteAllText(Path.Combine(dir, "users", "folder.bru"), "meta {\n  name: users\n}\n");
            File.WriteAllText(Path.Combine(dir, "environments", "Local.bru"), "vars {\n  base: http://localhost\n}\nvars:secret [\n  token\n]\n");

            var importer = new Importer(null!, null!);
            var result = await importer.ImportPathAsync(dir);

            var collection = result.Collections.Single();
            Assert.Equal("Shop API", collection.Name);
            Assert.Equal(["List", "Create user"], collection.Requests.Select(r => r.Name));
            Assert.All(collection.Requests, r => Assert.Equal("users", r.Folder));
            var env = result.Environments.Single();
            Assert.Equal("Local", env.Name);
            Assert.Equal(["base", "token"], env.Variables.Select(v => v.Key));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Bruno_json_export()
    {
        var result = Importer.ImportText("""
            {"name":"Shop","version":"1","items":[
              {"type":"folder","name":"Users","items":[
                {"type":"http-request","name":"Get","seq":1,"request":{"url":"{{base}}/users/1","method":"GET",
                  "headers":[{"name":"X-A","value":"1","enabled":true}],"params":[],
                  "body":{"mode":"none"},"auth":{"mode":"basic","basic":{"username":"u","password":"p"}}}}]}],
             "environments":[{"name":"Dev","variables":[{"name":"base","value":"https://x","enabled":true}]}]}
            """);

        Assert.Equal("Bruno", result.Format);
        var request = result.Collections.Single().Requests.Single();
        Assert.Equal("Users", request.Folder);
        Assert.Equal(AuthMode.Basic, request.Auth.Mode);
        Assert.Equal("Dev", result.Environments.Single().Name);
    }
}
