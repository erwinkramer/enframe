using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Enframe.Schema;
using Microsoft.OpenApi;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Enframe.Mcp;

/// <summary>
/// An MCP tool generated from a single OpenAPI operation: it builds the HTTP request from the tool's
/// arguments, authenticates per the operation's inferred auth mode, and calls the target API.
/// </summary>
public sealed class OpenApiTool : McpServerTool
{
    /// <summary>
    /// Bodies larger than this are returned as text only: parsing and cloning them
    /// for structured content would double the memory of large payloads per request.
    /// </summary>
    private const int MaxStructuredContentLength = 256 * 1024;

    private readonly Dictionary<string, ToolParameter> _parameters;
    private readonly HttpMethod _method;
    private readonly string _route;
    private readonly IOpenApiSchema? _responseSchema;
    private readonly IReadOnlyList<string>? _responseHeaders;
    private readonly string _apiKey;
    private readonly OperationAuth _auth;

    public OpenApiTool(
        Tool tool,
        HttpMethod method,
        string route,
        Dictionary<string, ToolParameter> parameters,
        IOpenApiSchema? responseSchema,
        IReadOnlyList<string>? responseHeaders,
        string apiKey,
        OperationAuth auth)
    {
        _method = method;
        _route = route;
        _parameters = parameters;
        _responseSchema = responseSchema;
        _responseHeaders = responseHeaders;
        _apiKey = apiKey;
        _auth = auth;
        ProtocolTool = tool;
    }

    public override Tool ProtocolTool { get; }

    /// <summary>How this tool authenticates to the target API (inferred from the spec's `security`).</summary>
    public OperationAuth Auth => _auth;

    public override IReadOnlyList<object> Metadata { get; } = Array.Empty<object>();

    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        var args = request.Params.Arguments ?? new Dictionary<string, JsonElement>();

        // Get the client from the factory registered in Program.cs (AddHttpClient) so the
        // underlying socket handlers are pooled and rotated instead of one new pool per call.
        var httpClientFactory = request.Services?.GetService<IHttpClientFactory>()
            ?? throw new InvalidOperationException(
                "IHttpClientFactory is not registered; call AddHttpClient on the service collection.");
        using var httpClient = httpClientFactory.CreateClient("target-api");
        // AbsoluteUri normalizes a root base (http://host[:port]) to end with "/",
        // which would double the leading slash of the route and 404 on most servers.
        var baseUrl = httpClient.BaseAddress!.AbsoluteUri;
        if (baseUrl.Length > 0 && baseUrl[^1] == '/')
            baseUrl = baseUrl[..^1];

