namespace InferHub.Shared.Upstream;

/// <summary>
/// An upstream server answered, and it answered badly. Carries the status it used.
/// </summary>
/// <remarks>
/// Phase 63 extracted this base from <c>OpenAiUpstreamException</c>, which is now one of two
/// implementations — the other being Anthropic's. A caller that only wants "the upstream failed"
/// catches this and never has to name a vendor. <b>Considered and rejected: renaming the OpenAI one
/// to something dialect-neutral</b> — 57 D10 refused exactly that rename for exactly this reason: a
/// phase that moves a hundred call sites has a bisect nobody can read, and the existing
/// <c>Assert.ThrowsAsync&lt;OpenAiUpstreamException&gt;</c> in two suites is the contract that keeps
/// working because this is a base rather than a replacement.
/// </remarks>
public abstract class UpstreamDialectException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
