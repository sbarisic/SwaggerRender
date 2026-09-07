using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SwaggerRender;

internal static class Cli
{
    private const string Help = """
        SwaggerRender — render Swagger 2.0 / OpenAPI 3.0–3.1 JSON to SVG images
        Usage: dotnet run -- <swagger.json> [--output <directory>] [--width <pixels>] [--split-sections]
          --output  Output directory (default: rendered)
          --width   Image width from 640 to 4096 pixels (default: 1200)
          --split-sections  Export overviews, examples and property tables as separate images
          --help    Show this help
        Writes a request SVG and a response SVG for every endpoint.
        Also writes one .schema.svg image for each named schema.
        """;

    public static int Run(string[] args)
    {
        try
        {
            if (args.Contains("--help", StringComparer.Ordinal)) { Console.WriteLine(Help); return 0; }
            string? input = null;
            var output = "rendered";
            var width = 1200;
            var splitSections = false;
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--output": output = Value(args, ref i); break;
                    case "--split-sections": splitSections = true; break;
                    case "--width":
                        if (!int.TryParse(Value(args, ref i), NumberStyles.None, CultureInfo.InvariantCulture, out width)
                            || width < 640 || width > 4096)
                            throw new ArgumentException("--width must be an integer from 640 to 4096.");
                        break;
                    default:
                        if (args[i].StartsWith('-')) throw new ArgumentException($"Unknown option: {args[i]}");
                        if (input is not null) throw new ArgumentException("Supply exactly one input JSON file.");
                        input = args[i];
                        break;
                }
            }
            if (input is null) throw new ArgumentException("An input JSON file is required. Use --help for usage.");
            var root = JsonNode.Parse(File.ReadAllText(input)) as JsonObject
                ?? throw new ArgumentException("The input must contain a JSON object.");
            var context = new DocumentContext(root);
            var reader = new OpenApiReader(context);
            var endpoints = reader.Read();
            var namedSchemas = reader.ReadSchemas();
            if (endpoints.Count == 0 && namedSchemas.Count == 0) context.Warn("No HTTP operations or named schemas were found.");
            Directory.CreateDirectory(output);
            var schemaDocumentation = new SchemaDocumentation(context);
            var renderer = new SvgRenderer(schemaDocumentation, width);
            var sections = new SvgSectionRenderer(schemaDocumentation, width);
            var imageCount = 0;
            void SaveSections(string stem, IEnumerable<SvgSection> images)
            {
                var part = 0;
                foreach (var image in images)
                {
                    image.Image.Save(Path.Combine(output, $"{stem}.{++part:D2}.{image.Name}.svg"));
                    imageCount++;
                }
            }
            for (var i = 0; i < endpoints.Count; i++)
            {
                var endpoint = endpoints[i];
                var name = FileStem(i + 1, endpoint);
                if (splitSections)
                {
                    SaveSections(name + ".request", sections.Request(endpoint));
                    SaveSections(name + ".response", sections.Response(endpoint));
                }
                else
                {
                    renderer.Request(endpoint).Save(Path.Combine(output, name + ".request.svg"));
                    renderer.Response(endpoint).Save(Path.Combine(output, name + ".response.svg"));
                    imageCount += 2;
                }
            }
            for (var i = 0; i < namedSchemas.Count; i++)
            {
                var schema = namedSchemas[i];
                var name = SchemaFileStem(i + 1, schema.Name) + ".schema";
                if (splitSections) SaveSections(name, sections.Schema(schema));
                else
                {
                    renderer.Schema(schema).Save(Path.Combine(output, name + ".svg"));
                    imageCount++;
                }
            }
            foreach (var warning in context.Warnings) Console.Error.WriteLine($"Warning: {warning}");
            Console.WriteLine($"Rendered {endpoints.Count} endpoints and {namedSchemas.Count} schemas ({imageCount} SVG images) to {Path.GetFullPath(output)}");
            return 0;
        }
        catch (Exception error) when (error is ArgumentException or JsonException or IOException or UnauthorizedAccessException
            or InvalidOperationException or NotSupportedException)
        {
            Console.Error.WriteLine($"Error: {error.Message}");
            return 1;
        }
    }

    private static string Value(string[] args, ref int index)
    {
        var option = args[index];
        if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"Missing value for {option}.");
        return args[index];
    }

    internal static string FileStem(int index, Endpoint endpoint) => $"{index:D4}_{endpoint.Method}_{SafeName(endpoint.Path)}";

    internal static string SchemaFileStem(int index, string name) => $"{index:D4}_{SafeName(name)}";

    private static string SafeName(string name)
    {
        var safe = new string(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        if (safe.Length > 100) safe = safe[..100];
        return safe.Trim('_');
    }
}
