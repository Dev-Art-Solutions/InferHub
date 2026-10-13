using InferHub.Coordinator.Services;
using InferHub.Shared.ChatGpt;

namespace InferHub.Coordinator.ChatGpt;

/// <summary>
/// The coordinator's ChatGPT sign-in (phase 103): the console's panel under <c>/api/admin/chatgpt</c>
/// (admin key), and the three pairing calls the sign-in helper makes (pairing code).
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no browser callback on this host</b> (103 D6). The console runs in a browser on some
/// other machine, where <c>127.0.0.1</c> is that machine — so the console creates a pairing, the
/// helper signs in there, and the session arrives here in one request body. No token is ever put in
/// a URL, handed to console JavaScript, or written to a log; the admin endpoints answer with state,
/// never with a credential.
/// </para>
/// <para>
/// The pairing paths sit outside <c>/api</c> on purpose: neither key middleware guards them, because
/// the helper holds neither key. The one-time code is their whole authentication, created behind the
/// admin key, hashed at rest, ten minutes, consumed by <c>complete</c> or <c>cancel</c>.
/// </para>
/// </remarks>
public static class ChatGptEndpoints
{
    public static IEndpointRouteBuilder MapChatGptEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin/chatgpt");

        admin.MapGet("/", (ChatGptAccounts accounts) =>
            Results.Ok(new
            {
                providers = accounts.Providers.Select(route => new
                {
                    id = route.Id,
                    account = accounts.Session(route.Id).State(),
                    mappedModels = route.Definition.ModelMap.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray()
                })
            }));

        admin.MapGet("/{id}", async (string id, ChatGptAccounts accounts, IProviderRegistry providers, CancellationToken cancellationToken) =>
        {
            if (!accounts.IsChatGptProvider(id))
            {
                return NotChatGpt(id);
            }

            var session = accounts.Session(id);
            var route = accounts.Providers.First(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            var state = session.State();

            IReadOnlyList<ChatGptModel>? models = null;
            string? modelsError = null;

            if (state is { SignedIn: true, PlanUsage: true })
            {
                try
                {
                    models = await session.ListModelsAsync(route.Definition.ResolvedBaseUrl()!, cancellationToken);
                }
                catch (Exception ex) when (ex is ChatGptUpstreamException or ChatGptUnavailableException or HttpRequestException or TaskCanceledException)
                {
                    modelsError = ex.Message;
                }
            }

            return Results.Ok(new
            {
                id = route.Id,
                hostId = session.HostId(),
                account = session.State(),
                models,
                modelsError,
                mappedModels = route.Definition.ModelMap
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => new { model = pair.Key, upstream = pair.Value })
            });
        });

