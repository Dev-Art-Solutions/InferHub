using System.Text.Json;
using System.Text.Json.Serialization;

namespace InferHub.Node.Tools;

/// <summary>
/// The three things about a model that are the <em>node's</em> business (phase 48): what it is
/// called, what licence it is under, and how much of the card it wants.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is not the node learning about diffusion.</b> Nothing here reads <c>repo</c>,
/// <c>pipeline</c>, <c>variant</c>, <c>dtype</c> or the aspect buckets, and nothing here would
/// change if the worker underneath were a Go binary wrapping something else entirely — phase-41 D1's
/// line is intact. What the node learns is <em>licences and megabytes</em>, which are facts about
/// the box rather than about a pipeline class.
/// </para>
/// <para>
/// <b>Why the node reads the file at all, rather than asking the worker.</b> Two of the three
/// consumers need the answer with no worker running: <c>NodeProfileClamp</c> is pure and must refuse
/// an oversized or unlicensed recipe synchronously (phase-43 D1), and a recipe whose licence has not
/// been accepted must never be <em>fetched</em>, which is a decision that has to precede the process
/// that would fetch it. A capability declaration that arrived only after a download is a consent
/// asked after the fact.
/// </para>
/// </remarks>
public sealed record ImageRecipeInfo(
    string Id,
    string LicenseId,
    bool Permissive,
    string? LicenseUrl,
    int VramMiB,
    string Quantization,
    string Media = ImageRecipeInfo.ImageMedia)
{
    public const string ImageMedia = "image";

    public const string VideoMedia = "video";

    /// <summary>
    /// What this recipe produces, and the <em>fourth</em> field the node reads (phase 58, D3).
    /// </summary>
    /// <remarks>
    /// It is still not the node learning about diffusion (41 D1): nothing here reads <c>fps</c>,
    /// <c>durations</c>, <c>repo</c> or <c>pipeline</c>, and what <c>media</c> buys is not knowledge
    /// of what a clip is — it is <em>which recipes must state their megabytes</em>, which is this
    /// box's own subject. <b>Absent means image</b>, so the eight recipes that predate video are
    /// unchanged (40 D1's "null is today's behaviour", fourth use).
    /// </remarks>
    public bool IsVideo => string.Equals(Media, VideoMedia, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this node may run it. A permissive recipe always may; anything else needs its licence
    /// id in <c>Tools:Image:AcceptedLicenses</c>.
    /// </summary>
    public bool IsLicensed(IReadOnlyCollection<string> accepted) =>
        Permissive
        || accepted.Any(entry => !string.IsNullOrWhiteSpace(entry)
            && string.Equals(entry.Trim(), LicenseId, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Reads <c>Tools:Image:RecipeDirectory</c>. A broken file is skipped and logged, never fatal — the
/// same discipline <c>ToolManifestLoader</c> applies to a manifest, and for the same reason: one bad
/// JSON file must not take a node's inference offline.
/// </summary>
internal static class ImageRecipeCatalogue
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Every readable recipe in <paramref name="directory"/>, keyed by id. An absent directory is an
    /// empty catalogue, not an error: a node with no image worker has no recipes and that is the
    /// ordinary case.
    /// </summary>
    public static IReadOnlyDictionary<string, ImageRecipeInfo> LoadDirectory(string? directory, ILogger logger)
    {
        var catalogue = new Dictionary<string, ImageRecipeInfo>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return catalogue;
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            ImageRecipeInfo? recipe;

            try
            {
                recipe = Parse(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Skipping image recipe {Path}: it could not be read", path);
                continue;
            }

            if (recipe is null)
            {
                // The worker skips a recipe with no `revision` by name, and so does this: a
                // catalogue that counted a model the worker will never offer would budget VRAM for
                // something that cannot run, and refuse a profile naming it for the wrong reason.
                logger.LogWarning(
                    "Skipping image recipe {Path}: it needs an 'id' and a pinned 'revision' (a commit sha, not a branch).",
                    path);

                continue;
            }

            // PHASE 58, D3 — and the default is flipped for video only. Phase 48's rule is that a
            // recipe with no declared figure is admitted rather than guessed at, because inventing
            // a number would refuse a model the operator can see on the box. That is right where
            // the miss is 4-8 GB and it is a loaded gun where the same silence admits a model that
            // wants 24 GB onto a card holding 12 — and the failure lands as an out-of-memory error
            // inside somebody's four-minute job rather than as a line in this log.
            if (recipe.IsVideo && recipe.VramMiB <= 0)
            {
                logger.LogWarning(
                    "Skipping video recipe '{Recipe}' ({Path}): a video recipe must declare 'vramMiB'. "
                    + "An image recipe without one is admitted, because the miss is a few gigabytes; a "
                    + "video model's is the whole card.",
                    recipe.Id,
                    path);

                continue;
            }

            catalogue[recipe.Id] = recipe;
        }

        return catalogue;
    }

    internal static ImageRecipeInfo? Parse(string json)
    {
        var file = JsonSerializer.Deserialize<RecipeFile>(json, Json);

        if (file is null || string.IsNullOrWhiteSpace(file.Id) || string.IsNullOrWhiteSpace(file.Revision))
        {
            return null;
        }

        // A recipe that does not say is treated as NOT permissive. A recipe that forgot to say is a
        // recipe nobody has read the licence of, and defaulting the other way would make the
        // consent opt-out by accident of a missing field.
        var licence = file.License;

        return new ImageRecipeInfo(
            file.Id.Trim(),
            string.IsNullOrWhiteSpace(licence?.Id) ? "unknown" : licence.Id.Trim(),
            licence?.Permissive == true,
            string.IsNullOrWhiteSpace(licence?.Url) ? null : licence.Url.Trim(),
            file.VramMiB ?? 0,
            string.IsNullOrWhiteSpace(file.Quantization) ? "none" : file.Quantization.Trim().ToLowerInvariant(),
            string.IsNullOrWhiteSpace(file.Media)
                ? ImageRecipeInfo.ImageMedia
                : file.Media.Trim().ToLowerInvariant());
    }

    private sealed record RecipeFile(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("revision")] string? Revision,
        [property: JsonPropertyName("license")] LicenseFile? License,
        [property: JsonPropertyName("vramMiB")] int? VramMiB,
        [property: JsonPropertyName("quantization")] string? Quantization,
        [property: JsonPropertyName("media")] string? Media);

    private sealed record LicenseFile(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("permissive")] bool? Permissive,
        [property: JsonPropertyName("url")] string? Url);
}
