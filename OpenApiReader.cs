using System.Text.Json.Nodes;

namespace SwaggerRender;

internal sealed class OpenApiReader(DocumentContext context)
{
    private static readonly HashSet<string> Methods = new(StringComparer.Ordinal)
        { "get", "put", "post", "delete", "options", "head", "patch", "trace" };
    private readonly SchemaDocumentation schemas = new(context);
    private bool swagger2;

    public List<Endpoint> Read()
    {
        ValidateVersion();
        if (context.Root["paths"] is not JsonObject paths)
        {
            if (!context.Root.ContainsKey("paths") && ReadSchemas().Count > 0) return [];
            throw new ArgumentException("The document must contain a paths object or named schemas.");
        }
        var endpoints = new List<Endpoint>();
        foreach (var path in paths)
        {
            if (!path.Key.StartsWith('/')) continue;
            var item = context.Resolve(path.Value);
            foreach (var operation in item.Members().Where(pair => Methods.Contains(pair.Key)))
            {
                if (operation.Value is not JsonObject op) throw new ArgumentException($"{operation.Key.ToUpperInvariant()} {path.Key} must be an object.");
                var parameters = MergeParameters(item.Get("parameters"), op["parameters"]);
                var fields = parameters.Where(p => p.Get("in").Text() != "body").Select(p => Parameter(p, swagger2)).ToList();
                var body = swagger2 ? SwaggerBody(parameters, op) : ReadBody(op["requestBody"]);
                var responses = new List<ApiResponse>();
                foreach (var response in op["responses"].Members().Where(pair => !pair.Key.StartsWith("x-", StringComparison.Ordinal)))
                {
                    var value = context.Resolve(response.Value);
                    var headers = value.Get("headers").Members().Select(h => Header(h.Key, context.Resolve(h.Value))).ToList();
                    var content = swagger2 ? SwaggerResponse(value, op) : Content(value.Get("content"));
                    responses.Add(new ApiResponse(response.Key, value.Get("description").Text(), headers, content));
                }
                endpoints.Add(new Endpoint(operation.Key.ToUpperInvariant(), path.Key, op["summary"].Text(), op["description"].Text(), fields, body, responses));
            }
            if (item.Get("x-render-note") is not null) context.Warn($"Path {path.Key}: {item.Get("x-render-note").Text()}");
        }
        return endpoints;
    }

    public List<NamedSchema> ReadSchemas()
    {
        ValidateVersion();
        var container = swagger2 ? context.Root["definitions"] : context.Root.Get("components").Get("schemas");
        if (container is null) return [];
        if (container is not JsonObject) throw new ArgumentException("The named schemas collection must be an object.");
        return container.Members().Select(pair => new NamedSchema(pair.Key, pair.Value)).ToList();
    }

    private void ValidateVersion()
    {
        var version = context.Root.Get("openapi").Text();
        swagger2 = context.Root.Get("swagger").Text() == "2.0";
        if (!swagger2 && !version.StartsWith("3.0.", StringComparison.Ordinal) && !version.StartsWith("3.1.", StringComparison.Ordinal))
            throw new ArgumentException("Unsupported document version. Expected swagger: 2.0 or openapi: 3.0.x / 3.1.x.");
    }

    private List<JsonNode?> MergeParameters(JsonNode? inherited, JsonNode? own)
    {
        var result = new List<JsonNode?>();
        foreach (var raw in inherited.Items().Concat(own.Items()))
        {
            var parameter = context.Resolve(raw);
            var index = result.FindIndex(p => p.Get("name").Text() == parameter.Get("name").Text()
                && p.Get("in").Text() == parameter.Get("in").Text());
            if (index >= 0) result[index] = parameter;
            else result.Add(parameter);
        }
        return result;
    }

