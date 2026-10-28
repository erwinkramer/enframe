using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.OpenApi;

namespace Enframe.Schema;

/// <summary>
/// An <see cref="IOpenApiWriter"/> that builds a <see cref="JsonNode"/> tree directly,
/// so schemas can be serialized without the buffer-then-parse round trip.
/// </summary>
internal sealed class JsonNodeWriter : IOpenApiWriter
{
    private static readonly JsonNode? NullNode = JsonNode.Parse("null");

    private readonly Stack<JsonNode> _stack = new();
    private string? _pendingKey;
    private JsonNode? _root;

    /// <summary>The root of the serialized tree; available once serialization completes.</summary>
    public JsonNode? Root => _root;

    /// <summary>The OpenAPI writers always finish balanced; there is nothing to flush.</summary>
    public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void WriteStartObject() => Push(new JsonObject());

    public void WriteEndObject() => Pop();

    public void WriteStartArray() => Push(new JsonArray());

    public void WriteEndArray() => Pop();

    public void WritePropertyName(string name) => _pendingKey = name;

    public void WriteValue(string? value) => SetCurrent(value is null ? NullNode : value);

    public void WriteValue(decimal value) => SetCurrent(value);

    public void WriteValue(int value) => SetCurrent(value);

    public void WriteValue(bool value) => SetCurrent(value);

    /// <summary>
    /// The OpenAPI writer extensions box value types (schema limits, enums, defaults,
    /// examples) and route them through this method, so every common JSON scalar type
    /// is accepted here.
    /// </summary>
    public void WriteValue(object? value)
    {
        switch (value)
        {
            case JsonNode node:
                SetCurrent(node);
                break;
            case string s:
                SetCurrent(s);
                break;
            case bool b:
                SetCurrent(b);
                break;
            case int i:
                SetCurrent(i);
                break;
            case long l:
                SetCurrent(l);
                break;
            case double d:
                SetCurrent(d);
                break;
            case decimal m:
                SetCurrent(m);
                break;
            case float f:
                SetCurrent(f);
                break;
            case byte b:
                SetCurrent((int)b);
                break;
            case sbyte b:
                SetCurrent((int)b);
                break;
            case short b:
                SetCurrent((int)b);
                break;
            case ushort b:
                SetCurrent((int)b);
                break;
            case uint u:
                SetCurrent((long)u);
                break;
            case ulong u:
                SetCurrent(u);
                break;
            case Guid g:
                SetCurrent(g.ToString());
                break;
            case DateTime dt:
                SetCurrent(dt.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture));
                break;
            case DateTimeOffset dto:
                SetCurrent(dto.ToString("o", CultureInfo.InvariantCulture));
                break;
            case null:
                throw new InvalidOperationException("Unexpected null value; use WriteNull.");
            default:
                throw new InvalidOperationException($"Unexpected value type: {value.GetType()}.");
        }
    }

    public void WriteNull() => SetCurrent(NullNode);

    /// <summary>
    /// Raw values are either JSON fragments or unquoted text (e.g. a media type);
    /// parse into the tree where possible so the result stays a valid JSON document.
    /// </summary>
    public void WriteRaw(string value)
    {
        try
        {
            SetCurrent(JsonNode.Parse(value));
        }
        catch (JsonException)
        {
            SetCurrent(value);
        }
    }

    private void Push(JsonNode node)
    {
        if (_stack.Count == 0)
        {
            _root = node;
        }
        else
        {
            SetCurrent(node);
        }
        _stack.Push(node);
    }

    private void Pop()
    {
        if (_stack.Count == 0)
            throw new InvalidOperationException("Unbalanced end marker from the OpenAPI writer.");
        _stack.Pop();
    }

    private void SetCurrent(JsonNode? node)
    {
        if (_pendingKey is { } key)
        {
            var parent = _stack.Peek() as JsonObject
                ?? throw new InvalidOperationException("Value written with a pending key outside of an object.");
            parent[key] = node;
            _pendingKey = null;
            return;
        }
        var array = _stack.Peek() as JsonArray
            ?? throw new InvalidOperationException("Value written without a pending key or an enclosing array.");
        array.Add(node);
    }
}
