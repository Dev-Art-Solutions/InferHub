using System.Net.Http.Json;
using System.Text.Json;

namespace InferHub.Tests;

/// <summary>
/// The same image requests, against a real Kestrel hub and a real Kestrel solo node, compared as a
/// client would see them.
/// </summary>
/// <remarks>
/// <para>
/// <c>SoloParityTests</c>' discipline (phase-37 D7), applied to the route phase 46 adds. Both sides
/// drive the <b>same worker binary</b> as a real child process, so the only thing a difference can
/// be is ours: the status, the content type, the body, the <c>Retry-After</c>.
/// </para>
/// <para>
/// The <c>created</c> timestamp and the base64 payload are excused, and nothing else is. The
/// timestamp is a clock reading; the bytes carry a per-run seed. Everything that decides what a
/// caller <em>gets</em> — the status, the envelope's shape, the reported size, the refusal sentence —
/// is compared literally.
/// </para>
/// </remarks>
public class ImageParityTests
{
    [Fact]
    public async Task AGenerationIsShapedIdenticallyOnBothHosts()
        => await CompareAsync(client => client.PostAsJsonAsync("/v1/images/generations", ImageEndpointTests.Body()));

    [Fact]
    public async Task ABatchIsShapedIdenticallyOnBothHosts()
        => await CompareAsync(client => client.PostAsJsonAsync("/v1/images/generations", ImageEndpointTests.Body(n: 2)));

    [Fact]
    public async Task AUrlResponseFormatIsRefusedIdenticallyOnBothHosts()
        => await CompareAsync(client => client.PostAsJsonAsync(
            "/v1/images/generations",
            new { model = ImageFixture.Model, prompt = "a cat", size = ImageFixture.Size, response_format = "url" }));

    [Fact]
    public async Task AMalformedSizeIsRefusedIdenticallyOnBothHosts()
        => await CompareAsync(client => client.PostAsJsonAsync(
            "/v1/images/generations",
            new { model = ImageFixture.Model, prompt = "a cat", size = "1000x1000" }));

    /// <summary>
    /// The one that phase 42 found late, on the other modality: a worker refusal has to render to
    /// the same status on both hosts, or a caller moving from a hub to a solo node sees a 400 become
    /// a 502.
    /// </summary>
    [Fact]
    public async Task AWorkerRefusalIsRefusedIdenticallyOnBothHosts()
        => await CompareAsync(client => client.PostAsJsonAsync(
            "/v1/images/generations",
            new { model = ImageFixture.Model, prompt = "a cat", size = "1024x768" }));

    [Fact]
    public async Task AMissingPromptIsRefusedIdenticallyOnBothHosts()
        => await CompareAsync(client => client.PostAsJsonAsync(
            "/v1/images/generations",
            new { model = ImageFixture.Model }));

    // ---- phase 50: editing ---------------------------------------------------------------------

