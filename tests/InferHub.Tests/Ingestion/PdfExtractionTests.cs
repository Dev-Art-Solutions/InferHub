using InferHub.Coordinator.Ingestion;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace InferHub.Tests.Ingestion;

/// <summary>
/// Real PDFs, built here and parsed back — a stub would prove only that the seam is wired, and
/// the whole reason this phase spends its one dependency is on the parts a stub cannot reach.
/// </summary>
public class PdfExtractionTests
{
    private static PdfTextExtractor NewExtractor() => new(NullLogger<PdfTextExtractor>.Instance);

    [Fact]
    public void TextIsExtractedPerPageWithOneBasedPageNumbers()
    {
        var pdf = BuildPdf(
            "The employee handbook grants twenty five days of annual leave to every full time member of staff.",
            "Expenses must be submitted within thirty days of being incurred or they will not be reimbursed.");

        var document = NewExtractor().Extract(pdf);

        Assert.Equal("application/pdf", document.MediaType);
        Assert.Equal(2, document.Pages.Count);

        Assert.Equal(1, document.Pages[0].Page);
        Assert.Contains("twenty five days", document.Pages[0].Text, StringComparison.Ordinal);

        Assert.Equal(2, document.Pages[1].Page);
        Assert.Contains("thirty days", document.Pages[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AScannedPdfIsRejectedWithAnErrorAHumanCanActOn()
    {
        // A page with no text layer is what a scan looks like from here: the image is the content
        // and there is nothing to read. This must fail, not produce an empty document — a corpus
        // of near-gibberish retrieves confidently and surfaces months later as a model that is
        // subtly, unaccountably wrong (D4).
        var scanned = BuildPdf("", "");

        var ex = Assert.Throws<ExtractionFailedException>(() => NewExtractor().Extract(scanned));

        Assert.Contains("looks like a scan", ex.Message, StringComparison.Ordinal);
        Assert.Contains("does not do OCR", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APdfWithOnlyAPageNumberPerPageStillCountsAsAScan()
    {
        // The nastier case: a scan whose pages carry a stamped folio or a header, so the text
        // layer is non-empty but useless. A per-page character floor catches it; "is it empty"
        // would not.
        var barelyAnyText = BuildPdf("3", "4", "5");

        var ex = Assert.Throws<ExtractionFailedException>(() => NewExtractor().Extract(barelyAnyText));

        Assert.Contains("looks like a scan", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BytesThatAreNotAPdfFailWithAClearErrorRatherThanAStackTrace()
    {
        var ex = Assert.Throws<ExtractionFailedException>(
            () => NewExtractor().Extract("this is plainly not a PDF"u8.ToArray()));

        Assert.Contains("could not be read as a PDF", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APdfFlowsThroughTheExtractorFacadeOnItsExtensionAlone()
    {
        var pdf = BuildPdf("The leave policy grants twenty five days to every full time member of staff.");

        // Content type is octet-stream, as every browser sends: the extension has to carry it.
        var document = new TextExtractor(NewExtractor())
            .Extract(pdf, "application/octet-stream", "handbook.pdf");

        Assert.Equal("application/pdf", document.MediaType);
        Assert.Contains("twenty five days", document.Pages[0].Text, StringComparison.Ordinal);
    }

    private static byte[] BuildPdf(params string[] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        foreach (var text in pages)
        {
            var page = builder.AddPage(PageSize.A4);
            if (text.Length > 0)
            {
                page.AddText(text, 12, new PdfPoint(25, 700), font);
            }
        }

        return builder.Build();
    }
}
