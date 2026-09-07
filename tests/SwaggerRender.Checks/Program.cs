using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using SwaggerRender;

var failures = 0;
var count = 0;
var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var temp = Path.Combine(Path.GetTempPath(), "SwaggerRender.Checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    Check("Swagger 2 and OpenAPI 3.1 sample normalization", () =>
    {
        foreach (var sample in new[] { "swagger2.json", "openapi31.json" })
        {
            var ctx = Context(File.ReadAllText(Path.Combine(repo, "samples", sample)));
            var endpoints = new OpenApiReader(ctx).Read();
            Equal(2, endpoints.Count);
            var renderer = new SvgRenderer(new SchemaDocumentation(ctx), 1200);
            foreach (var endpoint in endpoints)
            {
                Bounds(renderer.Request(endpoint));
                Bounds(renderer.Response(endpoint));
            }
        }
    });
    Check("JSON Pointer escaping, URI escaping and arrays", () =>
    {
        var ctx = Context("""{"definitions":{"a/b~c":{"type":"string"},"space name":{"type":"integer"}},"list":[{"type":"boolean"}]}""");
        Equal("string", ctx.Resolve(Node("""{"$ref":"#/definitions/a~1b~0c"}""")).Get("type").Text());
        Equal("integer", ctx.Resolve(Node("""{"$ref":"#/definitions/space%20name"}""")).Get("type").Text());
        Equal("boolean", ctx.Resolve(Node("""{"$ref":"#/list/0"}""")).Get("type").Text());
    });
    Check("Operation parameters override inherited name/in pairs", () =>
    {
        var ctx = Context(File.ReadAllText(Path.Combine(repo, "samples", "swagger2.json")));
        var endpoints = new OpenApiReader(ctx).Read();
        Equal(1, endpoints[0].Parameters.Count);
        True(endpoints[0].Parameters[0].Description.Contains("nadjačava"));
        True(endpoints[1].Parameters[0].Description.Contains("Zajednički"));
    });
    Check("Inline examples override schema examples and preserve null", () =>
    {
        var doc = new SchemaDocumentation(Context("{}"));
        var schema = Node("""{"type":"string","example":"schema","default":"default"}""");
        var provided = new MediaBody("application/json", schema, [new BodyExample("first", JsonValue.Create("media")), new BodyExample("null", null)]);
        var result = doc.Examples(provided, true);
        Equal(2, result.Count);
        Equal("media", result[0].Value.Text());
        True(result[1].Value is null);
        Equal("schema", doc.Examples(provided with { Examples = [] }, true)[0].Value.Text());
    });
    Check("OpenAPI 3.0 and all named example references", () =>
    {
        var ctx = Context("""{"openapi":"3.0.3","paths":{"/example":{"post":{"requestBody":{"$ref":"#/components/requestBodies/R"},"responses":{"200":{"description":"OK"}}}}},"components":{"requestBodies":{"R":{"content":{"application/json":{"schema":{"type":"string"},"examples":{"first":{"value":"one"},"second":{"$ref":"#/components/examples/E"}}}}}},"examples":{"E":{"value":"two"}}}}""");
        var body = new OpenApiReader(ctx).Read()[0].Body!;
        Equal(2, body.Content[0].Examples.Count);
        Equal("two", body.Content[0].Examples[1].Value.Text());
    });
    Check("Schema examples array is retained", () =>
    {
        var doc = new SchemaDocumentation(Context("{}"));
        var examples = doc.Examples(new MediaBody("application/json", Node("""{"type":"string","examples":["one","two"]}"""), []), true);
        Equal(2, examples.Count);
    });
    Check("Nested examples take precedence over defaults", () =>
    {
        var doc = new SchemaDocumentation(Context("{}"));
        var schema = Node("""{"type":"object","properties":{"value":{"type":"string","examples":["example"],"default":"default"}}}""");
        Equal("example", doc.Examples(new MediaBody("application/json", schema, []), true)[0].Value.Get("value").Text());
    });
    Check("allOf properties and required lists merge", () =>
    {
        var schema = Node("""{"allOf":[{"type":"object","required":["a"],"properties":{"a":{"type":"string"}}},{"type":"object","required":["b"],"properties":{"b":{"type":"integer"}}}]}""");
        var doc = new SchemaDocumentation(Context("{}"));
        var fields = doc.Fields(schema, true);
        Equal("yes", fields.Single(f => f.Name == "a").Required);
        Equal("yes", fields.Single(f => f.Name == "b").Required);
        var value = doc.Examples(new MediaBody("application/json", schema, []), true)[0].Value!;
        Equal("string", value.Get("a").Text());
        Equal("0", value.Get("b").Text());
    });
    Check("oneOf and anyOf select and label their first branch", () =>
    {
        var doc = new SchemaDocumentation(Context("{}"));
        foreach (var key in new[] { "oneOf", "anyOf" })
        {
            var schema = Node("{\"" + key + "\":[{\"type\":\"string\",\"example\":\"first\"},{\"type\":\"number\"}]}");
            Equal("first", doc.Examples(new MediaBody("application/json", schema, []), true)[0].Value.Text());
            True(doc.Fields(schema, true)[0].Description.Contains($"{key}: first of 2"));
        }
    });
    Check("Generated examples and tables respect readOnly/writeOnly", () =>
    {
        var schema = Node("""{"type":"object","properties":{"id":{"type":"integer","readOnly":true},"password":{"type":"string","writeOnly":true},"name":{"type":"string"}}}""");
        var doc = new SchemaDocumentation(Context("{}"));
        foreach (var request in new[] { true, false })
        {
            var excluded = request ? "id" : "password";
            True(!doc.Fields(schema, request).Any(f => f.Name == excluded));
            True(!((JsonObject)doc.Examples(new MediaBody("application/json", schema, []), request)[0].Value!).ContainsKey(excluded));
        }
    });
    Check("Recursive property and allOf schemas terminate visibly", () =>
    {
        var ctx = Context("""{"definitions":{"Node":{"type":"object","properties":{"next":{"$ref":"#/definitions/Node"}}},"Loop":{"allOf":[{"$ref":"#/definitions/Loop"}]}}}""");
        var doc = new SchemaDocumentation(ctx);
        foreach (var name in new[] { "Node", "Loop" })
        {
            var schema = Node("{\"$ref\":\"#/definitions/" + name + "\"}");
            True(doc.Fields(schema, true).Any(f => f.Description.Contains("omitted")));
            True(doc.Examples(new MediaBody("application/json", schema, []), true)[0].Value!.ToJsonString().Contains("omitted"));
        }
        True(ctx.Warnings.Count > 0);
    });
    Check("External, missing and cyclic references produce visible warnings", () =>
    {
        var ctx = Context("""{"a":{"$ref":"#/b"},"b":{"$ref":"#/a"}}""");
        foreach (var reference in new[] { "other.json#/User", "#/missing", "#/a" })
            True(ctx.Resolve(new JsonObject { ["$ref"] = reference }).Get("x-render-note") is not null);
        Equal(3, ctx.Warnings.Count);
    });
    Check("Unsupported schema constructs remain documented", () =>
    {
        var ctx = Context("{}");
        var fields = new SchemaDocumentation(ctx).Fields(Node("""{"type":"object","not":{"required":["secret"]}}"""), true);
        True(fields[0].Description.Contains("Unsupported schema keyword: not"));
        True(ctx.Warnings.Count > 0);
    });
    Check("Arrays, enums, nullable and maps generate deterministic examples", () =>
    {
        var schema = Node("""{"type":"object","properties":{"items":{"type":"array","items":{"type":"string","enum":["chosen","other"]}},"nullable":{"type":["string","null"]},"map":{"type":"object","additionalProperties":{"type":"integer"}}}}""");
        var doc = new SchemaDocumentation(Context("{}"));
        var media = new MediaBody("application/json", schema, []);
        var first = doc.Examples(media, true)[0].Value!;
        Equal(first.ToJsonString(), doc.Examples(media, true)[0].Value!.ToJsonString());
        Equal("chosen", first.Get("items")![0].Text());
        True(doc.Fields(schema, true).Single(f => f.Name == "nullable").Type.Contains("null"));
        True(doc.Fields(schema, true).Any(f => f.Name == "map.*"));
    });
    Check("Standalone SVG escapes XML, controls and preserves Croatian text", () =>
    {
        var endpoint = Endpoint("/čćđšž/<x>&\"", "Text <script>alert(1)</script> & čćđšž\u0001");
        var svg = new SvgRenderer(new SchemaDocumentation(Context("{}")), 1200).Request(endpoint);
        var parsed = XDocument.Parse(svg.ToString());
        True(parsed.Descendants().All(e => e.Name.LocalName != "script"));
        True(parsed.ToString().Contains("&lt;script&gt;"));
        True(parsed.ToString().Contains("čćđšž"));
        Bounds(parsed);
    });
    Check("Long text and JSON fit at minimum width under non-English culture", () =>
    {
        var oldCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("hr-HR");
            var endpoint = Endpoint("/" + new string('W', 250), string.Join(" ", Enumerable.Repeat("Dugačak opis čćđšž 😀", 25))) with
            {
                Body = new RequestBody("Body", true, [new MediaBody("application/json", Node("""{"type":"string"}"""),
                    [new BodyExample("Long example", JsonValue.Create(new string('W', 500)))])])
            };
            Bounds(new SvgRenderer(new SchemaDocumentation(Context("{}")), 640).Request(endpoint));
        }
        finally { CultureInfo.CurrentCulture = oldCulture; }
    });
    Check("Sanitized and truncated filenames cannot collide within a run", () =>
    {
        var paths = new[] { "/a/b", "/a_b", "/a?b", "/" + new string('a', 150), "/" + new string('a', 151) };
        var names = paths.Select((path, index) => Cli.FileStem(index + 1, Endpoint(path, ""))).ToArray();
        Equal(paths.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        True(names.All(name => name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0));
    });
    Check("CLI generates both images, is repeatable and preserves unrelated files", () =>
    {
        var output = Path.Combine(temp, "output");
        var input = Path.Combine(repo, "samples", "openapi31.json");
        Equal(0, Cli.Run([input, "--output", output]));
        var files = Directory.GetFiles(output, "*.svg");
        Equal(4, files.Length);
        var before = files.ToDictionary(file => file, File.ReadAllText);
        var unrelated = Path.Combine(output, "keep.txt");
        File.WriteAllText(unrelated, "keep");
        Equal(0, Cli.Run([input, "--output", output]));
        Equal("keep", File.ReadAllText(unrelated));
        foreach (var file in files) { Equal(before[file], File.ReadAllText(file)); Bounds(XDocument.Load(file)); }
    });
    Check("CLI reports malformed files, unsupported versions and write failures", () =>
    {
        Equal(0, Cli.Run(["--help"]));
        Equal(1, Cli.Run([]));
        Equal(1, Cli.Run(["--width", "no"]));
        Equal(1, Cli.Run(["--output"]));
        Equal(1, Cli.Run(["--unknown"]));
        Equal(1, Cli.Run([Path.Combine(temp, "missing.json")]));
        var file = Path.Combine(temp, "input.json");
        foreach (var json in new[] { "{", "[]", "{\"openapi\":\"3.2.0\",\"paths\":{}}", "{\"openapi\":\"3.1.0\"}" })
        { File.WriteAllText(file, json); Equal(1, Cli.Run([file])); }
        File.WriteAllText(file, "{}");
        Equal(1, Cli.Run([Path.Combine(repo, "samples", "swagger2.json"), "--output", file]));
    });
}
finally
{
    if (Path.GetFullPath(temp).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
        Directory.Delete(temp, recursive: true);
}
Console.WriteLine($"{count - failures}/{count} checks passed.");
return failures == 0 ? 0 : 1;

void Check(string name, Action action)
{
    count++;
    try { action(); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + error.Message); }
}
static JsonNode Node(string json) => JsonNode.Parse(json)!;
static DocumentContext Context(string json) => new((JsonObject)Node(json));
static Endpoint Endpoint(string path, string description) => new("POST", path, "Test endpoint", description, [], null, []);
static void True(bool value) { if (!value) throw new Exception("Assertion failed."); }
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}.");
}
static void Bounds(XDocument doc)
{
    var root = doc.Root!;
    XNamespace svg = "http://www.w3.org/2000/svg";
    Equal(svg + "svg", root.Name);
    double Number(XElement element, string attribute) => double.Parse(element.Attribute(attribute)!.Value, CultureInfo.InvariantCulture);
    var width = Number(root, "width");
    var height = Number(root, "height");
    foreach (var text in root.Descendants(svg + "text"))
    {
        True(Number(text, "x") >= 0);
        True(Number(text, "y") < height);
        True(Number(text, "x") < width);
        // Natural glyph bounds depend on the viewer's fonts; the SVG must not stretch them.
        True(text.Attribute("textLength") is null);
        True(text.Attribute("lengthAdjust") is null);
    }
    foreach (var rect in root.Descendants(svg + "rect"))
    {
        True(Number(rect, "x") + Number(rect, "width") <= width + 0.1);
        True(Number(rect, "y") + Number(rect, "height") <= height + 0.1);
    }
}
