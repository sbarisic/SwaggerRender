using System.Text.Json.Nodes;

namespace SwaggerRender;

internal sealed class SchemaDocumentation(DocumentContext context)
{
    internal const int MaxDepth = 8;
    private static readonly string[] Unsupported = ["$dynamicRef", "$recursiveRef", "not", "if", "then", "else", "patternProperties", "dependentSchemas", "prefixItems", "unevaluatedProperties", "contains"];

    // Flatten only composition at this node; descend into properties separately with a depth bound.
    private JsonNode? Expand(JsonNode? raw, int depth = 0)
    {
        if (depth >= MaxDepth) return Note("Recursive/composition details omitted at depth limit (8).");
        var node = context.Resolve(raw);
        if (node is not JsonObject obj) return node;
        var result = (JsonObject)obj.DeepClone();
        foreach (var keyword in Unsupported.Where(obj.ContainsKey))
            AddNote(result, $"Unsupported schema keyword: {keyword}");
        var types = obj["type"] is JsonArray typeArray ? typeArray.Select(value => value.Text()) : new[] { obj["type"].Text() };
        foreach (var type in types.Where(type => type.Length > 0 && type is not ("object" or "array" or "string" or "integer" or "number" or "boolean" or "null" or "file")))
            AddNote(result, $"Unsupported schema type: {type}");
        foreach (var part in obj["allOf"].Items())
            Merge(result, Expand(part, depth + 1) as JsonObject);
        result.Remove("allOf");
        foreach (var key in new[] { "oneOf", "anyOf" })
        {
            if (obj[key] is not JsonArray { Count: > 0 } alternatives) continue;
            Merge(result, Expand(alternatives[0], depth + 1) as JsonObject);
            AddNote(result, $"{key}: first of {alternatives.Count} alternatives shown.", warn: false);
            result.Remove(key);
        }
        return result;
    }

