using System.Xml.Linq;

namespace SwaggerRender;

internal sealed record SvgSection(string Name, XDocument Image);

// Each section is laid out on its own canvas so text stays at the selected font size.
internal sealed class SvgSectionRenderer(SchemaDocumentation schemas, int width)
{
    public IEnumerable<SvgSection> Request(Endpoint endpoint)
    {
        var overview = new SvgCanvas(width, endpoint, "Request · overview");
        overview.Paragraph(endpoint.Description);
        SvgRenderer.Parameters(overview, endpoint.Parameters);
        overview.Section("Request body" + (endpoint.Body is null ? "" : endpoint.Body.Required ? " · required" : " · optional"));
        if (endpoint.Body is null) overview.Paragraph("No request body.");
        else
        {
            overview.Paragraph(endpoint.Body.Description);
            if (endpoint.Body.Content.Count == 0) overview.Paragraph("No body content documented.");
            foreach (var body in endpoint.Body.Content) overview.Label(body.MediaType, small: true);
        }
        yield return new SvgSection("overview", overview.Finish());

        if (endpoint.Body is null) yield break;
        foreach (var (body, index) in endpoint.Body.Content.Select((body, index) => (body, index + 1)))
        {
            SvgCanvas Canvas()
            {
                var canvas = new SvgCanvas(width, endpoint, "Request body" + (endpoint.Body.Required ? " · required" : " · optional"));
                canvas.Paragraph(endpoint.Body.Description);
                canvas.Label(body.MediaType);
                return canvas;
            }
            foreach (var section in Body(body, request: true, $"body-{index:D2}", Canvas)) yield return section;
        }
    }

    public IEnumerable<SvgSection> Response(Endpoint endpoint)
    {
        var overview = new SvgCanvas(width, endpoint, "Responses · overview");
        if (endpoint.Responses.Count == 0) overview.Paragraph("No responses documented.");
        else overview.Table(["Code", "Description"], [0.14, 0.86],
            endpoint.Responses.Select(response => new[] { response.Code, response.Description }));
        yield return new SvgSection("overview", overview.Finish());

        foreach (var (response, responseIndex) in endpoint.Responses.Select((response, index) => (response, index + 1)))
        {
            var prefix = $"response-{responseIndex:D2}";
            SvgCanvas ResponseCanvas()
            {
                var canvas = new SvgCanvas(width, endpoint, "Response · " + response.Code);
                canvas.Paragraph(response.Description);
                return canvas;
            }
            if (response.Headers.Count > 0)
            {
                var headers = ResponseCanvas();
                headers.Section("Response headers");
                SvgRenderer.Properties(headers, response.Headers);
                yield return new SvgSection(prefix + ".headers", headers.Finish());
            }
            foreach (var (body, mediaIndex) in response.Content.Select((body, index) => (body, index + 1)))
            {
                SvgCanvas Canvas()
                {
                    var canvas = ResponseCanvas();
                    canvas.Label(body.MediaType);
                    return canvas;
                }
                foreach (var section in Body(body, request: false, $"{prefix}.body-{mediaIndex:D2}", Canvas)) yield return section;
            }
        }
    }

    public IEnumerable<SvgSection> Schema(NamedSchema schema)
    {
        SvgCanvas Canvas()
        {
            var canvas = new SvgCanvas(width, schema.Name);
            canvas.Paragraph(schemas.Description(schema.Schema));
            return canvas;
        }
        var properties = Canvas();
        properties.Section("Properties");
        SvgRenderer.Properties(properties, schemas.Fields(schema.Schema, request: null));
        yield return new SvgSection("properties", properties.Finish());
        foreach (var section in Examples(new MediaBody("application/json", schema.Schema, []), request: null, Canvas)) yield return section;
    }

    private IEnumerable<SvgSection> Body(MediaBody body, bool request, string prefix, Func<SvgCanvas> createCanvas)
    {
        foreach (var section in Examples(body, request, createCanvas)) yield return section with { Name = prefix + "." + section.Name };
        var properties = createCanvas();
        properties.Section("Body properties");
        var fields = schemas.Fields(body.Schema, request);
        if (fields.Count == 0) properties.Paragraph("No schema documented.");
        else SvgRenderer.Properties(properties, fields);
        yield return new SvgSection(prefix + ".properties", properties.Finish());
    }

    private IEnumerable<SvgSection> Examples(MediaBody body, bool? request, Func<SvgCanvas> createCanvas)
    {
        var index = 0;
        foreach (var example in schemas.Examples(body, request))
        {
            var canvas = createCanvas();
            SvgRenderer.Example(canvas, example);
            yield return new SvgSection($"example-{++index:D2}", canvas.Finish());
        }
    }
}
