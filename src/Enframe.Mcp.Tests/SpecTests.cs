using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;

namespace Enframe.Mcp.Tests;

/// <summary>Every spec parses as an OpenAPI document and produces MCP tools.</summary>
public class SpecTests
{
    [Test]
    [MethodDataSource(typeof(Specs), nameof(Specs.Files))]
    public async Task Spec_parses_as_OpenApi_document(string name, string path)
    {
        var document = new OpenApiJsonReader()
            .Read(new MemoryStream(File.ReadAllBytes(path)), new Uri(path), new()).Document!;

        await Assert.That(string.IsNullOrWhiteSpace(document.Info.Title)).IsFalse();
        await Assert.That(document.Paths.Count).IsGreaterThan(0);
    }

    [Test]
    [MethodDataSource(typeof(Specs), nameof(Specs.Files))]
    public async Task Spec_produces_Mcp_tools(string name, string _)
    {
        var tools = OpenApiTools.Create("test-key", await OpenApiTools.LoadDocumentAsync(Specs.File(name)));

        await Assert.That(tools).IsNotEmpty();
    }

    [Test]
    [MethodDataSource(typeof(Specs), nameof(Specs.Files))]
    public async Task Tool_names_are_valid_and_unique(string name, string _)
    {
        var tools = OpenApiTools.Create("test-key", await OpenApiTools.LoadDocumentAsync(Specs.File(name)));
        var names = tools.Select(t => t.ProtocolTool.Name).ToList();

        // The MCP spec allows A-Z, a-z, 0-9, underscore, hyphen, and dot in tool names.
        var valid = new Regex("^[A-Za-z0-9_.-]{1,128}$");
        await Assert.That(names.All(n => valid.IsMatch(n))).IsTrue();
        await Assert.That(names.Distinct().Count()).IsEqualTo(names.Count);
    }

    [Test]
    public async Task Server_info_is_inferred_from_the_spec()
    {
        var info = OpenApiTools.ResolveServerInfo(await OpenApiTools.LoadDocumentAsync(Specs.File("openapi_bankapi_spec.json")));

        await Assert.That(info.Name).IsEqualTo("Bank API");
        await Assert.That(info.Version).IsEqualTo("1.1.0");
        await Assert.That(info.Description)
            .IsEqualTo("The Bank API is a design reference project suitable to bootstrap development for a compliant and modern API.");
        await Assert.That(info.WebsiteUrl).IsEqualTo("https://guanchen.nl");
    }

