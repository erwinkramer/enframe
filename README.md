<div align="center">

<img src="artwork/banner.jpg" alt="Enframe Header" width="720">

# wrap any OpenAPI spec into an MCP server

**Featuring a ~43 MB image footprint, powered by [Native AOT](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot).**

[![CC BY-NC-SA 4.0][cc-by-nc-sa-shield]][cc-by-nc-sa]
![GitHub commit activity](https://img.shields.io/github/commit-activity/m/erwinkramer/enframe)

</div>

## Quick start

If you have a well-specified API with OpenAPI, **Enframe rewards you**: it takes almost no configuration to get an MCP server running, and even (mixed) authentication (API key, OAuth 2.0, OpenID Connect) is inferred.

Let's wrap [DummyJSON](https://dummyjson.com/) into an MCP server and start it:

```sh
podman run -d --name enframe -p 8080:8080 \
  -e Enframe__ApiSpecSource=https://dummyjson.com/openapi.json \
  ghcr.io/erwinkramer/enframe:latest
```

Confirm it's up and healthy by going to the health endpoint: <http://127.0.0.1:8080/mcp/healthz>.

Now integrate the live server with your favorite harness, for Pi the `mcp.json` can be as minimal as:

```json
{"mcpServers":{"dummy":{"url":"http://127.0.0.1:8080/mcp"}}}
```

Finally, think of some creative way to reach it in a session, such as...: 

> get all smartphones from dummy mcp

## Compatibility

- **OpenAPI:** accepts Swagger 2.0 and OpenAPI 3.0-[3.2](https://spec.openapis.org/oas/v3.2.0.html) specs, emits tool schemas as [JSON Schema 2020-12](https://json-schema.org/draft/2020-12); the dialect OpenAPI 3.1+ and MCP share.
- **MCP:** official [C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) 2.x — implements [spec revision `2026-07-28`](https://modelcontextprotocol.io/specification/latest), negotiates down to `2025-11-25` and earlier.
- **Clients:** any client conforming to the MCP spec, confirmed with following clients using [pre-registration authz](https://modelcontextprotocol.io/specification/2026-07-28/basic/authorization/client-registration#pre-registration) (inferred from OpenAPI spec) via [Entra ID](https://learn.microsoft.com/en-us/entra/agent-id/secure-mcp-server-with-entra-id):

  | Client | Config, notes
  | -- | --
  | [Pi](https://pi.dev) | [.pi/mcp.json](.pi/mcp.json)
  | [OpenCode v2 CLI](https://opencode.ai/v2/docs/cli) | [opencode.jsonc](opencode.jsonc)
  | [MCP Inspector](https://modelcontextprotocol.io/docs/2026-07-28/tools/inspector) | [mcp.json](mcp.json)
  | [Local mode in VS Code](https://code.visualstudio.com/docs/agents/run/agent-harnesses#_local) | [authorize VS Code in the app reg](https://learn.microsoft.com/en-us/azure/app-service/configure-authentication-mcp-server-vscode#authorize-visual-studio-code-in-the-app-registration)

## Configuration

| Environment variable | Required | Description
| -- | -- | --
| `ASPNETCORE_HTTP_PORTS` | no | HTTP port to listen on; default `8080`
| `Enframe__ApiBaseUrl` | no | Base URL of the target API that generated tools call; defaults to the first URL in the spec's `servers`, with server variables substituted by their defaults (a relative URL is resolved against the spec's location)
| `Enframe__ApiKey` | conditional | API key sent for operations the spec protects with an apiKey scheme; required if the spec declares one
| `Enframe__ApiSpecSource` | yes | OpenAPI spec to wrap: an http(s) URL (fetched at startup) or a file path relative to the executable
| `Enframe__Authority` | no | Authorization server URL; defaults to the spec's `openIdConnectUrl` (`.well-known/openid-configuration` suffix stripped). Bearer-token auth for MCP clients is enabled when the spec declares an OAuth2 scheme and the authorization server is identifiable (this variable or the spec's `openIdConnectUrl`)
| `Enframe__McpPath` | no | URL prefix the MCP endpoint is served under; default `/mcp` (leading/trailing slashes are normalized)
| `Enframe__Resource` | no | RFC 8707 resource identifier announced to MCP clients; defaults to the API [URI authority](https://www.rfc-editor.org/info/rfc3986/#section-3.2) (via `Enframe__ApiBaseUrl`) + the MCP path (`Enframe__McpPath`)

## Concept mapping

The general OpenAPI mapping behavior is summarized as the MCP server [instructions](https://modelcontextprotocol.io/specification/2026-07-28/server/discover#data-types), via [ServerInstructions.md](src/Enframe.Mcp/ServerInstructions.md). Details about the mapping:

| OpenAPI concept | MCP concept
| -- | --
| Operation | One tool, named by its `operationId` (sanitized to MCP's allowed characters)
| `summary` / `description` | Tool `title` / `description`
| `parameters` (path, query, header) | `inputSchema` properties
| `requestBody` (first JSON content) | Required `inputSchema` property `body`
| 2xx response | Tool `outputSchema` (and `structuredContent` on success): wrapper object with `status`, declared response `headers`, and `body` (first JSON content)
| Non-2xx response | `isError: true` plus the raw response body as text; no structured content, since the `outputSchema` describes the success shape only
| `$ref` to `components/schemas` | Inlined into the tool's input/output schemas
| HTTP verb | Tool annotations: `GET`/`QUERY` → readOnly, `PUT`/`DELETE` → destructive, non-`POST` → idempotent
| `tags` | Tool `_meta.tags`
| `info.title` / `version` / `description` / `contact.url` | MCP server info: name, version, description, website URL
| `servers` (first URL, variables at defaults) | Base URL of the target API (default of `Enframe__ApiBaseUrl`)
| `securitySchemes` OAuth2 scopes | MCP `scopes_supported`; token audience derived from the scope's base
| `securitySchemes.openIdConnectUrl` | MCP authorization server (`.well-known/openid-configuration` suffix stripped)
| `security` per operation | Call-time auth mode: bearer token forwarded, apiKey scheme → APIM key header, or none

## Local development

1. Configure `127.0.0.1  localhost.guanchen.nl` or similar in the host file, so you'd have an easier time making your authorization server happy.
1. Build Enframe from source and wrap a spec that supports auth you have control over, for instance:

  ```sh
  podman build -t enframe .
  podman rm -f enframe
  podman run -d --name enframe -p 8080:8080 \
    -e Enframe__ApiKey="Lifetime Subscription" \
    -e Enframe__ApiSpecSource=https://ca.guanchen.nl/v1/openapi/spec.json \
    -e Enframe__Resource=http://localhost.guanchen.nl:8080/mcp \
    enframe
  ```

Now, the MCP server will be live and listening at `http://localhost.guanchen.nl:8080/mcp`.

With the [MCP Inspector](https://github.com/modelcontextprotocol/inspector) CLI we can test the behavior. The first run performs the interactive OAuth flow (browser + loopback callback); later runs reuse the cached token:

```sh
export MCP_INSPECTOR_SECRET_STORE=file
export MCP_OAUTH_CALLBACK_URL=http://localhost:6276/oauth/callback
npx @modelcontextprotocol/inspector --cli --config ./mcp.json --method initialize
npx @modelcontextprotocol/inspector --cli --config ./mcp.json --method servers/show --server enframe
npx @modelcontextprotocol/inspector --cli --config ./mcp.json --method tools/list --format json --strict
npx @modelcontextprotocol/inspector --cli --config ./mcp.json --method tools/call --tool-name GetBankTeller
```

## License

This work is licensed under a
[Creative Commons Attribution-NonCommercial-ShareAlike 4.0 International License][cc-by-nc-sa].

[![CC BY-NC-SA 4.0][cc-by-nc-sa-image]][cc-by-nc-sa]

[cc-by-nc-sa]: http://creativecommons.org/licenses/by-nc-sa/4.0/
[cc-by-nc-sa-image]: https://licensebuttons.net/l/by-nc-sa/4.0/88x31.png
[cc-by-nc-sa-shield]: https://img.shields.io/badge/License-CC%20BY--NC--SA%204.0-lightgrey.svg