    [Fact]
    public async Task AnEditIsShapedIdenticallyOnBothHosts()
        => await CompareAsync(client => client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/v1/images/edits")
            {
                Content = ImageEditTests.Form(mask: TestPng.Mask(512, 512))
            }));

    [Fact]
    public async Task AVariationIsShapedIdenticallyOnBothHosts()
        => await CompareAsync(client => client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/v1/images/variations")
            {
                Content = ImageEditTests.Form(prompt: null)
            }));

    /// <summary>
    /// The refusal that matters most: a mask the <em>worker</em> rejected has to render to the same
    /// 400 on both hosts. It is phase-42's late-found bug in the shape phase 50 could reproduce it —
    /// the hub never opened the mask, so both hosts are rendering a coded error they did not read.
    /// </summary>
    [Fact]
    public async Task AMaskWithNoAlphaIsRefusedIdenticallyOnBothHosts()
        => await CompareAsync(client => client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/v1/images/edits")
            {
                Content = ImageEditTests.Form(mask: TestPng.Create(512, 512))
            }));

    [Fact]
    public async Task AnUnknownMaskConventionIsRefusedIdenticallyOnBothHosts()
        => await CompareAsync(client =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/images/edits")
            {
                Content = ImageEditTests.Form(mask: TestPng.Mask(512, 512))
            };

            request.Headers.TryAddWithoutValidation("X-InferHub-Mask-Convention", "inverted");
            return client.SendAsync(request);
        });

    [Fact]
    public async Task AVariationWithAPromptIsRefusedIdenticallyOnBothHosts()
        => await CompareAsync(client => client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/v1/images/variations")
            {
                Content = ImageEditTests.Form()
            }));

    /// <summary>
    /// The guard on the guard, and it is not ceremony: without it, a comparison that silently
    /// stopped comparing anything would pass forever. Phase-37 D7 and phase-42 both carry one.
    /// </summary>
    [Fact]
    public async Task TheComparisonActuallyDetectsADifference()
    {
        await using var mesh = await ImageMesh.StartAsync();
        var (solo, cleanup) = await ImageFixture.SoloAsync("--image-fail", "invalid_request");

        try
        {
            var hub = await mesh.Client.PostAsJsonAsync("/v1/images/generations", ImageEndpointTests.Body());
            var node = await solo.Client.PostAsJsonAsync("/v1/images/generations", ImageEndpointTests.Body());

            // Different statuses: the hub's worker generates and the node's was started with
            // --image-fail. If the assertions below stopped reading the response, this test would go
            // green and every other one in the file would become decoration.
            Assert.NotEqual(hub.StatusCode, node.StatusCode);
        }
        finally
        {
            await solo.DisposeAsync();
            cleanup.Dispose();
        }
    }

    private static async Task CompareAsync(Func<HttpClient, Task<HttpResponseMessage>> call)
    {
        await using var mesh = await ImageMesh.StartAsync();
        var (solo, cleanup) = await ImageFixture.SoloAsync();

        try
        {
            var hub = await call(mesh.Client);
            var node = await call(solo.Client);

            Assert.Equal(hub.StatusCode, node.StatusCode);

            Assert.Equal(
                hub.Content.Headers.ContentType?.ToString(),
                node.Content.Headers.ContentType?.ToString());

            Assert.Equal(
                hub.Headers.TryGetValues("Retry-After", out var hubRetry) ? hubRetry.Single() : null,
                node.Headers.TryGetValues("Retry-After", out var nodeRetry) ? nodeRetry.Single() : null);

            Assert.Equal(
                Comparable(await hub.Content.ReadAsStringAsync()),
                Comparable(await node.Content.ReadAsStringAsync()));

            // Served-By is the one header that must *differ* where it is set at all: it exists to
            // tell a client which shape answered, and a solo node claiming to be a mesh node would
            // be the lie it prevents. A request refused before dispatch sets it on neither host —
            // and "one of them set it" is itself a difference worth failing on.
            var hubServedBy = hub.Headers.TryGetValues("X-InferHub-Served-By", out var h) ? h.Single() : null;
            var nodeServedBy = node.Headers.TryGetValues("X-InferHub-Served-By", out var n) ? n.Single() : null;

            Assert.Equal(hubServedBy is null, nodeServedBy is null);

            if (hubServedBy is not null)
            {
                Assert.Equal("node", hubServedBy);
                Assert.Equal("node-solo", nodeServedBy);
            }
        }
        finally
        {
            await solo.DisposeAsync();
            cleanup.Dispose();
        }
    }

    /// <summary>
    /// The body with the two genuinely per-run values replaced: <c>created</c> is a clock reading,
    /// and <c>b64_json</c> carries a seed the worker chose. The <b>lengths</b> of the base64 strings
    /// are kept, so a host that returned a different number of images or a truncated one still
    /// fails.
    /// </summary>
    private static string Comparable(string body)
    {
        if (!body.TrimStart().StartsWith('{'))
        {
            return body;
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        if (!root.TryGetProperty("data", out var data) || data.ValueKind is not JsonValueKind.Array)
        {
            return body;
        }

        var items = data.EnumerateArray()
            .Select(item => new
            {
                bytes = item.GetProperty("b64_json").GetString()!.Length,
                size = item.TryGetProperty("size", out var size) ? size.GetString() : null,
                hasSeed = item.TryGetProperty("seed", out var seed) && seed.ValueKind is JsonValueKind.Number
            })
            .ToArray();

        return JsonSerializer.Serialize(items);
    }
}
