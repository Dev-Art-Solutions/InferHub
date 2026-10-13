using InferHub.Node.Backends.ChatGpt;
using InferHub.Shared.ChatGpt;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace InferHub.Node.LocalApi;

/// <summary>
/// The solo node's own "Connect with ChatGPT" page (phase 103), beside phases 82 and 102's, under the
/// same loopback-guarded <c>/api/</c>. The node starts the <c>127.0.0.1</c> listener itself and the
/// page opens the authorize URL in this browser — which works when the browser is on this machine,
/// and the page says so. Anywhere else, the <c>chatgpt login</c> verb at the node's own console is
/// the way in.
/// </summary>
/// <remarks>
/// The node never opens a browser from here: a service has no desktop to open one on. The page does,
/// and the URL it is handed carries no token — a returning sign-in sends only the login hint, not the
/// retained ID token, so nothing credential-shaped is ever in this page's JavaScript.
/// </remarks>
internal static class LocalChatGptAdminEndpoints
{
    private static readonly object Gate = new();
    private static ChatGptBrowserSignIn? pending;
    private static string? lastOutcome;

    public static IEndpointRouteBuilder MapLocalChatGptAdminEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/chatgpt", () => Results.Content(Page, "text/html; charset=utf-8"));

        app.MapGet("/api/admin/chatgpt/state", async (NodeChatGpt chatGpt, CancellationToken cancellationToken) =>
        {
            var state = chatGpt.Session.State();
            IReadOnlyList<ChatGptModel>? models = null;
            string? modelsError = null;

            if (state is { SignedIn: true, PlanUsage: true })
            {
                try
                {
                    models = await chatGpt.Session.ListModelsAsync(chatGpt.Options.BaseUrl, cancellationToken);
                }
                catch (Exception ex) when (ex is ChatGptUpstreamException or ChatGptUnavailableException or HttpRequestException or TaskCanceledException)
                {
                    modelsError = ex.Message;
                }
            }

            lock (Gate)
            {
                return Results.Json(new
                {
                    hostId = chatGpt.Session.HostId(),
                    directory = chatGpt.Directory,
                    account = state,
                    models,
                    modelsError,
                    pending = pending is not null,
                    lastOutcome
                }, LocalApiEndpoints.JsonOptions);
            }
        });

        app.MapPost("/api/admin/chatgpt/connect", async (HttpContext context, NodeChatGpt chatGpt, ILoggerFactory loggers) =>
        {
            var newAccount = context.Request.Query.ContainsKey("newAccount");
            var account = context.Request.Query["account"].FirstOrDefault();
            ChatGptBrowserSignIn signIn;
            SignInHints hints;

            try
            {
                var full = chatGpt.Session.Hints(account, newAccount);
                hints = full with { IdTokenHint = null };
            }
            catch (ChatGptStoreException ex)
            {
                return Error(ex.Message);
            }

            lock (Gate)
            {
                // One at a time: a second listener would be a second state nobody is waiting on.
                pending?.Cancel();
                signIn = ChatGptBrowserSignIn.Begin(chatGpt.Session.HostId(), hints, chatGpt.Options.CallbackPort);
                pending = signIn;
                lastOutcome = null;
            }

            var logger = loggers.CreateLogger("InferHub.Node.ChatGpt");

            _ = Task.Run(async () =>
            {
                string outcome;

                try
                {
                    using var http = chatGpt.CreateHttpClient();
                    var grant = await signIn.CompleteAsync(http, chatGpt.Session.Validator, hints.RegistrationId, null, CancellationToken.None);
                    var state = await chatGpt.Session.ApplySignInAsync(grant, CancellationToken.None);

                    outcome = state.PlanUsage
                        ? $"Signed in as {state.Email ?? state.Account}; ChatGPT plan usage is granted."
                        : $"Signed in as {state.Email ?? state.Account}, but ChatGPT plan usage was not granted — this node refuses inference until you reconnect and allow it.";
                    logger.LogInformation("This node signed in to ChatGPT (registration {Account}, plan usage {PlanUsage})", state.Account, state.PlanUsage);
                }
                catch (Exception ex) when (ex is ChatGptOAuthException or ChatGptStoreException or HttpRequestException or ObjectDisposedException or System.Net.Sockets.SocketException)
                {
                    outcome = $"The sign-in did not complete: {ex.Message}";
                    logger.LogWarning("This node's ChatGPT sign-in did not complete: {Message}", ex.Message);
                }

                lock (Gate)
                {
                    if (ReferenceEquals(pending, signIn))
                    {
                        pending = null;
                        lastOutcome = outcome;
                    }
                }
            });

            return Results.Json(new { authorizeUrl = signIn.AuthorizeUrl, redirectUri = signIn.Attempt.RedirectUri }, LocalApiEndpoints.JsonOptions);
        });

