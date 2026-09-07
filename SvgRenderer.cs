using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml.Linq;

namespace SwaggerRender;

internal sealed class SvgRenderer(SchemaDocumentation schemas, int width)
{
    public XDocument Schema(NamedSchema schema)
    {
        var canvas = new SvgCanvas(width, schema.Name);
        canvas.Paragraph(schemas.Description(schema.Schema));
        canvas.Section("Properties");
        Properties(canvas, schemas.Fields(schema.Schema, request: null));
        canvas.Section("Examples");
        Examples(canvas, new MediaBody("application/json", schema.Schema, []), request: null);
        return canvas.Finish();
    }

    public XDocument Request(Endpoint endpoint)
    {
        var canvas = new SvgCanvas(width, endpoint, "Request");
        canvas.Paragraph(endpoint.Description);
        canvas.Section("Parameters");
        if (endpoint.Parameters.Count == 0) canvas.Paragraph("No parameters.");
        else canvas.Table(["Name", "Type", "In", "Required", "Description"], [0.22, 0.18, 0.10, 0.10, 0.40],
            endpoint.Parameters.Select(p => new[] { p.Name, p.Type, p.Location, p.Required, p.Description }));
        if (endpoint.Body is null)
        {
            canvas.Section("Request body");
            canvas.Paragraph("No request body.");
        }
        else
        {
            canvas.Section("Request body" + (endpoint.Body.Required ? " · required" : " · optional"));
            canvas.Paragraph(endpoint.Body.Description);
            Bodies(canvas, endpoint.Body.Content, request: true);
        }
        return canvas.Finish();
    }

    public XDocument Response(Endpoint endpoint)
    {
        var canvas = new SvgCanvas(width, endpoint, "Response");
        canvas.Section("Responses");
        if (endpoint.Responses.Count == 0) canvas.Paragraph("No responses documented.");
        foreach (var response in endpoint.Responses)
        {
            canvas.Section(response.Code, small: true);
            canvas.Paragraph(response.Description);
            if (response.Headers.Count > 0)
            {
                canvas.Label("Response headers");
                Properties(canvas, response.Headers);
            }
            Bodies(canvas, response.Content, request: false);
        }
        return canvas.Finish();
    }

    private void Bodies(SvgCanvas canvas, List<MediaBody> bodies, bool request)
    {
        if (bodies.Count == 0) { canvas.Paragraph("No body content documented."); return; }
        foreach (var body in bodies)
        {
            canvas.Label(body.MediaType);
            Examples(canvas, body, request);
            canvas.Label("Body properties");
            var fields = schemas.Fields(body.Schema, request);
            if (fields.Count == 0) canvas.Paragraph("No schema documented.");
            else Properties(canvas, fields);
        }
    }

    private void Examples(SvgCanvas canvas, MediaBody body, bool? request)
    {
        foreach (var example in schemas.Examples(body, request))
        {
            canvas.Label(example.Name, small: true);
            canvas.Code(example.Value?.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }) ?? "null");
        }
    }

    private static void Properties(SvgCanvas canvas, IEnumerable<Field> fields) =>
        canvas.Table(["Property", "Type", "Required", "Description"], [0.28, 0.20, 0.10, 0.42],
            fields.Select(field => new[] { field.Name, field.Type, field.Required, field.Description }));
}