    [Test]
    public async Task Spec_is_loaded_from_a_url()
    {
        // Serve the bank spec from a local HTTP listener and load it the way the app would at startup.
        var listener = new HttpListener();
        var port = GetFreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var cts = new CancellationTokenSource();
        var server = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var context = await listener.GetContextAsync();
                    context.Response.StatusCode = 200;
                    await context.Response.OutputStream.WriteAsync(File.ReadAllBytes(Specs.File("openapi_bankapi_spec.json")));
                    context.Response.Close();
                }
            }
            catch
            {
                // Listener stopped.
            }
            finally
            {
                listener.Stop();
            }
        });

        try
        {
            var document = await OpenApiTools.LoadDocumentAsync($"http://127.0.0.1:{port}/openapi_bankapi_spec.json");
            await Assert.That(document.Info.Title).IsEqualTo("Bank API");
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
        }
    }

    private static int GetFreePort()
    {
        var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        return port;
    }

    [Test]
    public async Task Create_fails_fast_when_the_apiKey_is_missing_but_the_spec_requires_it()
    {
        // The Giphy spec protects its operations with an apiKey scheme.
        var document = await OpenApiTools.LoadDocumentAsync(Specs.File("openapi_giphy_spec.json"));

        await Assert.That(() => { OpenApiTools.Create("", document); }).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Create_does_not_require_an_apiKey_when_the_spec_does_not_use_one()
    {
        // A spec that declares no security at all needs no subscription key.
        var document = new OpenApiDocument
        {
            Info = new OpenApiInfo { Title = "Test API", Version = "1.0.0" },
            Paths = new OpenApiPaths
            {
                ["/pets"] = new OpenApiPathItem
                {
                    Operations = new Dictionary<HttpMethod, OpenApiOperation>
                    {
                        [HttpMethod.Get] = new OpenApiOperation { OperationId = "listPets" },
                    },
                },
            },
        };

        var tools = OpenApiTools.Create("", document);
        await Assert.That(tools).IsNotEmpty();
    }

    [Test]
    public async Task Base_url_is_inferred_from_the_spec()
    {
        // An absolute server URL is used as-is.
        var bank = await OpenApiTools.LoadDocumentAsync(Specs.File("openapi_bankapi_spec.json"));
        await Assert.That(OpenApiTools.ResolveBaseUrl(bank))
            .IsEqualTo(new Uri("https://ca.guanchen.nl/v1"));

        var giphy = await OpenApiTools.LoadDocumentAsync(Specs.File("openapi_giphy_spec.json"));
        await Assert.That(OpenApiTools.ResolveBaseUrl(giphy))
            .IsEqualTo(new Uri("https://api.giphy.com/v1"));

        // Swagger 2.0's host + basePath map to a single server URL.
        var swagger = await OpenApiTools.LoadDocumentAsync(Specs.File("openapi_petstore_v2_spec.json"));
        await Assert.That(OpenApiTools.ResolveBaseUrl(swagger))
            .IsEqualTo(new Uri("https://petstore.swagger.io/v2"));
    }

    [Test]
    public async Task Base_url_of_a_relative_server_resolves_against_the_spec_location()
    {
        // petstore v3 declares a relative server URL (/api/v3).
        var openApi = await OpenApiTools.LoadDocumentAsync("https://petstore3.swagger.io/api/v3/openapi.json");
        await Assert.That(OpenApiTools.ResolveBaseUrl(openApi))
            .IsEqualTo(new Uri("https://petstore3.swagger.io/api/v3"));
    }

    [Test]
    public async Task Base_url_substitutes_server_variables_with_their_defaults()
    {
        var document = new OpenApiDocument
        {
            Info = new OpenApiInfo { Title = "Test API", Version = "1.0.0" },
            Servers =
            [
                new OpenApiServer
                {
                    Url = "https://{org}.example.com/v1",
                    Variables = new Dictionary<string, OpenApiServerVariable>
                    {
                        ["org"] = new() { Default = "api" },
                    },
                },
            ],
            Paths = new OpenApiPaths(),
        };

        await Assert.That(OpenApiTools.ResolveBaseUrl(document))
            .IsEqualTo(new Uri("https://api.example.com/v1"));
    }

    [Test]
    public async Task Base_url_is_null_when_the_spec_declares_no_servers()
    {
        var document = await OpenApiTools.LoadDocumentAsync(Specs.File("openapi_petstore_v3_spec.json"));
        document.Servers.Clear();

        await Assert.That(OpenApiTools.ResolveBaseUrl(document)).IsNull();
    }

    [Test]
    public async Task Token_auth_is_inferred_from_the_spec()
    {
        var auth = OpenApiTools.ResolveTokenAuth(await OpenApiTools.LoadDocumentAsync(Specs.File("openapi_bankapi_spec.json")))
            ?? throw new Exception("Expected the bank API spec to declare OAuth2 security schemes.");

        await Assert.That(auth.Audience).IsEqualTo("b6997777-3799-4c55-b78a-4ce96e3d959c");
        await Assert.That(auth.Scopes).Contains("b6997777-3799-4c55-b78a-4ce96e3d959c/.default");
        await Assert.That(auth.AuthorizationServer)
            .IsEqualTo("https://login.microsoftonline.com/b81eb003-1c5c-45fd-848f-90d9d3f8d016/v2.0");
    }
}