    private static void Merge(JsonObject target, JsonObject? source)
    {
        if (source is null) return;
        foreach (var pair in source)
        {
            if (pair.Key == "properties" && pair.Value is JsonObject properties)
            {
                if (target["properties"] is not JsonObject) target["properties"] = new JsonObject();
                var dest = (JsonObject)target["properties"]!;
                foreach (var property in properties)
                {
                    if (dest[property.Key] is JsonObject existing && property.Value is JsonObject addition)
                        Merge(existing, addition);
                    else if (!dest.ContainsKey(property.Key)) dest[property.Key] = property.Value?.DeepClone();
                }
            }
            else if (pair.Key == "required" && pair.Value is JsonArray required)
            {
                var names = target["required"].Items().Concat(required).Select(n => n.Text()).Distinct(StringComparer.Ordinal);
                target["required"] = new JsonArray(names.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
            }
            else if (pair.Key == "x-render-note" && target[pair.Key] is not null)
                target[pair.Key] = target[pair.Key].Text() + " " + pair.Value.Text();
            else if (!target.ContainsKey(pair.Key)) target[pair.Key] = pair.Value?.DeepClone();
        }
    }

    private JsonObject Note(string text)
    {
        context.Warn(text);
        return new JsonObject { ["x-render-note"] = text };
    }

    private void AddNote(JsonObject obj, string text, bool warn = true)
    {
        if (warn) context.Warn(text);
        obj["x-render-note"] = (obj["x-render-note"].Text() + " " + text).Trim();
    }

    public string Type(JsonNode? raw) => TypeExpanded(Expand(raw));

    public string Description(JsonNode? raw, string description = "") => Describe(Expand(raw), description);

    private static string Describe(JsonNode? schema, string description = "")
    {
        if (description.Length == 0) description = schema.Get("description").Text();
        if (schema.Get("enum") is JsonArray enums) description += " Allowed: " + enums.ToJsonString();
        if (schema is JsonObject obj && obj.ContainsKey("default")) description += " Default: " + obj["default"].Text("null");
        if (schema.Flag("readOnly")) description += " Read only.";
        if (schema.Flag("writeOnly")) description += " Write only.";
        var note = schema.Get("x-render-note").Text();
        if (note.Length > 0 && !description.Contains(note, StringComparison.Ordinal)) description += " " + note;
        return description.Trim();
    }

    private static string TypeExpanded(JsonNode? schema)
    {
        if (schema is JsonValue boolean && boolean.TryGetValue<bool>(out var allowed)) return allowed ? "any" : "not allowed";
        var types = schema.Get("type") is JsonArray array
            ? string.Join(" | ", array.Select(v => v.Text())) : schema.Get("type").Text();
        if (types.Length == 0) types = schema.Get("properties") is not null || schema.Get("additionalProperties") is not null ? "object"
            : schema.Get("items") is not null ? "array" : "any";
        var format = schema.Get("format").Text();
        if (format.Length > 0) types += $" ({format})";
        if (schema.Flag("nullable") && !types.Contains("null", StringComparison.Ordinal)) types += " | null";
        return types;
    }

    // null selects a standalone schema, with no request/response field filtering.
    private static bool Excluded(JsonNode? schema, bool? request) => request switch
    {
        true => schema.Flag("readOnly"),
        false => schema.Flag("writeOnly"),
        null => false
    };

    public List<Field> Fields(JsonNode? schema, bool? request)
    {
        var fields = new List<Field>();
        if (schema is not null) Visit(schema, "$", "—", request, 0, fields);
        return fields;
    }

    private void Visit(JsonNode? raw, string path, string required, bool? request, int depth, List<Field> fields)
    {
        if (depth >= MaxDepth)
        {
            context.Warn("Schema expansion stopped at depth limit (8).");
            fields.Add(new Field(path, "…", required, "Nested/recursive details omitted at depth limit (8)."));
            return;
        }
        var schema = Expand(raw);
        if (Excluded(schema, request)) return;
        fields.Add(new Field(path, TypeExpanded(schema), required, Describe(schema)));
        var requiredNames = schema.Get("required").Items().Select(v => v.Text()).ToHashSet(StringComparer.Ordinal);
        foreach (var property in schema.Get("properties").Members())
            Visit(property.Value, path == "$" ? property.Key : path + "." + property.Key,
                requiredNames.Contains(property.Key) ? "yes" : "no", request, depth + 1, fields);
        if (schema.Get("items") is JsonNode items) Visit(items, path + "[]", "—", request, depth + 1, fields);
        if (schema.Get("additionalProperties") is JsonObject additional) Visit(additional, path + ".*", "no", request, depth + 1, fields);
    }

    public List<BodyExample> Examples(MediaBody media, bool? request)
    {
        if (media.Examples.Count > 0) return media.Examples;
        var schema = Expand(media.Schema);
        if (schema is JsonObject obj)
        {
            if (obj.ContainsKey("example")) return [new BodyExample("Schema example", obj["example"])];
            if (obj["examples"] is JsonArray { Count: > 0 } examples)
                return examples.Select((value, index) => new BodyExample($"Schema example {index + 1}", value)).ToList();
        }
        return [new BodyExample("Generated example (representative JSON)", Generate(media.Schema, request, 0))];
    }

    private JsonNode? Generate(JsonNode? raw, bool? request, int depth)
    {
        if (depth >= MaxDepth) return JsonValue.Create("[Nested/recursive details omitted at depth limit (8)]");
        var node = Expand(raw);
        if (node is JsonValue boolean && boolean.TryGetValue<bool>(out var allowed))
            return JsonValue.Create(allowed ? "[Any value]" : "[No value permitted by schema]");
        if (node is not JsonObject schema) return JsonValue.Create("[No schema supplied]");
        if (schema.ContainsKey("example")) return schema["example"]?.DeepClone();
        if (schema["examples"] is JsonArray { Count: > 0 } examples) return examples[0]?.DeepClone();
        foreach (var key in new[] { "const", "default" })
            if (schema.ContainsKey(key)) return schema[key]?.DeepClone();
        if (schema["enum"] is JsonArray { Count: > 0 } enums) return enums[0]?.DeepClone();
        var type = schema["type"] is JsonArray types ? types.Select(v => v.Text()).FirstOrDefault(v => v != "null") ?? "null" : schema["type"].Text();
        if (type == "object" || schema["properties"] is not null || schema["additionalProperties"] is not null)
        {
            var result = new JsonObject();
            foreach (var property in schema["properties"].Members())
                if (!Excluded(Expand(property.Value), request)) result[property.Key] = Generate(property.Value, request, depth + 1);
            if (schema["additionalProperties"] is JsonObject additional)
            {
                var name = "additionalProperty";
                while (result.ContainsKey(name)) name += "_";
                result[name] = Generate(additional, request, depth + 1);
            }
            return result;
        }
        if (type == "array" || schema["items"] is not null) return new JsonArray(Generate(schema["items"], request, depth + 1));
        return type switch
        {
            "integer" or "number" => JsonValue.Create(0),
            "boolean" => JsonValue.Create(true),
            "null" => null,
            "string" => JsonValue.Create(schema["format"].Text() switch
            {
                "date" => "2026-01-01",
                "date-time" => "2026-01-01T00:00:00Z",
                "uuid" => "00000000-0000-0000-0000-000000000000",
                "email" => "user@example.com",
                "uri" or "url" => "https://example.com",
                "binary" or "byte" => "[Binary data]",
                _ => "string"
            }),
            "file" => JsonValue.Create("[File upload]"),
            _ => JsonValue.Create(schema["x-render-note"].Text("[Any value]"))
        };
    }
}