internal sealed class SvgCanvas
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    private readonly XElement root;
    private readonly XElement background;
    private readonly int width;
    private readonly string color;
    private double y = 0;
    private const double Margin = 22;
    private const double TextSize = 16;
    private double InnerWidth => width - 2 * Margin;

    private SvgCanvas(int width, string title, string color)
    {
        this.width = width;
        this.color = color;
        root = new XElement(Svg + "svg", new XAttribute("width", width), new XAttribute("role", "img"),
            new XElement(Svg + "title", Clean(title)));
        background = Rect(1, 1, width - 2, 1, "#ffffff", stroke: color);
        root.Add(background);
        root.Add(Rect(2, 2, width - 4, 1, color, opacity: 0.09));
        y = 18;
    }

    public SvgCanvas(int width, string schemaName) : this(width, $"Schema — {schemaName}", "#8b929b")
    {
        Label("Schema", small: true);
        Section(schemaName);
    }

    public SvgCanvas(int width, Endpoint endpoint, string section)
        : this(width, $"{endpoint.Method} {endpoint.Path} — {section}", endpoint.Method switch
        {
            "GET" => "#61affe", "POST" => "#49cc90", "PUT" => "#fca130", "DELETE" => "#f93e3e",
            "PATCH" => "#50e3c2", "HEAD" => "#9012fe", "OPTIONS" => "#0d5aa7", _ => "#777777"
        })
    {
        root.Add(Rect(Margin, y, 100, 34, color, radius: 4));
        Text(endpoint.Method, Margin + 12, y + 23, 17, "#ffffff", bold: true);
        var paths = Wrap(endpoint.Path, InnerWidth - 116, 20, mono: true);
        foreach (var (line, index) in paths.Select((line, index) => (line, index)))
            Text(line, Margin + 116, y + 24 + index * 28, 20, "#283443", bold: true, mono: true);
        y += Math.Max(36, paths.Count * 28) + 15;
        if (!string.IsNullOrWhiteSpace(endpoint.Summary)) Paragraph(endpoint.Summary, bold: true);
        Label(section);
    }

    public void Paragraph(string text, bool bold = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        foreach (var line in Wrap(text, InnerWidth, TextSize, bold: bold))
        {
            Text(line, Margin, y + TextSize, TextSize, "#344150", bold);
            y += 23;
        }
        y += 13;
    }

    public void Label(string text, bool small = false)
    {
        var size = small ? 14 : 17;
        y += 9;
        foreach (var line in Wrap(text, InnerWidth, size, bold: true))
        {
            Text(line, Margin, y + size, size, "#344150", bold: true);
            y += size + 7;
        }
        y += 8;
    }

    public void Section(string title, bool small = false)
    {
        var size = small ? 17 : 19;
        var lines = Wrap(title, InnerWidth, size, bold: true);
        var height = lines.Count * (size + 7) + 20;
        root.Add(Rect(2, y, width - 4, height, "#ffffff", opacity: 0.88));
        root.Add(new XElement(Svg + "line", new XAttribute("x1", 2), new XAttribute("x2", width - 2),
            new XAttribute("y1", Number(y + height)), new XAttribute("y2", Number(y + height)), new XAttribute("stroke", "#d5dfdd")));
        foreach (var (line, index) in lines.Select((line, index) => (line, index)))
            Text(line, Margin, y + size + 10 + index * (size + 7), size, "#344150", bold: true);
        y += height + 15;
    }

    public void Table(string[] headers, double[] fractions, IEnumerable<string[]> rows)
    {
        var widths = fractions.Select(fraction => InnerWidth * fraction).ToArray();
        var requiredColumn = Array.IndexOf(headers, "Required");
        if (requiredColumn >= 0 && widths[requiredColumn] < 90)
        {
            widths[^1] -= 90 - widths[requiredColumn];
            widths[requiredColumn] = 90;
        }
        Row(headers, widths, header: true, alternate: false);
        var index = 0;
        foreach (var row in rows) Row(row, widths, header: false, alternate: index++ % 2 == 0);
        y += 18;
    }

    private void Row(string[] cells, double[] widths, bool header, bool alternate)
    {
        const double size = 14;
        const double lineHeight = 20;
        var wrapped = cells.Select((cell, i) => Wrap(cell, widths[i] - 18, size, bold: header)).ToArray();
        var height = Math.Max(1, wrapped.Max(lines => lines.Count)) * lineHeight + 18;
        if (header || alternate) root.Add(Rect(Margin, y, InnerWidth, height, header ? "#e5efed" : "#ffffff", opacity: header ? 1 : 0.62));
        var x = Margin;
        for (var col = 0; col < cells.Length; col++)
        {
            for (var line = 0; line < wrapped[col].Count; line++)
                Text(wrapped[col][line], x + 8, y + 23 + line * lineHeight, size,
                    cells[col] == "yes" ? "#be2732" : "#344150", bold: header);
            x += widths[col];
        }
        y += height;
        root.Add(new XElement(Svg + "line", new XAttribute("x1", Number(Margin)), new XAttribute("x2", Number(width - Margin)),
            new XAttribute("y1", Number(y)), new XAttribute("y2", Number(y)), new XAttribute("stroke", "#d5dfdd")));
    }

    public void Code(string code)
    {
        const double size = 14;
        var lines = Wrap(code, InnerWidth - 30, size, mono: true, preserveSpaces: true);
        var height = lines.Count * 20 + 26;
        root.Add(Rect(Margin, y, InnerWidth, height, "#303030", radius: 4));
        for (var i = 0; i < lines.Count; i++)
            Text(lines[i], Margin + 15, y + 23 + i * 20, size, "#c3ecc4", mono: true);
        y += height + 14;
    }

    public XDocument Finish()
    {
        var height = Math.Ceiling(y + 16);
        root.SetAttributeValue("height", Number(height));
        root.SetAttributeValue("viewBox", $"0 0 {width} {Number(height)}");
        background.SetAttributeValue("height", Number(height - 2));
        root.Elements(Svg + "rect").ElementAt(1).SetAttributeValue("height", Number(height - 4));
        return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
    }

    private XElement Rect(double x, double top, double w, double h, string fill, double radius = 0, string? stroke = null, double opacity = 1)
    {
        var element = new XElement(Svg + "rect", new XAttribute("x", Number(x)), new XAttribute("y", Number(top)),
            new XAttribute("width", Number(w)), new XAttribute("height", Number(h)), new XAttribute("fill", fill));
        if (radius > 0) element.Add(new XAttribute("rx", Number(radius)));
        if (stroke is not null) element.Add(new XAttribute("stroke", stroke));
        if (opacity < 1) element.Add(new XAttribute("fill-opacity", Number(opacity)));
        return element;
    }

    private void Text(string text, double x, double baseline, double size, string fill, bool bold = false, bool mono = false)
    {
        text = Clean(text);
        if (text.Length == 0) return;
        var element = new XElement(Svg + "text", new XAttribute("x", Number(x)), new XAttribute("y", Number(baseline)),
            new XAttribute("font-family", mono ? "Consolas, 'Liberation Mono', monospace" : "Arial, Helvetica, sans-serif"),
            new XAttribute("font-size", Number(size)), new XAttribute("fill", fill), new XAttribute(XNamespace.Xml + "space", "preserve"), text);
        if (bold) element.Add(new XAttribute("font-weight", "bold"));
        // Let the viewer use natural glyph widths; estimates are only for line wrapping.
        root.Add(element);
    }

    internal static List<string> Wrap(string text, double available, double size, bool mono = false, bool bold = false, bool preserveSpaces = false)
    {
        // Leave room for font substitution and the approximate proportional-font metrics.
        available *= mono ? 0.97 : 0.92;
        var result = new List<string>();
        foreach (var paragraph in Clean(text).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var elements = StringInfo.GetTextElementEnumerator(paragraph.Replace("\t", "    ", StringComparison.Ordinal));
            var current = new StringBuilder();
            while (elements.MoveNext())
            {
                var next = elements.GetTextElement();
                while (current.Length > 0 && Measure(current.ToString() + next, size, mono, bold) > available)
                {
                    var line = current.ToString();
                    var split = preserveSpaces ? -1 : line.LastIndexOf(' ');
                    if (split > 0)
                    {
                        result.Add(line[..split]);
                        current.Clear().Append(line[(split + 1)..]);
                    }
                    else { result.Add(line); current.Clear(); }
                }
                current.Append(next);
            }
            result.Add(current.ToString());
        }
        return result;
    }

    private static double Measure(string text, double size, bool mono, bool bold)
    {
        double units = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format) continue;
            if (rune.Value >= 0x2e80) units += 1.05;
            else if (mono) units += 0.61;
            else if (" ilI.,:;!'|`".Contains(rune.ToString(), StringComparison.Ordinal)) units += 0.28;
            else if (rune.Value == '@') units += 1.05;
            else if ("mwMW%&#".Contains(rune.ToString(), StringComparison.Ordinal)) units += 0.86;
            else if (Rune.IsUpper(rune)) units += 0.67;
            else units += 0.55;
        }
        return units * size * (bold && !mono ? 1.04 : 1);
    }

    private static string Clean(string text)
    {
        var result = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
            result.Append(rune.Value is 9 or 10 or 13 || rune.Value >= 32 && rune.Value is not 0xfffe and not 0xffff ? rune.ToString() : "�");
        return result.ToString();
    }

    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
