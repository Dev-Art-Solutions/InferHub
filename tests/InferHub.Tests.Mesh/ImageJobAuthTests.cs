using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace InferHub.Tests;

/// <summary>
/// Another client's job id is a <c>404</c>, byte-identical to one that does not exist — never a
/// <c>403</c> (phase 47; phase-25 D4's reasoning for the fourth time).
/// </summary>
/// <remarks>
/// A <c>403</c> would answer a question the caller is not entitled to ask: it says "this id names
/// something". On a surface whose ids are guessable only by having been issued one, the difference
/// between "not yours" and "not there" <em>is</em> the isolation boundary. This suite fails if that
/// ever becomes a 403, and it compares the two bodies rather than only the two statuses, because a
/// status that matches and a message that does not is the same leak with an extra step.
/// </remarks>
public class ImageJobAuthTests
{
    [Fact]
    public async Task AnotherClientsJobIsIndistinguishableFromOneThatDoesNotExist()
    {
        await using var mine = await ImageMesh.StartAsync(clientId: "client-a");

        var response = await mine.Client.PostAsJsonAsync("/api/images/jobs", ImageEndpointTests.Body());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var realId = document.RootElement.GetProperty("id").GetString()!;

        // A second hub with a different identity, standing in for a second tenant on the same one.
        await using var theirs = await ImageMesh.StartAsync(clientId: "client-b");

        var imaginary = Guid.NewGuid().ToString();

        foreach (var (method, suffix) in Routes())
        {
            var real = await Send(theirs, method, realId, suffix);
            var fake = await Send(theirs, method, imaginary, suffix);

            Assert.Equal(HttpStatusCode.NotFound, real.Status);
            Assert.Equal(HttpStatusCode.NotFound, fake.Status);

            // Byte-identical modulo the id itself. Anything else lets a caller tell a job that
            // exists but is not theirs from one that was never issued.
            Assert.Equal(fake.Body.Replace(imaginary, "ID"), real.Body.Replace(realId, "ID"));
        }
    }

    [Fact]
    public async Task AMalformedIdIsTheSame404AsAWellFormedOne()
    {
        await using var mesh = await ImageMesh.StartAsync();

        var malformed = await mesh.Client.GetAsync("/api/images/jobs/not-a-guid");

        Assert.Equal(HttpStatusCode.NotFound, malformed.StatusCode);
        Assert.Contains("not found", await malformed.Content.ReadAsStringAsync());
    }

    private static IEnumerable<(HttpMethod Method, string Suffix)> Routes() =>
    [
        (HttpMethod.Get, string.Empty),
        (HttpMethod.Get, "/content/0"),
        (HttpMethod.Get, "/events"),
        (HttpMethod.Delete, string.Empty)
    ];

    private static async Task<(HttpStatusCode Status, string Body)> Send(
        ImageMesh mesh,
        HttpMethod method,
        string id,
        string suffix)
    {
        using var request = new HttpRequestMessage(method, $"/api/images/jobs/{id}{suffix}");
        using var response = await mesh.Client.SendAsync(request, HttpCompletionOption.ResponseContentRead);

        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }
}