        admin.MapPost("/{id}/pairing", (string id, PairingRequest? request, HttpContext context, ChatGptAccounts accounts, ILoggerFactory loggers) =>
        {
            if (!accounts.IsChatGptProvider(id))
            {
                return NotChatGpt(id);
            }

            try
            {
                var by = ActorOf(context);
                var (code, expires) = accounts.CreatePairing(id, request?.Account, request?.NewAccount ?? false, by);

                loggers.CreateLogger("InferHub.Coordinator.ChatGpt").LogInformation(
                    "ChatGPT pairing created for provider '{Provider}' by {By}, valid until {ExpiresAtUtc:u}", id, by, expires);

                // The code is shown once, to the administrator who asked for it, and never again.
                return Results.Ok(new { code, expiresAtUtc = expires, header = ChatGptPairing.HeaderName });
            }
            catch (ChatGptStoreException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        admin.MapPost("/{id}/disconnect", async (string id, AccountRequest? request, HttpContext context, ChatGptAccounts accounts, ILoggerFactory loggers, CancellationToken cancellationToken) =>
        {
            if (!accounts.IsChatGptProvider(id))
            {
                return NotChatGpt(id);
            }

            try
            {
                var revoked = await accounts.Session(id).SignOutAsync(request?.Account, cancellationToken);

                loggers.CreateLogger("InferHub.Coordinator.ChatGpt").LogInformation(
                    "ChatGPT provider '{Provider}' signed out by {By}; revocation {Outcome}", id, ActorOf(context), revoked ? "confirmed" : "NOT confirmed");

                return Results.Ok(new
                {
                    signedOut = true,
                    revoked,
                    note = revoked
                        ? "The session was revoked at OpenAI and the tokens were cleared here."
                        : "The tokens were cleared here, but OpenAI did not confirm the revocation — disconnect the app in ChatGPT Settings to be sure."
                });
            }
            catch (ChatGptStoreException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        admin.MapPost("/{id}/activate", async (string id, AccountRequest? request, ChatGptAccounts accounts, CancellationToken cancellationToken) =>
        {
            if (!accounts.IsChatGptProvider(id))
            {
                return NotChatGpt(id);
            }

            if (string.IsNullOrWhiteSpace(request?.Account))
            {
                return Results.BadRequest(new { error = "account is required" });
            }

            try
            {
                await accounts.Session(id).ActivateAsync(request.Account, cancellationToken);
                return Results.Ok(accounts.Session(id).State());
            }
            catch (ChatGptStoreException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        admin.MapPut("/{id}/model", async (string id, ModelRequest? request, ChatGptAccounts accounts, CancellationToken cancellationToken) =>
        {
            if (!accounts.IsChatGptProvider(id))
            {
                return NotChatGpt(id);
            }

            var session = accounts.Session(id);
            var route = accounts.Providers.First(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            var model = request?.Model?.Trim();

            if (!string.IsNullOrEmpty(model))
            {
                // Only a model this account's own catalogue lists: a slug it cannot use is a 400 on
                // the first prompt, discovered by whoever sent it.
                try
                {
                    var catalogue = await session.ListModelsAsync(route.Definition.ResolvedBaseUrl()!, cancellationToken);

                    if (catalogue.All(m => m.Slug != model))
                    {
                        return Results.BadRequest(new { error = $"'{model}' is not in this account's model list." });
                    }
                }
                catch (Exception ex) when (ex is ChatGptUpstreamException or ChatGptUnavailableException or HttpRequestException)
                {
                    return Results.Json(new { error = $"the account's model list could not be read: {ex.Message}" }, statusCode: StatusCodes.Status502BadGateway);
                }
            }

            try
            {
                await session.SelectModelAsync(model, cancellationToken);
                return Results.Ok(session.State());
            }
            catch (ChatGptStoreException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // ---- the helper's three calls ---------------------------------------------------

        app.MapPost(ChatGptPairing.BeginPath, (HttpContext context, ChatGptAccounts accounts) =>
        {
            if (accounts.Find(context.Request.Headers[ChatGptPairing.HeaderName]) is not { } pairing)
            {
                return BadPairing();
            }

            return Results.Ok(new PairingBeginResponse
            {
                Provider = pairing.ProviderId,
                HostId = accounts.Session(pairing.ProviderId).HostId(),
                ClientId = pairing.Hints.ClientId,
                RegistrationId = pairing.Hints.RegistrationId,
                LoginHint = pairing.Hints.LoginHint,
                Nonce = pairing.Nonce,
                ExpiresAtUtc = pairing.ExpiresAtUtc
            });
        });

        app.MapPost(ChatGptPairing.CompletePath, async (
            HttpContext context,
            PairingCompleteRequest? request,
            ChatGptAccounts accounts,
            ILoggerFactory loggers,
            CancellationToken cancellationToken) =>
        {
            // Taken, not found: a code that reached here is spent whether or not what it carried is
            // accepted, so a rejected attempt cannot be retried with a different token.
            if (accounts.Take(context.Request.Headers[ChatGptPairing.HeaderName]) is not { } pairing)
            {
                return BadPairing();
            }

            var logger = loggers.CreateLogger("InferHub.Coordinator.ChatGpt");

            if (request is null || string.IsNullOrWhiteSpace(request.ClientId) || string.IsNullOrWhiteSpace(request.IdToken))
            {
                return Results.BadRequest(new { error = "the pairing carried no client id or ID token; nothing was stored." });
            }

            if (request.ClientId == ChatGptOAuth.DynamicClientId
                || (pairing.Hints.ClientId is { } expected && request.ClientId != expected))
            {
                return Results.BadRequest(new { error = "the pairing came back for a different client than the one it was created for; nothing was stored." });
            }

            try
            {
                var session = accounts.Session(pairing.ProviderId);

                // The coordinator's own check, against the nonce the coordinator chose: an ID token
                // from any other sign-in does not verify here, whatever the helper says about it.
                var claims = await accounts.Validator().ValidateAsync(request.IdToken, request.ClientId, pairing.Nonce, cancellationToken);

                var token = new TokenResponse
                {
                    AccessToken = request.AccessToken,
                    RefreshToken = request.RefreshToken,
                    IdToken = request.IdToken,
                    TokenType = request.TokenType,
                    ExpiresIn = request.ExpiresIn,
                    Scope = request.Scope
                };

                var state = await session.ApplySignInAsync(
                    new SignInGrant(request.ClientId, token, claims, pairing.Hints.RegistrationId),
                    cancellationToken);

                logger.LogInformation(
                    "ChatGPT session imported for provider '{Provider}' (registration {Account}, plan usage {PlanUsage}) via a pairing created by {By}",
                    pairing.ProviderId,
                    state.Account,
                    state.PlanUsage ? "granted" : "NOT granted",
                    pairing.CreatedBy);

                return Results.Ok(new PairingCompleteResponse
                {
                    Email = state.Email,
                    PlanUsage = state.PlanUsage,
                    Account = state.Account,
                    Message = state.PlanUsage
                        ? $"The coordinator's provider '{pairing.ProviderId}' is signed in and may use the ChatGPT plan."
                        : $"Signed in, but ChatGPT plan usage was not granted — provider '{pairing.ProviderId}' will refuse inference until you reconnect and allow it."
                });
            }
            catch (ChatGptOAuthException ex)
            {
                logger.LogWarning("ChatGPT pairing for provider '{Provider}' rejected: {Code}", pairing.ProviderId, ex.Code);
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (HttpRequestException ex)
            {
                return Results.Json(new { error = $"OpenAI's signing keys could not be fetched to check the sign-in: {ex.Message}" }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        app.MapPost(ChatGptPairing.CancelPath, (HttpContext context, ChatGptAccounts accounts) =>
            accounts.Take(context.Request.Headers[ChatGptPairing.HeaderName]) is null ? BadPairing() : Results.Ok(new { cancelled = true }));

        return app;
    }

    // One sentence for an unknown, an expired and a spent code — the caller learns nothing by probing.
    private static IResult BadPairing()
        => Results.Json(
            new { error = "this pairing code is not valid: it is unknown, expired, or already used. Create a new one in the console." },
            statusCode: StatusCodes.Status401Unauthorized);

    private static IResult NotChatGpt(string id)
        => Results.NotFound(new { error = $"no enabled provider '{id}' of type {ProviderDefinition.TypeChatGpt} is configured on this hub." });

    private static string ActorOf(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress;
        return ip is null ? "admin" : System.Net.IPAddress.IsLoopback(ip) ? "local" : ip.ToString();
    }

    public sealed record PairingRequest(string? Account, bool? NewAccount);

    public sealed record AccountRequest(string? Account);

    public sealed record ModelRequest(string? Model);
}
