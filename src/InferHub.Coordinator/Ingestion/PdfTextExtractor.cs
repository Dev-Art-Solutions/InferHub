using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;
// PdfPig has a *namespace* called TextExtractor and we have a *class* called TextExtractor.
// Aliasing the one type we want keeps the two from shadowing each other.
using ContentOrderTextExtractor = UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor;

namespace InferHub.Coordinator.Ingestion;

/// <summary>
/// The only file in the solution that references the PDF package (rule 5, D3): coordinator-only,
/// never <c>InferHub.Shared</c>, never the node, and no code path reaches it unless a PDF is
/// actually uploaded.
/// </summary>
public sealed class PdfTextExtractor(ILogger<PdfTextExtractor> logger) : IPdfTextExtractor
{
    /// <summary>
    /// Below this many characters of text layer per page, averaged over the document, we call it a
    /// scan. A real text page carries hundreds; a scanned one carries a stray header, a page number,
    /// or nothing at all.
    /// </summary>
    private const int MinCharsPerPage = 50;

    public ExtractedDocument Extract(byte[] content)
    {
        PdfDocument document;
        try
        {
            document = PdfDocument.Open(content, new ParsingOptions { UseLenientParsing = true });
        }
        catch (PdfDocumentEncryptedException)
        {
            throw new ExtractionFailedException(
                "this PDF is encrypted; decrypt it before uploading — InferHub does not hold document passwords");
        }
        catch (Exception ex) when (ex is not ExtractionFailedException)
        {
            throw new ExtractionFailedException($"this file could not be read as a PDF: {ex.Message}");
        }

        using (document)
        {
            var pages = new List<ExtractedPage>(document.NumberOfPages);
            foreach (var page in document.GetPages())
            {
                // ContentOrderTextExtractor puts words back into reading order; page.Text returns
                // them in the order the content stream happened to draw them, which for a
                // two-column layout is an interleaved mess that embeds as noise.
                var text = TextExtractor.Normalize(ContentOrderTextExtractor.GetText(page) ?? "");
                if (text.Length > 0)
                {
                    pages.Add(new ExtractedPage(page.Number, text));
                }
            }

            RejectIfScanned(document.NumberOfPages, pages);

            logger.LogInformation(
                "Extracted {Chars} characters from {Pages} of {Total} PDF pages",
                pages.Sum(p => p.Text.Length), pages.Count, document.NumberOfPages);

            return new ExtractedDocument(TextExtractor.Pdf, pages);
        }
    }

    /// <summary>
    /// D4 — no OCR, ever. A scanned page yields an empty or near-empty text layer, and bolting on
    /// OCR would produce something that <em>usually</em> works: a corpus of near-gibberish that
    /// retrieves plausible nonsense and surfaces months later as a model that is subtly, and
    /// unaccountably, wrong. Refusing the file is the kinder failure. If a document genuinely
    /// needs OCR, that is a decision its owner should make deliberately, with a tool they chose,
    /// before it reaches us.
    /// </summary>
    private static void RejectIfScanned(int pageCount, List<ExtractedPage> pages)
    {
        if (pageCount == 0)
        {
            throw new ExtractionFailedException("this PDF has no pages");
        }

        var totalChars = pages.Sum(p => p.Text.Length);
        if (totalChars / (double)pageCount >= MinCharsPerPage)
        {
            return;
        }

        throw new ExtractionFailedException(
            $"this PDF has almost no text layer ({totalChars} characters across {pageCount} pages) — " +
            "it looks like a scan. InferHub does not do OCR: a bad extraction does not fail, it " +
            "succeeds quietly and fills your corpus with gibberish that retrieves confidently. " +
            "Run the file through an OCR tool of your choosing and upload the result.");
    }
}
