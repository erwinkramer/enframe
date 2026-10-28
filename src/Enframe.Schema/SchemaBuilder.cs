using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.OpenApi;

namespace Enframe.Schema;

/// <summary>
/// Builds JSON Schema trees (<see cref="JsonNode"/>) from OpenAPI model objects, using
/// the Microsoft.OpenApi 3.2 serializer so every schema keyword survives (format, pattern,
/// min/maximum, enums, defaults, examples, …). Local <c>$ref</c>s (e.g.
/// <c>#/components/schemas/X</c>) are inlined, because
/// MCP clients cannot resolve component references inside tool input/output schemas.
/// </summary>
public static class SchemaBuilder
{
    /// <summary>
    /// Resolves the 2xx response of an operation — the first one with a JSON content
    /// schema, else the first one that declares response headers (e.g. a 204 No
    /// Content) — used to declare the tool's outputSchema and to know which response
    /// headers the API declares for it.
    /// </summary>
    public static IOpenApiResponse? ResolveSuccessResponse(OpenApiOperation operation)
    {
        if (operation.Responses is null)
            return null;

        IOpenApiResponse? headerOnly = null;
        foreach (var (statusCode, response) in operation.Responses)
        {
            if (!statusCode.StartsWith("2", StringComparison.Ordinal) || response is null)
                continue;
            if (response.Content is not null)
            {
                foreach (var media in response.Content.Values)
                {
                    if (media is OpenApiMediaType { Schema: not null })
                        return response;
                }
            }
            headerOnly ??= response.Headers is { Count: > 0 } ? response : null;
        }
        return headerOnly;
    }

    /// <summary>
    /// Resolves the 2xx response schema (the first JSON content, if any) of an
    /// operation, used to declare the tool's outputSchema.
    /// </summary>
    public static IOpenApiSchema? ResolveSuccessResponseSchema(OpenApiOperation operation)
    {
        var response = ResolveSuccessResponse(operation);
        if (response?.Content is null)
            return null;
        foreach (var media in response.Content.Values)
        {
            if (media is OpenApiMediaType { Schema: not null })
                return media.Schema;
        }
        return null;
    }

    /// <summary>
    /// Builds the MCP tool output schema, mirroring the input schema: the HTTP status
    /// code, the response headers the spec declares, and the response body (the first
    /// JSON content, if any) become top-level properties. Headers are declared as
    /// strings, since HTTP header values are strings even when the spec types them
    /// (e.g. int32 rate-limit counters). Declared headers may go missing at runtime,
    /// so they are not required.
    /// </summary>
    public static JsonNode BuildOutputSchema(OpenApiDocument document, IOpenApiResponse response, Dictionary<string, JsonNode> inlined)
    {
        var properties = new JsonObject
        {
            ["status"] = new JsonObject
            {
                ["type"] = "integer",
                ["description"] = "The HTTP status code of the response.",
            },
        };
        // The (JsonNode) cast selects the non-generic Add overload; the generic Add<T> requires
        // dynamic code and breaks trimmed/AOT builds of the sidecar.
        var required = new JsonArray { (JsonNode)"status" };

        if (response.Headers is { Count: > 0 })
        {
            var headerProperties = new JsonObject();
            foreach (var (name, header) in response.Headers)
            {
                var schema = new JsonObject { ["type"] = "string" };
                if (!string.IsNullOrEmpty(header?.Description))
                    schema["description"] = header.Description;
                headerProperties[name] = schema;
            }
            properties["headers"] = new JsonObject
            {
                ["type"] = "object",
                ["description"] = "The response headers the API declares for this operation.",
                ["properties"] = headerProperties,
            };
        }

        if (response.Content is not null)
        {
            foreach (var media in response.Content.Values)
            {
                if (media is OpenApiMediaType { Schema: not null })
                {
                    properties["body"] = SerializeAsSchema(document, media.Schema, inlined);
                    required.Add((JsonNode)"body");
                    break;
                }
            }
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
        };
    }

