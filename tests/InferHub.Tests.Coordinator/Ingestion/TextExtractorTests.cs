using System.Text;
using InferHub.Coordinator.Ingestion;

namespace InferHub.Tests.Ingestion;

public class TextExtractorTests
{
    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    [Theory]
    [InlineData("text/plain; charset=utf-8", null, "text/plain")]
    [InlineData("text/markdown", null, "text/markdown")]
    [InlineData("application/json", null, "application/json")]
    [InlineData(null, "notes.md", "text/markdown")]
    [InlineData(null, "page.HTML", "text/html")]
    [InlineData(null, "handbook.pdf", "application/pdf")]
    // Browsers and curl send octet-stream for anything they don't recognise; falling back to the
    // extension is the difference between "works" and "415 on every upload".
    [InlineData("application/octet-stream", "readme.md", "text/markdown")]
    public void MediaTypeResolvesFromContentTypeThenExtension(string? contentType, string? fileName, string expected)
    {
        Assert.Equal(expected, TextExtractor.ResolveMediaType(contentType, fileName));
    }

    [Fact]
    public void UnknownTypeIsRejectedWithTheSupportedList()
    {
        var ex = Assert.Throws<UnsupportedMediaTypeException>(
            () => TextExtractor.ResolveMediaType("application/zip", "archive.zip"));

        Assert.Contains("text/plain", ex.Message, StringComparison.Ordinal);
        Assert.Contains("application/pdf", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainTextSurvivesIntact()
    {
        var extractor = new TextExtractor();

        var document = extractor.Extract(Bytes("Hello.\n\nSecond paragraph."), "text/plain", null);

        var page = Assert.Single(document.Pages);
        Assert.Null(page.Page);
        Assert.Equal("Hello.\n\nSecond paragraph.", page.Text);
    }

    [Fact]
    public void MarkdownKeepsItsStructure()
    {
        var extractor = new TextExtractor();
        var md = "# Title\n\n- one\n- two\n\n```js\nvar x = 1;\n```";

        var document = extractor.Extract(Bytes(md), "text/markdown", null);

        Assert.Equal(md, document.Pages[0].Text);
    }

    [Fact]
    public void HtmlLosesTagsAndKeepsBlockStructure()
    {
        var extractor = new TextExtractor();
        var html =
            "<html><head><style>p { color: red }</style><script>alert('x')</script></head>" +
            "<body><h1>Title</h1><p>First &amp; foremost.</p><p>Second.</p></body></html>";

        var text = extractor.Extract(Bytes(html), "text/html", null).Pages[0].Text;

        Assert.Contains("Title", text, StringComparison.Ordinal);
        Assert.Contains("First & foremost.", text, StringComparison.Ordinal);
        Assert.Contains("Second.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<", text, StringComparison.Ordinal);
        Assert.DoesNotContain("color: red", text, StringComparison.Ordinal);
        Assert.DoesNotContain("alert", text, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonIsFlattenedToPathValueLines()
    {
        var extractor = new TextExtractor();
        var json = """{"policy":{"name":"Leave","days":25},"tags":["hr","2026"],"retired":null}""";

        var text = extractor.Extract(Bytes(json), "application/json", null).Pages[0].Text;

        Assert.Contains("policy.name: Leave", text, StringComparison.Ordinal);
        Assert.Contains("policy.days: 25", text, StringComparison.Ordinal);
        Assert.Contains("tags[0]: hr", text, StringComparison.Ordinal);
        Assert.Contains("tags[1]: 2026", text, StringComparison.Ordinal);
        Assert.DoesNotContain("retired", text, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidJsonFailsLoudly()
    {
        var extractor = new TextExtractor();

        Assert.Throws<ExtractionFailedException>(() => extractor.Extract(Bytes("{not json"), "application/json", null));
    }

    [Fact]
    public void EmptyDocumentIsRejectedRatherThanIngestedAsNothing()
    {
        var extractor = new TextExtractor();

        Assert.Throws<ExtractionFailedException>(() => extractor.Extract(Bytes("   \n\n  "), "text/plain", null));
    }

    [Fact]
    public void PdfWithoutAnExtractorRegisteredIsAnHonestFailure()
    {
        var extractor = new TextExtractor(pdf: null);

        Assert.Throws<UnsupportedMediaTypeException>(() => extractor.Extract([1, 2, 3], "application/pdf", null));
    }

    [Fact]
    public void PdfIsDelegatedToTheRegisteredExtractor()
    {
        var extractor = new TextExtractor(new StubPdf());

        var document = extractor.Extract([1, 2, 3], null, "handbook.pdf");

        Assert.Equal("application/pdf", document.MediaType);
        Assert.Equal(2, document.Pages.Count);
        Assert.Equal(1, document.Pages[0].Page);
    }

    private sealed class StubPdf : IPdfTextExtractor
    {
        public ExtractedDocument Extract(byte[] content) => new("application/pdf",
        [
            new ExtractedPage(1, "page one"),
            new ExtractedPage(2, "page two")
        ]);
    }
}
