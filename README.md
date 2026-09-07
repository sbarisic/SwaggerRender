# SwaggerRender

A .NET 9 console application that reads a local Swagger/OpenAPI JSON file and writes two SVG images per endpoint (request and response), plus one standalone SVG per named schema. Images use Swagger-like method colors, parameter tables, dark example blocks, and property tables.

No NuGet packages, graphics libraries, browser processes, or network access are needed to generate the images. Only the .NET SDK is needed to build and run the source. A built application requires the .NET 9 runtime.

## Run

From this directory:

```powershell
dotnet run -- swagger.json --output rendered
```

Try the included examples:

```powershell
dotnet run -- samples/openapi31.json --output samples/rendered/openapi31
dotnet run -- samples/swagger2.json --output samples/rendered/swagger2
dotnet run -- samples/schemas-only.json --output samples/rendered/schemas-only
```

Options:

| Argument | Meaning |
| --- | --- |
| `swagger.json` | Required local JSON input file; quote paths containing spaces. |
| `--output <directory>` | Output directory. Default: `rendered`, relative to the current directory. |
| `--width <pixels>` | Image width, from 640 to 4096 pixels. Default: 1200. |
| `--split-sections` | Write each logical section as a separate SVG instead of the combined request, response, and schema images. |
| `--help` | Show usage. |

The project targets `net9.0`; `global.json` selects an installed .NET 9 SDK. It does not require the SDK version used during development.

## Output

For the first sample endpoint, the output filenames are:

```text
0001_POST_api_v_version__preapproval_request.request.svg
0001_POST_api_v_version__preapproval_request.response.svg
```

The prefix follows endpoint order in the input document and prevents collisions between sanitized paths. Filenames use at most 100 characters from the path. Each response image contains every documented response, including `default` and responses without bodies. Each documented body media type and supplied inline example gets its own section.

Every model in OpenAPI `components.schemas` or Swagger 2 `definitions` is also exported automatically, even if no endpoint references it. Schema files use their own declaration-order numbering and a `.schema.svg` suffix, for example `0001_SpecificationDTO.schema.svg`. Names are sanitized and shortened using the same rules as endpoint paths; the prefix prevents filename collisions. No additional command-line option is needed.

Each schema image has a neutral gray heading with the model name, a description, a property table, and supplied or generated JSON examples. Nested references and composition use the same rules as endpoint bodies. Standalone schemas retain both read-only and write-only fields and label them in their descriptions. A document containing only named schemas is accepted without `paths`; it still needs a supported `openapi` or `swagger` version. Bare JSON Schema files are not accepted.

Repeated runs replace matching filenames and preserve other files. If endpoints are removed or reordered, older images can remain; use a fresh output directory when you need an exact export. A write failure can leave earlier images from that run in place.

Text and JSON wrap inside the image. Images grow vertically to fit their contents; large schemas produce tall images. Use section splitting below to keep examples and tables in separate images. Individual sections are not paginated. The SVGs contain ordinary text and shapes, with inline styles and no linked assets, scripts, or embedded HTML. Text uses local Arial/Helvetica and Consolas/monospace font fallbacks at their natural proportions. Width estimates are used only for wrapping, with some spare room for font substitution; exact glyph widths depend on the viewer's installed fonts.

Generated sample images are included under `samples/rendered`. Open the SVGs in an SVG-capable viewer. Word generation and Word compatibility testing are deferred.

## Split images for documents

To insert smaller pieces into a document without shrinking a whole endpoint image, use:

```powershell
dotnet run -- swagger.json --output rendered-sections --split-sections
```

The images keep their normal font sizes. Each piece repeats the endpoint method/path or schema name, plus the relevant status code and media type. Sections are laid out independently, so text and table rows are not cropped.

| Source | Separate images |
| --- | --- |
| Request | Overview with description, parameters, and body metadata; each body example; each media type's property table. |
| Responses | Compact table of all status codes and descriptions, including responses without bodies; headers per response where present; each body example; each media type's property table. |
| Named schema | Property table with description; each supplied or generated example. |

For example, a request with two JSON examples produces:

```text
0001_POST_api_v_version__preapproval_request.request.01.overview.svg
0001_POST_api_v_version__preapproval_request.request.02.body-01.example-01.svg
0001_POST_api_v_version__preapproval_request.request.03.body-01.example-02.svg
0001_POST_api_v_version__preapproval_request.request.04.body-01.properties.svg
```

