using System.Diagnostics;

namespace InferHub.Shared.ChatGpt;

/// <summary>
/// One sign-in on the machine whose browser is used (phase 103): the loopback listener first, then
/// the authorize URL, then the callback, the code exchange and the ID-token check. Used by the
/// node's sign-in verb, the solo node's page and the coordinator's sign-in helper — three callers,
/// one flow, so the order the vendor insists on is written down once.
/// </summary>
/// <remarks>
/// Nothing here writes anything. The grant comes back to the caller, which decides where the
/// tokens go — the node's own store, or (for the helper) the coordinator, over the pairing.
/// </remarks>
public sealed class ChatGptBrowserSignIn
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    private readonly LoopbackCallbackListener listener;

    private ChatGptBrowserSignIn(LoopbackCallbackListener listener, AuthorizationAttempt attempt)
    {
        this.listener = listener;
        Attempt = attempt;
    }

    public AuthorizationAttempt Attempt { get; }

    /// <summary>Never logged: a returning sign-in's URL carries the retained ID token as a hint.</summary>
    public string AuthorizeUrl => ChatGptOAuth.AuthorizeUrl(Attempt);

    public int Port => listener.Port;

    /// <summary>Binds the listener and prepares the attempt. The browser has not been opened yet.</summary>
    public static ChatGptBrowserSignIn Begin(
        string hostId,
        SignInHints hints,
        int preferredPort = ChatGptOAuth.DefaultCallbackPort,
        string? nonce = null)
    {
        var listener = LoopbackCallbackListener.Start(preferredPort);

        try
        {
            var attempt = ChatGptOAuth.NewAttempt(hostId, listener.Port, hints.ClientId, hints.IdTokenHint, hints.LoginHint, nonce);
            return new ChatGptBrowserSignIn(listener, attempt);
        }
        catch
        {
            listener.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Waits for the browser, exchanges the code and validates the ID token against this attempt's
    /// nonce. The listener is closed whatever happens.
    /// </summary>
    public async Task<SignInGrant> CompleteAsync(
        HttpClient http,
        IdTokenValidator validator,
        string? expectedRegistration,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string> query;

        using (listener)
        {
            query = await listener.WaitAsync(timeout ?? DefaultTimeout, cancellationToken);
        }

        var callback = ChatGptOAuth.ReadCallback(Attempt, query);
        var token = await ChatGptOAuth.ExchangeCodeAsync(http, Attempt, callback, cancellationToken);

        if (string.IsNullOrWhiteSpace(token.IdToken))
        {
            throw new ChatGptOAuthException("invalid_token_response", "the token response carried no ID token; nothing was stored.");
        }

        var claims = await validator.ValidateAsync(token.IdToken, callback.ClientId, Attempt.Nonce, cancellationToken);

        return new SignInGrant(callback.ClientId, token, claims, expectedRegistration);
    }

    public void Cancel() => listener.Dispose();

    /// <summary>
    /// Opens the user's default browser. Returns false where there is none to open — a service
    /// session, a container, an SSH shell — and the caller prints the URL instead.
    /// </summary>
    public static bool TryOpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Session 0 has no desktop: a service must never try (the page or the verb opens it).
                if (Environment.UserInteractive is false)
                {
                    return false;
                }

                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
                return true;
            }

            var opener = OperatingSystem.IsMacOS() ? "open" : "xdg-open";
            var start = new ProcessStartInfo(opener) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            start.ArgumentList.Add(url);
            Process.Start(start)?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
