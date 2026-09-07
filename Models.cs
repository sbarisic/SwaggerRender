using System.Text.Json.Nodes;

namespace SwaggerRender;

internal sealed record Field(string Name, string Type, string Required, string Description, string Location = "");
internal sealed record BodyExample(string Name, JsonNode? Value);
internal sealed record MediaBody(string MediaType, JsonNode? Schema, List<BodyExample> Examples);
internal sealed record RequestBody(string Description, bool Required, List<MediaBody> Content);
internal sealed record ApiResponse(string Code, string Description, List<Field> Headers, List<MediaBody> Content);
internal sealed record Endpoint(string Method, string Path, string Summary, string Description,
    List<Field> Parameters, RequestBody? Body, List<ApiResponse> Responses);

internal static class Json
{
    public static JsonNode? Get(this JsonNode? node, string key) => node is JsonObject obj ? obj[key] : null;
    public static string Text(this JsonNode? node, string fallback = "") =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : node?.ToJsonString() ?? fallback;
    public static bool Flag(this JsonNode? node, string key) =>
        node.Get(key) is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
    public static IEnumerable<KeyValuePair<string, JsonNode?>> Members(this JsonNode? node) =>
        node is JsonObject obj ? obj : Enumerable.Empty<KeyValuePair<string, JsonNode?>>();
    public static IEnumerable<JsonNode?> Items(this JsonNode? node) =>
        node is JsonArray array ? array : Enumerable.Empty<JsonNode?>();
}