    /// <summary>
    /// Builds the MCP tool input schema: path, query, and header parameters become
    /// top-level properties, and the request body (the first JSON content, if any)
    /// becomes the required "body" property.
    /// </summary>
    public static JsonNode BuildInputSchema(
        OpenApiDocument document,
        Dictionary<string, ToolParameter> parameters,
        IOpenApiRequestBody? requestBody,
        Dictionary<string, JsonNode> inlined)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, parameter) in parameters)
        {
            var property = parameter.Schema is null
                ? new JsonObject()
                : SerializeAsSchema(document, parameter.Schema, inlined);
            // The parameter's own description is not part of its schema, so attach it
            // here; it helps MCP clients fill the argument (JSON Schema allows
            // `description` on any schema node).
            if (!string.IsNullOrWhiteSpace(parameter.Description))
                property["description"] = parameter.Description;
            properties[name] = property;
            if (parameter.Required)
                required.Add((JsonNode)name);
        }

        IOpenApiSchema? bodySchema = null;
        if (requestBody is { Content: not null })
            foreach (var media in requestBody.Content.Values)
            {
                if (media?.Schema is not null)
                {
                    bodySchema = media.Schema;
                    break;
                }
            }
        if (bodySchema is not null)
        {
            properties["body"] = SerializeAsSchema(document, bodySchema, inlined);
            required.Add((JsonNode)"body");
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
        };
    }

    /// <summary>
    /// Serializes a schema to JSON Schema with the official OpenAPI 3.1 writer,
    /// inlining local <c>$ref</c>s. An empty object is returned for a null schema.
    /// The <paramref name="inlined"/> cache is shared across the tools of one
    /// document: a component schema is serialized and inlined once, then
    /// deep-cloned into every other tool that references it.
    /// </summary>
    public static JsonNode SerializeAsSchema(
        OpenApiDocument document,
        IOpenApiSchema? schema,
        Dictionary<string, JsonNode> inlined)
    {
        var target = Dereference(schema);
        if (target is null)
            return new JsonObject();
        var writer = new JsonNodeWriter();
        target.SerializeAsV32(writer);
        var root = writer.Root ?? throw new InvalidOperationException("The schema serializer produced no output.");
        var inProgress = new HashSet<string>();
        var hitCycle = false;
        return InlineReferences(document, root, inProgress, inlined, ref hitCycle)!;
    }

    /// <summary>
    /// Converts a serialized schema tree into an owned <see cref="JsonElement"/>
    /// (MCP tool schemas take immutable <see cref="JsonElement"/> values). The tree
    /// is written straight into a UTF-8 buffer and parsed once, avoiding the
    /// intermediate managed string that <see cref="JsonNode.ToJsonString"/> would
    /// produce.
    /// </summary>
    public static JsonElement ToJsonElement(this JsonNode node)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            node.WriteTo(writer);
        return JsonDocument.Parse(buffer.WrittenMemory).RootElement.Clone();
    }

    /// <summary>
    /// Follows a chain of reference wrappers to the concrete schema they point to,
    /// stopping when a reference cannot be resolved (it then serializes as a <c>$ref</c>).
    /// </summary>
    private static IOpenApiSchema? Dereference(IOpenApiSchema? schema)
    {
        var current = schema;
        for (var depth = 0; current is OpenApiSchemaReference { Target: not null } reference; depth++)
        {
            if (depth >= 32)
                throw new InvalidOperationException("The schema reference chain is too deep (possible cycle).");
            current = reference.Target;
        }
        return current;
    }

    /// <summary>
    /// Walks a serialized tree, replacing local <c>$ref</c> nodes into component
    /// schemas with the serialized (recursively inlined) schema of the referenced
    /// component. References that cannot be resolved locally (e.g. external
    /// files) and reference cycles are left in place. Components whose inlining
    /// hit a cycle are not cached, since their inlined form depends on the
    /// reference that entered the cycle.
    /// </summary>
    private static JsonNode? InlineReferences(
        OpenApiDocument document,
        JsonNode? node,
        HashSet<string> inProgress,
        Dictionary<string, JsonNode> inlined,
        ref bool hitCycle)
    {
        switch (node)
        {
            case JsonObject @object:
                if (@object["$ref"] is JsonValue { } refValue
                    && refValue.TryGetValue<string>(out var refId)
                    && TryResolveComponentSchema(document, refId) is { } target)
                {
                    // Cycle guard: never inline a schema that is already being inlined higher up.
                    if (inProgress.Add(refId))
                    {
                        try
                        {
                            if (!inlined.TryGetValue(refId, out var replacement))
                            {
                                var writer = new JsonNodeWriter();
                                target.SerializeAsV32(writer);
                                var nestedCycle = false;
                                replacement = InlineReferences(document, writer.Root!, inProgress, inlined, ref nestedCycle)!;
                                if (!nestedCycle)
                                    inlined[refId] = replacement;
                            }
                            return replacement.DeepClone();
                        }
                        finally
                        {
                            inProgress.Remove(refId);
                        }
                    }
                    else
                    {
                        hitCycle = true;
                    }
                }
                else
                {
                    // Replacing values in place does not invalidate the enumeration.
                    foreach (var (key, value) in @object)
                    {
                        var updated = InlineReferences(document, value, inProgress, inlined, ref hitCycle);
                        if (!ReferenceEquals(updated, value))
                            @object[key] = updated;
                    }
                }
                return @object;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    var updated = InlineReferences(document, array[i], inProgress, inlined, ref hitCycle);
                    if (!ReferenceEquals(updated, array[i]))
                        array[i] = updated;
                }
                return array;
            default:
                return node;
        }
    }

    /// <summary>
    /// Resolves a local <c>#/components/schemas/{name}</c> reference against the
    /// document's components.
    /// </summary>
    private static IOpenApiSchema? TryResolveComponentSchema(OpenApiDocument document, string refId)
    {
        const string schemasPrefix = "#/components/schemas/";
        if (refId.StartsWith(schemasPrefix, StringComparison.Ordinal) && refId.Length > schemasPrefix.Length
            && document.Components?.Schemas is { } schemas
            && schemas.TryGetValue(refId[schemasPrefix.Length..], out var schema))
        {
            return schema;
        }
        return null;
    }
}
