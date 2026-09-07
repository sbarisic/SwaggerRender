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
        Equal(6, files.Length);
        Equal(2, files.Count(file => file.EndsWith(".schema.svg", StringComparison.Ordinal)));
        var before = files.ToDictionary(file => file, File.ReadAllText);
        var unrelated = Path.Combine(output, "keep.txt");
        File.WriteAllText(unrelated, "keep");
        Equal(0, Cli.Run([input, "--output", output]));
        Equal("keep", File.ReadAllText(unrelated));
        foreach (var file in files) { Equal(before[file], File.ReadAllText(file)); Bounds(XDocument.Load(file)); }
    });
    Check("All named schemas export in declaration order for both formats", () =>
    {
        foreach (var swagger in new[] { true, false })
        {
            var collection = new JsonObject
            {
                ["Unused"] = Node("""{"type":"object","properties":{"value":{"type":"string"}}}"""),
                ["Alias"] = new JsonObject { ["$ref"] = swagger ? "#/definitions/Unused" : "#/components/schemas/Unused" }
            };
            var root = swagger ? new JsonObject { ["swagger"] = "2.0", ["definitions"] = collection }
                : new JsonObject { ["openapi"] = "3.1.0", ["components"] = new JsonObject { ["schemas"] = collection } };
            var reader = new OpenApiReader(new DocumentContext(root));
            Equal(0, reader.Read().Count);
            var schemas = reader.ReadSchemas();
            Equal("Unused,Alias", string.Join(',', schemas.Select(s => s.Name)));
            var input = Path.Combine(temp, swagger ? "swagger-schemas.json" : "openapi-schemas.json");
            var output = input + ".output";
            File.WriteAllText(input, root.ToJsonString());
            Equal(0, Cli.Run([input, "--output", output]));
            Equal(2, Directory.GetFiles(output, "*.svg").Length);
            var alias = XDocument.Load(Path.Combine(output, "0002_Alias.schema.svg"));
            True(alias.Root!.Value.Contains("value"));
            Bounds(alias);
        }
    });
    Check("Standalone schema fields and examples include both access directions", () =>
    {
        var ctx = Context(File.ReadAllText(Path.Combine(repo, "samples", "schemas-only.json")));
        var named = new OpenApiReader(ctx).ReadSchemas().Single(s => s.Name == "TransactionBookingDTO");
        var doc = new SchemaDocumentation(ctx);
        var fields = doc.Fields(named.Schema, request: null);
        True(fields.Single(f => f.Name == "auditId").Description.Contains("Read only."));
        True(fields.Single(f => f.Name == "authorizationCode").Description.Contains("Write only."));
        True(fields.Any(f => f.Name == "specifications[].denomination" && f.Required == "yes"));
        var example = (JsonObject)doc.Examples(new MediaBody("application/json", named.Schema, []), request: null)[0].Value!;
        True(example.ContainsKey("auditId") && example.ContainsKey("authorizationCode"));
        Bounds(new SvgRenderer(doc, 640).Schema(named));
    });
    Check("Schema-only sample CLI exports all supplied examples and neutral titles", () =>
    {
        var output = Path.Combine(temp, "schema-samples");
        Equal(0, Cli.Run([Path.Combine(repo, "samples", "schemas-only.json"), "--output", output, "--width", "640"]));
        Equal(3, Directory.GetFiles(output, "*.schema.svg").Length);
        var image = XDocument.Load(Path.Combine(output, "0003_TransactionBookingDTOResponse.schema.svg"));
        var text = image.Root!.Value;
        True(text.Contains("Transakcija je prihvaćena.") && text.Contains("Podaci nisu ispravni."));
        True(text.Contains("Schema — TransactionBookingDTOResponse"));
        True(!text.Contains("Request body") && !text.Contains("Responses"));
        Bounds(image);
    });
    Check("Standalone schemas preserve recursion, composition and unsupported-reference notes", () =>
    {
        var ctx = Context("""{"openapi":"3.1.0","components":{"schemas":{"Node":{"type":"object","properties":{"next":{"$ref":"#/components/schemas/Node"}}},"External":{"$ref":"other.json#/Model"},"Choice":{"oneOf":[{"type":"string"},{"type":"integer"}]},"Any":true,"None":false}}}""");
        var schemas = new OpenApiReader(ctx).ReadSchemas();
        var renderer = new SvgRenderer(new SchemaDocumentation(ctx), 640);
        foreach (var schema in schemas) Bounds(renderer.Schema(schema));
        True(renderer.Schema(schemas[0]).Root!.Value.Contains("omitted"));
        True(renderer.Schema(schemas[1]).Root!.Value.Contains("External reference not loaded"));
        True(renderer.Schema(schemas[2]).Root!.Value.Contains("oneOf: first of 2"));
        True(renderer.Schema(schemas[4]).Root!.Value.Contains("not allowed"));
        True(ctx.Warnings.Count > 0);
    });
    Check("Schema names stay unique and XML-safe after sanitizing", () =>
    {
        var names = new[] { "A/B", "A_B", "../", "Čćđšž<&>", new string('W', 160), new string('W', 161) };
        var files = names.Select((name, i) => Cli.SchemaFileStem(i + 1, name)).ToArray();
        Equal(names.Length, files.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        True(files.All(name => name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0));
        var renderer = new SvgRenderer(new SchemaDocumentation(Context("{}")), 640);
        foreach (var name in names)
        {
            var image = XDocument.Parse(renderer.Schema(new NamedSchema(name, Node("""{"type":"string"}"""))).ToString());
            True(image.Root!.Value.Contains(name));
            Bounds(image);
        }
    });
    Check("Split requests separate examples and properties without stretching or losing context", () =>
    {
        var ctx = Context(File.ReadAllText(Path.Combine(repo, "samples", "openapi31.json")));
        var endpoint = new OpenApiReader(ctx).Read()[0];
        var docs = new SchemaDocumentation(ctx);
        var sections = new SvgSectionRenderer(docs, 1200).Request(endpoint).ToList();
        Equal("overview,body-01.example-01,body-01.example-02,body-01.properties", string.Join(',', sections.Select(s => s.Name)));
        True(sections[0].Image.Root!.Value.Contains("Parameters"));
        True(!sections[0].Image.Root!.Value.Contains("REQ-001"));
        True(sections[1].Image.Root!.Value.Contains("REQ-001") && !sections[1].Image.Root!.Value.Contains("REQ-002"));
        True(sections[2].Image.Root!.Value.Contains("REQ-002"));
        True(!sections[1].Image.Root!.Value.Contains("Body properties"));
        True(sections[3].Image.Root!.Value.Contains("metadata.channel"));
        True(!sections[3].Image.Root!.Value.Contains("REQ-001"));
        var wholeHeight = double.Parse(new SvgRenderer(docs, 1200).Request(endpoint).Root!.Attribute("height")!.Value, CultureInfo.InvariantCulture);
        foreach (var section in sections)
        {
            Bounds(section.Image);
            True(section.Image.Root!.Value.Contains(endpoint.Path));
            True(double.Parse(section.Image.Root.Attribute("height")!.Value, CultureInfo.InvariantCulture) < wholeHeight);
        }
    });
    Check("Split responses retain all statuses, headers, media types and example values", () =>
    {
        var schema = Node("""{"type":"object","properties":{"result":{"type":"string"}}}""");
        var endpoint = Endpoint("/responses", "") with
        {
            Responses = [
                new ApiResponse("200", "Accepted", [new Field("X-Trace", "string", "no", "Trace header")], [
                    new MediaBody("application/json", schema, [new BodyExample("first", JsonValue.Create("FIRST")), new BodyExample("second", null)]),
                    new MediaBody("application/vnd.example+json", schema, [new BodyExample("third", JsonValue.Create("THIRD"))])]),
                new ApiResponse("400", "Invalid request", [], []),
                new ApiResponse("default", "Unexpected failure", [], [])
            ]
        };
        var sections = new SvgSectionRenderer(new SchemaDocumentation(Context("{}")), 640).Response(endpoint).ToList();
        Equal(7, sections.Count);
        var overview = sections[0].Image.Root!.Value;
        True(overview.Contains("200") && overview.Contains("400") && overview.Contains("default"));
        True(overview.Contains("Unexpected failure") && !overview.Contains("FIRST"));
        True(sections.Single(s => s.Name.EndsWith("headers", StringComparison.Ordinal)).Image.Root!.Value.Contains("X-Trace"));
        True(sections.Single(s => s.Name == "response-01.body-01.example-01").Image.Root!.Value.Contains("FIRST"));
        True(sections.Single(s => s.Name == "response-01.body-01.example-02").Image.Root!.Value.Contains("null"));
        var third = sections.Single(s => s.Name == "response-01.body-02.example-01").Image.Root!.Value;
        True(third.Contains("THIRD") && third.Contains("application/vnd.example+json"));
        foreach (var section in sections) Bounds(section.Image);
        var empty = new SvgSectionRenderer(new SchemaDocumentation(Context("{}")), 640).Response(Endpoint("/empty", "")).Single();
        True(empty.Image.Root!.Value.Contains("No responses documented."));
    });
    Check("Split schemas separate the property table from all examples and retain access flags", () =>
    {
        var ctx = Context(File.ReadAllText(Path.Combine(repo, "samples", "schemas-only.json")));
        var named = new OpenApiReader(ctx).ReadSchemas();
        var renderer = new SvgSectionRenderer(new SchemaDocumentation(ctx), 640);
        var booking = renderer.Schema(named[1]).ToList();
        Equal(2, booking.Count);
        var properties = booking.Single(s => s.Name == "properties").Image.Root!.Value;
        True(properties.Contains("Read only.") && properties.Contains("Write only."));
        True(!properties.Contains("Generated example"));
        var response = renderer.Schema(named[2]).ToList();
        Equal("properties,example-01,example-02", string.Join(',', response.Select(s => s.Name)));
        True(response[1].Image.Root!.Value.Contains("Transakcija je prihvaćena."));
        True(response[2].Image.Root!.Value.Contains("Podaci nisu ispravni."));
        foreach (var section in booking.Concat(response)) Bounds(section.Image);
    });
    Check("Split CLI writes ordered unique images and preserves existing files on rerun", () =>
    {
        var output = Path.Combine(temp, "split");
        var input = Path.Combine(repo, "samples", "openapi31.json");
        Equal(0, Cli.Run([input, "--output", output, "--split-sections"]));
        var files = Directory.GetFiles(output, "*.svg");
        Equal(18, files.Length);
        True(File.Exists(Path.Combine(output, "0001_POST_api_v_version__preapproval_request.request.01.overview.svg")));
        True(File.Exists(Path.Combine(output, "0001_PreapprovalRequest.schema.01.properties.svg")));
        True(!File.Exists(Path.Combine(output, "0001_PreapprovalRequest.schema.svg")));
        var before = files.ToDictionary(file => file, File.ReadAllText);
        var keep = Path.Combine(output, "keep.svg");
        File.WriteAllText(keep, "Unrelated content");
        Equal(0, Cli.Run([input, "--split-sections", "--output", output]));
        Equal("Unrelated content", File.ReadAllText(keep));
        foreach (var file in files) { Equal(before[file], File.ReadAllText(file)); Bounds(XDocument.Load(file)); }
        Equal(0, Cli.Run([Path.Combine(repo, "samples", "swagger2.json"), "--split-sections", "--output", Path.Combine(temp, "swagger-split")]));
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
