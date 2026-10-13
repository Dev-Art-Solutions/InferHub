using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace InferHub.Shared.ChatGpt;

/// <summary>
/// Validates an OpenAI ID token: RS256 signature against the published JWKS, issuer, audience
/// (the issued client id), expiry, and the nonce this attempt sent (phase 103). Hand-rolled — the
/// whole of it is a base64url split, an <see cref="RSA"/> import and five comparisons, which is
/// less than the package a JWT library would add to a project whose shared library has none
/// (rule 2, rule 5).
/// </summary>
/// <remarks>
/// <b>A valid ID token is an identity and not a permission.</b> Whether the account may run
/// inference on its plan is the token response's granted scopes (103 D5), never this.
/// </remarks>
public sealed class IdTokenValidator(Func<CancellationToken, Task<string>> fetchJwks, TimeProvider? time = null)
{
    /// <summary>Clock skew tolerated on <c>exp</c>, <c>nbf</c> and <c>iat</c>.</summary>
    private static readonly TimeSpan Skew = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan JwksLifetime = TimeSpan.FromHours(1);

    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1, 1);
    private JsonDocument? jwks;
    private DateTimeOffset jwksAt;

    /// <summary>JWKS fetched over the supplied client from <see cref="ChatGptOAuth.JwksUri"/>.</summary>
    public static IdTokenValidator Over(HttpClient http, TimeProvider? time = null)
        => new(cancellationToken => http.GetStringAsync(ChatGptOAuth.JwksUri, cancellationToken), time);

    /// <exception cref="ChatGptOAuthException">With code <c>invalid_id_token</c> and the one check that failed.</exception>
    public async Task<IdTokenClaims> ValidateAsync(
        string idToken,
        string clientId,
        string? expectedNonce,
        CancellationToken cancellationToken)
    {
        var parts = idToken.Split('.');

        if (parts.Length != 3)
        {
            throw Invalid("it is not a signed JWT");
        }

        JsonElement header;
        JsonElement payload;

        try
        {
            header = JsonDocument.Parse(ChatGptOAuth.FromBase64Url(parts[0])).RootElement;
            payload = JsonDocument.Parse(ChatGptOAuth.FromBase64Url(parts[1])).RootElement;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw Invalid("its header or payload is not base64url JSON");
        }

        if (Str(header, "alg") != "RS256")
        {
            throw Invalid($"it is signed with '{Str(header, "alg")}', and only RS256 is accepted");
        }

        var kid = Str(header, "kid");
        var key = await KeyAsync(kid, refresh: false, cancellationToken)
            ?? await KeyAsync(kid, refresh: true, cancellationToken)
            ?? throw Invalid($"no key '{kid}' is in OpenAI's published JWKS");

        using (var rsa = RSA.Create())
        {
            rsa.ImportParameters(key);

            var signed = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
            byte[] signature;

            try
            {
                signature = ChatGptOAuth.FromBase64Url(parts[2]);
            }
            catch (FormatException)
            {
                throw Invalid("its signature is not base64url");
            }

            if (!rsa.VerifyData(signed, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            {
                throw Invalid("its signature does not verify against OpenAI's published key");
            }
        }

        if (Str(payload, "iss") != ChatGptOAuth.Issuer)
        {
            throw Invalid($"its issuer is '{Str(payload, "iss")}', not {ChatGptOAuth.Issuer}");
        }

        if (!Audiences(payload).Contains(clientId, StringComparer.Ordinal))
        {
            throw Invalid("its audience is not the issued client id");
        }

        var now = clock.GetUtcNow();

        if (Num(payload, "exp") is not { } exp || DateTimeOffset.FromUnixTimeSeconds(exp) + Skew < now)
        {
            throw Invalid("it has expired");
        }

        if (Num(payload, "nbf") is { } nbf && DateTimeOffset.FromUnixTimeSeconds(nbf) - Skew > now)
        {
            throw Invalid("it is not valid yet");
        }

        if (expectedNonce is not null
            && !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(Str(payload, "nonce") ?? string.Empty),
                Encoding.UTF8.GetBytes(expectedNonce)))
        {
            throw Invalid("its nonce is not the one this sign-in sent");
        }

        var subject = Str(payload, "sub");

        if (string.IsNullOrWhiteSpace(subject))
        {
            throw Invalid("it carries no subject");
        }

        return new IdTokenClaims(
            subject,
            Str(payload, "email"),
            Str(payload, "name"),
            DateTimeOffset.FromUnixTimeSeconds(exp));
    }

    private async Task<RSAParameters?> KeyAsync(string? kid, bool refresh, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);

        try
        {
            if (jwks is null || refresh || clock.GetUtcNow() - jwksAt > JwksLifetime)
            {
                var text = await fetchJwks(cancellationToken);
                jwks?.Dispose();
                jwks = JsonDocument.Parse(text);
                jwksAt = clock.GetUtcNow();
            }

            if (!jwks.RootElement.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var key in keys.EnumerateArray())
            {
                if (Str(key, "kty") != "RSA" || (kid is not null && Str(key, "kid") != kid))
                {
                    continue;
                }

                if (Str(key, "n") is not { } n || Str(key, "e") is not { } e)
                {
                    continue;
                }

                return new RSAParameters
                {
                    Modulus = ChatGptOAuth.FromBase64Url(n),
                    Exponent = ChatGptOAuth.FromBase64Url(e)
                };
            }

            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    private static IEnumerable<string> Audiences(JsonElement payload)
    {
        if (!payload.TryGetProperty("aud", out var aud))
        {
            return [];
        }

        return aud.ValueKind switch
        {
            JsonValueKind.String => [aud.GetString()!],
            JsonValueKind.Array => aud.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString()!)
                .ToArray(),
            _ => []
        };
    }

    private static string? Str(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? Num(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n)
            ? n
            : null;

    private static ChatGptOAuthException Invalid(string why)
        => new("invalid_id_token", $"the ID token was rejected: {why}. Nothing was stored.");
}

/// <summary>The identity a validated ID token carries. <see cref="Subject"/> is the account.</summary>
public sealed record IdTokenClaims(string Subject, string? Email, string? Name, DateTimeOffset ExpiresAtUtc);
