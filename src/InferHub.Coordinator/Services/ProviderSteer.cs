namespace InferHub.Coordinator.Services;

/// <summary>
/// What one request asked for, in the <c>X-InferHub-Provider</c> header (phase 65, D4). It can only
/// ever <b>narrow</b> what the configuration already permits: name a provider that claims the model,
/// or refuse every provider for this one request.
/// </summary>
/// <remarks>
/// <para>
/// <c>node</c> is the direction that matters most and the reason this is a header rather than an
/// operator-only setting: it is how a caller keeps one prompt off somebody else's servers without
/// anybody editing config, and it works on a hub that has no providers at all (where it is simply
/// what already happens).
/// </para>
/// <para>
/// It is deliberately <b>not</b> a body field. The body is forwarded to the upstream verbatim, and a
/// routing directive sitting inside a payload is a field a vendor will one day interpret as its own.
/// </para>
/// </remarks>
public readonly record struct ProviderSteer(string? ProviderId, bool NodeOnly)
{
    public const string HeaderName = "X-InferHub-Provider";

    /// <summary>The value that means "the fleet, and no vendor, for this request".</summary>
    public const string NodeValue = "node";

    /// <summary>No steer: the configured policy decides, exactly as it did before this header existed.</summary>
    public static readonly ProviderSteer None = new(null, false);

    public bool IsSet => NodeOnly || ProviderId is not null;

    /// <summary>
    /// Reads the header. An absent or blank value is <see cref="None"/>; anything that is not
    /// <c>node</c> is taken as a provider id and validated where the route is resolved — a hub that
    /// answered "unknown provider" here and "does not serve that model" there would be telling a
    /// client with a key which vendors exist (65 D4).
    /// </summary>
    public static ProviderSteer From(HttpRequest request)
    {
        if (!request.Headers.TryGetValue(HeaderName, out var header))
        {
            return None;
        }

        var value = header.ToString().Trim();

        if (value.Length == 0)
        {
            return None;
        }

        return string.Equals(value, NodeValue, StringComparison.OrdinalIgnoreCase)
            ? new ProviderSteer(null, NodeOnly: true)
            : new ProviderSteer(value, NodeOnly: false);
    }
}