        app.MapPost("/api/admin/chatgpt/disconnect", async (NodeChatGpt chatGpt, CancellationToken cancellationToken) =>
        {
            try
            {
                var revoked = await chatGpt.Session.SignOutAsync(null, cancellationToken);
                return Results.Json(new { signedOut = true, revoked }, LocalApiEndpoints.JsonOptions);
            }
            catch (ChatGptStoreException ex)
            {
                return Error(ex.Message);
            }
        });

        return app;
    }

    private static IResult Error(string message) =>
        Results.Json(new { error = message }, LocalApiEndpoints.JsonOptions, statusCode: StatusCodes.Status400BadRequest);

    private const string Page = """
        <!doctype html>
        <html>
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>InferHub node — ChatGPT</title>
        <style>
          body { font-family: system-ui, sans-serif; max-width: 860px; margin: 2rem auto; padding: 0 1rem; color: #1a1a1a; }
          button { padding: 0.5rem 1.25rem; margin-right: 0.5rem; }
          .muted { color: #666; font-size: 0.85rem; }
          .err { color: #b00020; }
          #note { margin-top: 1rem; padding: 0.75rem; background: #f4f4f4; border-radius: 4px; }
          li { margin: 0.2rem 0; }
        </style>
        </head>
        <body>
        <h1>ChatGPT on this node</h1>
        <p class="muted">This node's own ChatGPT account — separate from the coordinator's and from every other node's.
        Inference runs on your ChatGPT plan through OpenAI's Responses API; nothing ever falls back to a paid API key.
        Signing in here works when this browser is on the node's own machine. Otherwise run
        <code>chatgpt login</code> at the node's console.</p>
        <div id="state">Loading…</div>
        <p>
          <button id="connect">Continue with ChatGPT</button>
          <button id="add">Add another account</button>
          <button id="disconnect">Disconnect</button>
        </p>
        <div id="note" style="display:none"></div>
        <h2>Models this account can use</h2>
        <ul id="models"><li class="muted">—</li></ul>
        <script>
          const q = (id) => document.getElementById(id);
          const esc = (s) => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
          let polling = null;

          async function load() {
            const res = await fetch('/api/admin/chatgpt/state');
            const s = await res.json();
            const a = s.account || {};
            q('state').innerHTML =
              (a.signedIn ? ('<b>Signed in</b> as ' + esc(a.email || a.account) + (a.planUsage ? ' — plan usage granted.' : ' — <span class="err">ChatGPT plan usage NOT granted; inference is off. Reconnect and allow it.</span>')) : '<b>Not signed in.</b>') +
              (a.usagePausedUntilUtc ? '<div class="err">Usage limit reached; paused until ' + esc(new Date(a.usagePausedUntilUtc).toLocaleTimeString()) + '.</div>' : '') +
              (a.lastError ? '<div class="err">' + esc(a.lastError) + '</div>' : '') +
              '<div class="muted">host ' + esc(s.hostId) + ' · store ' + esc(s.directory) + '</div>';
            q('connect').textContent = a.signedIn ? 'Reconnect' : 'Continue with ChatGPT';
            q('disconnect').disabled = !a.signedIn;
            q('models').innerHTML = (s.models && s.models.length)
              ? s.models.map(m => '<li><code>' + esc(m.slug) + '</code> ' + esc(m.displayName) + '</li>').join('')
              : '<li class="muted">' + esc(s.modelsError || (a.signedIn ? 'none' : 'sign in to see them')) + '</li>';
            if (s.lastOutcome) { note(s.lastOutcome); }
            if (!s.pending && polling) { clearInterval(polling); polling = null; }
          }

          function note(text) { q('note').style.display = 'block'; q('note').textContent = text; }

          async function connect(query) {
            const res = await fetch('/api/admin/chatgpt/connect' + query, { method: 'POST' });
            const body = await res.json();
            if (!res.ok) { note(body.error || ('HTTP ' + res.status)); return; }
            window.open(body.authorizeUrl, '_blank', 'noopener');
            note('Finish signing in in the new tab. This node is listening on ' + body.redirectUri + '.');
            if (!polling) polling = setInterval(load, 2000);
          }

          q('connect').onclick = () => connect('');
          q('add').onclick = () => connect('?newAccount=1');
          q('disconnect').onclick = async () => {
            if (!confirm('Sign this node out of ChatGPT?')) return;
            const res = await fetch('/api/admin/chatgpt/disconnect', { method: 'POST' });
            const body = await res.json();
            note(body.error || (body.revoked ? 'Signed out; the session was revoked at OpenAI.' : 'Signed out locally; OpenAI did not confirm the revocation — disconnect the app in ChatGPT Settings to be sure.'));
            load();
          };
          load();
        </script>
        </body>
        </html>
        """;
}