    private Field Parameter(JsonNode? value, bool isSwagger)
    {
        var schema = isSwagger && value.Get("in").Text() != "body" ? value : value.Get("schema");
        schema ??= value.Get("content").Members().FirstOrDefault().Value.Get("schema");
        var description = schemas.Description(schema, value.Get("description").Text());
        if (value.Get("example") is JsonNode example) description += $" Example: {example.ToJsonString()}";
        return new Field(value.Get("name").Text("Unresolved parameter"), schemas.Type(schema),
            value.Flag("required") || value.Get("in").Text() == "path" ? "yes" : "no", description, value.Get("in").Text());
    }

    private Field Header(string name, JsonNode? header) => new(name, schemas.Type(swagger2 ? header : header.Get("schema")),
        header.Flag("required") ? "yes" : "no", schemas.Description(swagger2 ? header : header.Get("schema"), header.Get("description").Text()), "header");

    private RequestBody? ReadBody(JsonNode? raw)
    {
        if (raw is null) return null;
        var body = context.Resolve(raw);
        return new RequestBody(body.Get("description").Text(), body.Flag("required"), Content(body.Get("content")));
    }

    private List<MediaBody> Content(JsonNode? content)
    {
        var result = new List<MediaBody>();
        foreach (var pair in content.Members())
        {
            var examples = new List<BodyExample>();
            if (pair.Value is JsonObject media && media.ContainsKey("example"))
                examples.Add(new BodyExample("Example", media["example"]));
            foreach (var named in pair.Value.Get("examples").Members())
            {
                var value = context.Resolve(named.Value);
                var title = named.Key;
                if (value.Get("summary") is not null) title += " — " + value.Get("summary").Text();
                if (value is JsonObject example && example.ContainsKey("value")) examples.Add(new BodyExample(title, example["value"]));
                else
                {
                    var note = value.Get("x-render-note").Text();
                    if (note.Length == 0) note = value.Get("externalValue") is not null
                        ? $"External example not loaded: {value.Get("externalValue").Text()}" : "Example has no inline value.";
                    context.Warn(note);
                    examples.Add(new BodyExample(title, JsonValue.Create(note)));
                }
            }
            result.Add(new MediaBody(pair.Key, pair.Value.Get("schema"), examples));
        }
        return result;
    }

    private IEnumerable<string> MediaTypes(JsonNode? operation, string key) =>
        (operation.Get(key) ?? context.Root[key]).Items().Select(node => node.Text()).Where(s => s.Length > 0);

    private RequestBody? SwaggerBody(List<JsonNode?> parameters, JsonNode? operation)
    {
        var body = parameters.FirstOrDefault(p => p.Get("in").Text() == "body");
        if (body is not null)
        {
            var types = MediaTypes(operation, "consumes").DefaultIfEmpty("application/json");
            return new RequestBody(body.Get("description").Text(), body.Flag("required"),
                types.Select(type => new MediaBody(type, body.Get("schema"), [])).ToList());
        }
        var forms = parameters.Where(p => p.Get("in").Text() == "formData").ToList();
        if (forms.Count == 0) return null;
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var parameter in forms)
        {
            var name = parameter.Get("name").Text();
            properties[name] = parameter?.DeepClone();
            if (parameter.Flag("required")) required.Add(name);
        }
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required };
        return new RequestBody("Form fields", required.Count > 0, MediaTypes(operation, "consumes")
            .DefaultIfEmpty("application/x-www-form-urlencoded").Select(type => new MediaBody(type, schema, [])).ToList());
    }

    private List<MediaBody> SwaggerResponse(JsonNode? response, JsonNode? operation)
    {
        var examples = response.Get("examples").Members().ToList();
        if (response.Get("schema") is null && examples.Count == 0) return [];
        var types = MediaTypes(operation, "produces").Concat(examples.Select(pair => pair.Key))
            .Distinct(StringComparer.Ordinal).DefaultIfEmpty("application/json");
        return types.Select(type => new MediaBody(type, response.Get("schema"), examples
            .Where(pair => pair.Key == type).Select(pair => new BodyExample("Example", pair.Value)).ToList())).ToList();
    }
}
