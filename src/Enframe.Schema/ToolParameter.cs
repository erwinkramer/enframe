using Microsoft.OpenApi;

namespace Enframe.Schema;

/// <summary>
/// A path, query, or header parameter of an operation. The description is the
/// parameter's own <c>description</c> field (not the one from its schema), carried
/// so it can be surfaced on the generated JSON Schema property. Style and explode
/// (nullable, as declared in the spec) are carried so query parameters can be
/// serialized per the OpenAPI serialization rules at call time.
/// </summary>
public sealed record ToolParameter(
    ParameterLocation Location,
    bool Required,
    IOpenApiSchema? Schema,
    string? Description,
    ParameterStyle? Style,
    bool? Explode);
