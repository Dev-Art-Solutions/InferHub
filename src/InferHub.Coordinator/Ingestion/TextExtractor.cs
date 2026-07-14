using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace InferHub.Coordinator.Ingestion;

/// <summary>
/// Bytes in, readable text out. Handles the formats that need no dependency — plain text,
/// Markdown, HTML, JSON — and delegates PDF to <see cref="IPdfTextExtractor"/> when one is
/// registered. Nothing here touches the PDF library unless a PDF actually arrives (D3).
/// </summary>
public sealed partial class TextExtractor(IPdfTextExtractor? pdf = null)
{
    public const string PlainText = "text/plain";
    public const string Markdown = "text/markdown";
    public const string Html = "text/html";
    public const string Json = "application/json";
    public const string Pdf = "application/pdf";

    private static readonly string[] Supported = [PlainText, Markdown, Html, Json, Pdf];

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptOrStyleRegex();

    [GeneratedRegex(@"</?(p|div|br|li|tr|h[1-6]|section|article|header|footer|blockquote|pre|table)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTagRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex AnyTagRegex();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex HorizontalSpaceRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLineRunRegex();

    public ExtractedDocument Extract(byte[] content, string? contentType, string? fileName)
    {
        var mediaType = ResolveMediaType(contentType, fileName);

        if (mediaType == Pdf)
        {
            if (pdf is null)
            {
                throw new UnsupportedMediaTypeException("PDF extraction is not available in this build");
            }
            return pdf.Extract(content);
        }

        var text = DecodeText(content);
        var extracted = mediaType switch
        {
            Html => FromHtml(text),
            Json => FromJson(text),
            _ => Normalize(text) // plain text and Markdown are already readable; Markdown's
                                 // structure is meaningful to the model, so it survives intact.
        };

        if (string.IsNullOrWhiteSpace(extracted))
        {
            throw new ExtractionFailedException($"'{mediaType}' document contained no extractable text");
        }

        return new ExtractedDocument(mediaType, [new ExtractedPage(null, extracted)]);
    }

    /// <summary>
    /// The declared content type wins; the file extension is the fallback for the many clients
    /// that send <c>application/octet-stream</c> for every upload.
    /// </summary>
    public static string ResolveMediaType(string? contentType, string? fileName)
    {
        var declared = Canonical(contentType);
        if (declared is not null) return declared;

        var byExtension = FromExtension(fileName);
        if (byExtension is not null) return byExtension;

        var what = string.IsNullOrWhiteSpace(contentType) ? fileName ?? "(no content type)" : contentType;
        throw new UnsupportedMediaTypeException(
            $"unsupported document type '{what}'; supported: {string.Join(", ", Supported)}");
    }

    private static string? Canonical(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return null;

        // Strip parameters: "text/plain; charset=utf-8".
        var semicolon = contentType.IndexOf(';');
        var bare = (semicolon >= 0 ? contentType[..semicolon] : contentType).Trim().ToLowerInvariant();

        return bare switch
        {
            PlainText or "text/x-markdown" => bare == PlainText ? PlainText : Markdown,
            Markdown => Markdown,
            Html or "application/xhtml+xml" => Html,
            Json or "text/json" => Json,
            Pdf => Pdf,
            _ => null
        };
    }

    private static string? FromExtension(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;

        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".txt" or ".text" or ".log" => PlainText,
            ".md" or ".markdown" => Markdown,
            ".html" or ".htm" => Html,
            ".json" => Json,
            ".pdf" => Pdf,
            _ => null
        };
    }

    private static string DecodeText(byte[] content)
    {
        // Honour a BOM when there is one; assume UTF-8 otherwise, which is what every text
        // document produced this decade actually is.
        using var reader = new StreamReader(new MemoryStream(content), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    internal static string FromHtml(string html)
    {
        var text = ScriptOrStyleRegex().Replace(html, "\n");
        text = BlockTagRegex().Replace(text, "\n");
        text = AnyTagRegex().Replace(text, "");
        text = WebUtility.HtmlDecode(text);
        return Normalize(text);
    }

    /// <summary>
    /// Flatten JSON to <c>path: value</c> lines. A raw JSON blob embeds poorly — the braces and
    /// quotes are most of the tokens and none of the meaning — while the flattened form reads
    /// like prose and keeps the key names, which are usually the searchable part.
    /// </summary>
    internal static string FromJson(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new ExtractionFailedException($"document is not valid JSON: {ex.Message}");
        }

        using (doc)
        {
            var sb = new StringBuilder();
            Flatten(doc.RootElement, "", sb);
            return Normalize(sb.ToString());
        }
    }

    private static void Flatten(JsonElement element, string path, StringBuilder sb)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Flatten(property.Value, path.Length == 0 ? property.Name : $"{path}.{property.Name}", sb);
                }
                break;

            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Flatten(item, $"{path}[{i++}]", sb);
                }
                break;

            case JsonValueKind.Null or JsonValueKind.Undefined:
                break;

            default:
                var value = element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    sb.Append(path).Append(": ").AppendLine(value);
                }
                break;
        }
    }

    /// <summary>Collapse runs of horizontal space and blank lines; keep paragraph boundaries.</summary>
    internal static string Normalize(string text)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        text = HorizontalSpaceRegex().Replace(text, " ");
        text = BlankLineRunRegex().Replace(text, "\n\n");

        var lines = text.Split('\n').Select(l => l.TrimEnd());
        return string.Join('\n', lines).Trim();
    }
}

/// <summary>
/// The seam PDF extraction lives behind. The one implementation is the only place in the
/// solution that references the PDF package (rule 5, D3) — coordinator-only, never Shared,
/// never the node.
/// </summary>
public interface IPdfTextExtractor
{
    ExtractedDocument Extract(byte[] content);
}
