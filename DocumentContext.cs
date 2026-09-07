using System.Globalization;
using System.Text.Json.Nodes;

namespace SwaggerRender;

internal sealed class DocumentContext(JsonObject root)
{
    private readonly HashSet<string> warnings = new(StringComparer.Ordinal);
    public JsonObject Root { get; } = root;
    public IReadOnlyCollection<string> Warnings => warnings;
    public void Warn(string message) => warnings.Add(message);

    public JsonNode? Resolve(JsonNode? node) => Resolve(node, new HashSet<string>(StringComparer.Ordinal));

    private JsonNode? Resolve(JsonNode? node, HashSet<string> chain)
    {
        if (node is not JsonObject obj || obj["$ref"] is null) return node;
        var reference = obj["$ref"].Text();
        if (!reference.StartsWith('#')) return Unresolved($"External reference not loaded: {reference}");
        if (!chain.Add(reference)) return Unresolved($"Recursive reference omitted: {reference}");
        if (chain.Count > 64) return Unresolved($"Reference chain limit reached: {reference}");
        JsonNode? target = Root;
        try
        {
            var pointer = Uri.UnescapeDataString(reference[1..]);
            if (pointer.Length > 0 && !pointer.StartsWith('/')) return Unresolved($"Unsupported reference anchor: {reference}");
            if (pointer.Length > 0)
            {
                foreach (var segment in pointer[1..].Split('/'))
                {
                    var key = segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                    target = target switch
                    {
                        JsonObject value => value[key],
                        JsonArray array when int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                            && index >= 0 && index < array.Count => array[index],
                        _ => null
                    };
                    if (target is null) return Unresolved($"Unresolved reference: {reference}");
                }
            }
        }
        catch (UriFormatException) { return Unresolved($"Invalid reference: {reference}"); }

        var resolved = Resolve(target, chain);
        if (resolved is not JsonObject resolvedObject) return resolved;
        // OpenAPI 3.1 permits schema siblings; description/summary overrides are useful for all inputs.
        if (obj.Count == 1) return resolved;
        var merged = (JsonObject)resolvedObject.DeepClone();
        foreach (var pair in obj.Where(pair => pair.Key != "$ref")) merged[pair.Key] = pair.Value?.DeepClone();
        return merged;
    }

    private JsonObject Unresolved(string message)
    {
        Warn(message);
        return new JsonObject { ["x-render-note"] = message, ["description"] = message };
    }
}
