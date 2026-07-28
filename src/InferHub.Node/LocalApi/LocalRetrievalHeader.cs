using InferHub.Shared.Vector;
using Microsoft.AspNetCore.Http;

namespace InferHub.Node.LocalApi;

/// <summary>
/// The <c>X-InferHub-Retrieve*</c> headers, parsed the way the hub parses them (phase 38).
/// </summary>
/// <remarks>
/// <para>
/// A deliberate near-copy of <c>InferenceEndpoints.TryReadRetrievalHeader</c>, minus one thing: the
/// hub checks the named collection against the calling client's scope (phase-31 D2), and a node has
/// no tenancy — one key set, one corpus, one machine. Everything else must match exactly, because
/// this is the phase-37 D6 line again: the defaults and the refusals are *behaviour a client sees*,
/// and two parsers that disagreed about what <c>X-InferHub-Retrieve-Mode: Hybrid</c> means would
/// make "change one base URL" false.
/// </para>
/// <para>
/// In particular an unknown mode or a non-boolean rerank flag is a <b>400</b>, not a silent fall
/// back to the default: a caller who asked for hybrid and quietly got vector would draw the wrong
/// conclusion from the results (phase-24 D5).
/// </para>
/// </remarks>
internal static class LocalRetrievalHeader
{
    public const string RetrieveHeader = "X-InferHub-Retrieve";
    public const string RetrieveKHeader = "X-InferHub-Retrieve-K";
    public const string RetrieveModelHeader = "X-InferHub-Retrieve-Model";
    public const string RetrieveModeHeader = "X-InferHub-Retrieve-Mode";
    public const string RerankHeader = "X-InferHub-Rerank";
    public const string SourcesHeader = "X-InferHub-Sources";

    public static bool TryRead(HttpRequest request, out RetrievalRequest retrieval)
    {
        retrieval = default!;

        if (!request.Headers.TryGetValue(RetrieveHeader, out var raw))
        {
            return false;
        }

        var collection = raw.ToString().Trim();
        if (string.IsNullOrEmpty(collection))
        {
            return false;
        }

        int? k = null;
        if (request.Headers.TryGetValue(RetrieveKHeader, out var rawK)
            && int.TryParse(rawK.ToString(), out var parsedK)
            && parsedK > 0)
        {
            k = parsedK;
        }

        string? model = null;
        if (request.Headers.TryGetValue(RetrieveModelHeader, out var rawModel))
        {
            var value = rawModel.ToString().Trim();
            if (!string.IsNullOrEmpty(value))
            {
                model = value;
            }
        }

        string? mode = null;
        if (request.Headers.TryGetValue(RetrieveModeHeader, out var rawMode))
        {
            var value = rawMode.ToString().Trim();
            if (!string.IsNullOrEmpty(value))
            {
                if (!RetrievalModes.TryParse(value, out _))
                {
                    throw new BadHttpRequestException(
                        $"invalid {RetrieveModeHeader} '{value}'; expected vector, keyword or hybrid",
                        StatusCodes.Status400BadRequest);
                }
                mode = value;
            }
        }

        bool? rerank = null;
        if (request.Headers.TryGetValue(RerankHeader, out var rawRerank))
        {
            var value = rawRerank.ToString().Trim();
            if (!string.IsNullOrEmpty(value))
            {
                if (!bool.TryParse(value, out var parsedRerank))
                {
                    throw new BadHttpRequestException(
                        $"invalid {RerankHeader} '{value}'; expected true or false",
                        StatusCodes.Status400BadRequest);
                }
                rerank = parsedRerank;
            }
        }

        retrieval = new RetrievalRequest(collection, k, model, mode, rerank);
        return true;
    }
}