Response filenames use `response-01`, `response-02`, etc. in declaration order, with the actual status code shown inside the image. Media types and examples also use declaration-order indexes so similarly named items cannot collide. A split schema starts with a filename such as `0001_SpecificationDTO.schema.01.properties.svg`.

Without `--split-sections`, output remains combined as before. Split mode writes only the section images; it does not delete existing combined files. Use a separate output directory to keep the two exports apart. A single very large table or example can still make a tall section image.

Included split examples are under `samples/rendered/sections`. Regenerate them with:

```powershell
dotnet run -- samples/openapi31.json --output samples/rendered/sections/openapi31 --split-sections
dotnet run -- samples/schemas-only.json --output samples/rendered/sections/schemas-only --split-sections
```

## Supported input

- Swagger 2.0 and OpenAPI 3.0.x / 3.1.x JSON, with operations under `paths` in document order.
- Path, query, header, and cookie parameter summaries. Operation parameters override inherited parameters with the same name and location.
- Swagger body and form-data parameters; OpenAPI request bodies; response headers and bodies.
- Same-document JSON Pointer references in schemas, parameters, request bodies, responses, and examples, including `~0`, `~1`, URI escapes, and array indices.
- Nested objects, arrays, enums, formats, defaults, nullable types, boolean schemas, and schema-valued maps (`additionalProperties`). `$` denotes the root, `[]` denotes array items, and `.*` denotes map values in property tables. Nested required flags apply to their immediate parent.
- Common `allOf` object composition, combining properties and required lists. For conflicting annotations, the containing schema wins, then the first branch. `oneOf` and `anyOf` show their first branch, with a note in the property table.
- Croatian characters and other Unicode text. XML metacharacters are escaped; characters forbidden by XML are replaced. Descriptions are rendered as plain text, including any Markdown syntax present in the input.

Example selection:

1. Use supplied media-level examples (or Swagger response examples), including explicit `null` values and referenced inline examples.
2. Otherwise show the schema's `example`, or all values in its `examples` array.
3. Otherwise generate representative JSON. For each nested value, prefer `example`, then the first schema `examples` entry, `const`, `default`, the first enum value, and finally a placeholder for its type.

Generated examples and property tables omit `readOnly` fields in requests and `writeOnly` fields in responses. Supplied examples are preserved as written. Generated arrays contain one representative item, numbers use zero, and dates use fixed sample values. These examples illustrate structure; they are not guaranteed to satisfy validation constraints such as patterns, numeric limits, or array lengths. Non-JSON media bodies are also illustrated as representative JSON; the renderer does not serialize XML, multipart, or binary payloads.

Expansion stops at eight schema levels and adds a visible omission marker. Unsupported external references, unresolved references, recursive reference chains, anchors, and selected advanced schema keywords produce labeled notes and console warnings. Referenced files, URLs, and external example values are never fetched.

This is a documentation renderer, not a complete OpenAPI or JSON Schema validator. It does not implement schema dialect/resource resolution through `$id`, discriminator dispatch, validation of composed constraints, callbacks, webhooks, security flows, links, or general vendor extensions. Keywords such as `not`, conditionals, dynamic references, tuple schemas, and pattern properties are summarized with warnings. YAML, PNG export, and Word document generation are outside this version.

Invalid JSON, unsupported document versions, documents lacking both `paths` and named schemas, malformed schema collections, invalid CLI options, and file access failures return exit code `1`. Successful rendering returns `0`; nonfatal documentation warnings go to standard error.

## Build and verify

```powershell
dotnet build
dotnet run --project tests/SwaggerRender.Checks
```

The checks are a separate console project with a project reference and no test-framework packages. They exercise sample parsing, references, parameter overrides, example precedence, composition, direction-specific fields, recursion, warnings, XML/Unicode handling, layout bounds, filename collisions, repeatability, and CLI failures. Temporary test files are created in the OS temp directory and removed afterward.

Implementation responsibilities are split between the CLI, document reader/reference resolver, schema documentation and example generation, and SVG layout. All application types are internal; the only public interface is the command line.

## License

Licensed under the [MIT License](LICENSE). Copyright (c) 2026 Saša Barišić.