        using var response = await httpClient.SendAsync(
            BuildRequestMessage(baseUrl, args, request.Services), cancellationToken);
        return await BuildResultAsync(response, cancellationToken);
    }

    /// <summary>
    /// Builds the outbound request from the tool's arguments: path/query/header
    /// parameters, the JSON body when present, and the operation's auth.
    /// </summary>
    private HttpRequestMessage BuildRequestMessage(
        string baseUrl,
        IDictionary<string, JsonElement> args,
        IServiceProvider? services)
    {
        var url = _route;
        var query = new List<string>();
        var headers = new Dictionary<string, string>();
        foreach (var (name, param) in _parameters)
        {
            if (!args.TryGetValue(name, out var value))
                continue;

            switch (param.Location)
            {
                case ParameterLocation.Path:
                    url = url.Replace($"{{{name}}}", Uri.EscapeDataString(RawValue(value)));
                    break;
                case ParameterLocation.Query:
                    query.AddRange(SerializeQueryValue(name, param, value));
                    break;
                case ParameterLocation.Header:
                    headers[name] = RawValue(value);
                    break;
            }
        }
        if (query.Count > 0)
            url += "?" + string.Join("&", query);

        StringContent? content = null;
        if (args.TryGetValue("body", out var bodyValue) && bodyValue.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            content = new StringContent(bodyValue.GetRawText(), Encoding.UTF8, "application/json");

        var message = new HttpRequestMessage(_method, baseUrl + url) { Content = content };
        // Spec-declared header parameters (e.g. If-Match) go on the request.
        foreach (var (name, value) in headers)
            message.Headers.TryAddWithoutValidation(name, value);

        switch (_auth.Mode)
        {
            case AuthMode.Bearer:
                // Forward the client's bearer token, unless the caller supplied
                // their own Authorization header parameter.
                var authorization = services?.GetService<IHttpContextAccessor>()?.HttpContext
                    ?.Request.Headers.Authorization.ToString();
                if (!headers.ContainsKey("Authorization") &&
                    !string.IsNullOrEmpty(authorization) && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    message.Headers.Authorization =
                        new AuthenticationHeaderValue("Bearer", authorization["Bearer ".Length..]);
                break;
            case AuthMode.ApiKey:
                // Use the APIM subscription key, in the header named by the spec's apiKey scheme.
                message.Headers.TryAddWithoutValidation(_auth.ApiKeyHeaderName ?? "Ocp-Apim-Subscription-Key", _apiKey);
                break;
        }
        return message;
    }

    /// <summary>
    /// The raw text of an argument value: strings are unquoted, everything else
    /// (numbers, booleans, objects, arrays) goes out as raw JSON text.
    /// </summary>
    private static string RawValue(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();

    /// <summary>
    /// Serializes a query parameter value per the OpenAPI serialization rules for the
    /// parameter's style (default <c>form</c> when undeclared) and explode
    /// (defaults to <c>true</c> for <c>form</c>, <c>false</c> for other styles):
    /// exploded arrays repeat the parameter per item (<c>?tag=a&amp;tag=b</c>);
    /// non-exploded arrays join the items with the style's delimiter
    /// (<c>form</c>: <c>,</c>, <c>spaceDelimited</c>: <c> </c>, <c>pipeDelimited</c>: <c>|</c>);
    /// <c>deepObject</c> exploded arrays use bracketed keys (<c>?tag[]=a</c>).
    /// Returns "name=already-escaped-value" pairs ready to join into the query string.
    /// </summary>
    private static IEnumerable<string> SerializeQueryValue(
        string name, ToolParameter parameter, JsonElement value)
    {
        if (value.ValueKind is not JsonValueKind.Array)
            return [$"{name}={Uri.EscapeDataString(RawValue(value))}"];

        var items = value.EnumerateArray().Select(RawValue).ToArray();
        // Per the spec, explode defaults to true for form style, false for all others.
        if (parameter.Explode ?? parameter.Style is null or ParameterStyle.Form)
            return items.Select(item =>
                $"{name + (parameter.Style == ParameterStyle.DeepObject ? "[]" : "")}={Uri.EscapeDataString(item)}");

        var delimiter = parameter.Style switch
        {
            ParameterStyle.SpaceDelimited => " ",
            ParameterStyle.PipeDelimited => "|",
            _ => ",",
        };
        return [$"{name}={Uri.EscapeDataString(string.Join(delimiter, items))}"];
    }

    /// <summary>
    /// Turns the API response into the tool result: the body decoded once to text for the
    /// text block, plus structured content mirroring the declared output schema (status,
    /// declared headers, body) for successful responses only — non-2xx results rely on
    /// IsError plus the text block, since the outputSchema describes the success shape only.
    /// A 204 No Content maps to status and headers only, and an error response with an
    /// empty body reports "HTTP {status} {reason}" in the text block instead. Skipped for
    /// operations that declare no output shape at all, and for large bodies, to keep
    /// per-request memory bounded.
    /// </summary>
    private async ValueTask<CallToolResult> BuildResultAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        // Decode the body once; the original bytes are spliced into the structured
        // content below instead of re-encoding the text.
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var charset = response.Content.Headers.ContentType?.CharSet;
        var encoding = string.IsNullOrEmpty(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset);
        var text = encoding.GetString(bytes);
        var body = bytes.AsMemory();

        // Error responses with no body (e.g. 404 Not Found) would otherwise surface
        // as an empty text block with no status information; report the status.
        if (!response.IsSuccessStatusCode && string.IsNullOrWhiteSpace(text))
            text = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

        JsonElement? structuredContent = (_responseSchema is null && _responseHeaders is null)
            || !response.IsSuccessStatusCode
            || text.Length > MaxStructuredContentLength
            ? null
            : BuildStructuredContent(response, text, body);

        return new CallToolResult
        {
            IsError = !response.IsSuccessStatusCode,
            Content =
            [
                new TextContentBlock
                {
                    Text = text,
                },
            ],
            StructuredContent = structuredContent,
        };
    }

    /// <summary>
    /// Builds the structured result mirroring the declared output schema: the HTTP
    /// status code, the declared response headers the API actually sent (matched
    /// case-insensitively; omitted when none were sent), and the body when it is
    /// JSON (a 204 No Content has no body, so it maps to status and headers only).
    /// If the body is not valid JSON, the structured content falls back to status
    /// and headers only.
    /// </summary>
    private JsonElement BuildStructuredContent(HttpResponseMessage response, string text, ReadOnlyMemory<byte> body)
    {
        var status = (int)response.StatusCode;
        Dictionary<string, string>? headers = null;
        if (_responseHeaders is not null)
        {
            foreach (var name in _responseHeaders)
            {
                if (response.Headers.TryGetValues(name, out var values)
                    || response.Content?.Headers.TryGetValues(name, out values) == true)
                {
                    headers ??= new Dictionary<string, string>();
                    headers[name] = string.Join(", ", values);
                }
            }
        }

        var bodyBytes = string.IsNullOrWhiteSpace(text) ? default : body;
        return BuildStructuredElement(status, headers, bodyBytes);
    }

    private static JsonElement BuildStructuredElement(int status, Dictionary<string, string>? headers, ReadOnlyMemory<byte> body)
    {
        var bytes = BuildStructuredJsonBytes(status, headers, body);
        try
        {
            // JsonDocument.Parse is reflection-free, so this stays AOT-safe. No
            // Clone: the returned root element keeps its document alive, so the
            // parsed bytes stay valid for the caller's serialization.
            return JsonDocument.Parse(bytes).RootElement;
        }
        catch (JsonException)
        {
            // The body was not valid JSON after all: retry without it.
            return JsonDocument.Parse(BuildStructuredJsonBytes(status, headers, default)).RootElement;
        }
    }

    /// <summary>
    /// Serializes the structured-content wrapper straight to UTF-8 bytes, splicing
    /// the already-JSON response body in as a raw value so it is not parsed and
    /// re-serialized. The pre-sized stream avoids a grow/resize allocation for
    /// large bodies.
    /// </summary>
    private static byte[] BuildStructuredJsonBytes(int status, Dictionary<string, string>? headers, ReadOnlyMemory<byte> body)
    {
        using var stream = new MemoryStream(body.Length + 128);
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("status", status);
            if (headers is not null && headers.Count > 0)
            {
                writer.WritePropertyName("headers");
                writer.WriteStartObject();
                foreach (var (name, value) in headers)
                    writer.WriteString(name, value);
                writer.WriteEndObject();
            }
            if (body.Length > 0)
            {
                writer.WritePropertyName("body");
                writer.WriteRawValue(body.Span);
            }
            writer.WriteEndObject();
        }
        return stream.GetBuffer().AsSpan(0, (int)stream.Length).ToArray();
    }
}
