using InferHub.Coordinator.Auth;

namespace InferHub.Coordinator.Users;

/// <summary>
/// Phase 154: <c>/api/admin/users</c> and <c>/api/admin/roles</c>. Behind the admin guard, and within it behind the
/// <c>users</c> permission (an admin key and a loopback caller hold every permission). A key is in a response exactly once —
/// the one that made it.
/// </summary>
public static class UserEndpoints
{
    public sealed record CreateUserRequest(
        string? Id,
        string? DisplayName,
        string? Role,
        string? Organisation,
        string? Priority,
        List<string>? Collections,
        ClientLimits? Limits,
        string? KeyName);

    public sealed record ChangeUserRequest(
        string? DisplayName,
        string? Role,
        bool? Disabled,
        string? Organisation,
        string? Priority,
        List<string>? Collections,
        ClientLimits? Limits);

    public sealed record NewKeyRequest(string? Name);

    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/roles", (UserDirectory users) => Results.Ok(new
        {
            roles = users.Roles().Select(r => new
            {
                name = r.Key,
                builtIn = BuiltInRoles.All.ContainsKey(r.Key),
                permissions = r.Value
            }),
            permissions = Permissions.All
        }));

        app.MapGet("/api/admin/users", (UserDirectory users, Mcp.OAuthStore oauth) => Results.Ok(new
        {
            persistence = users.Persistence,
            users = users.All().Select(u => View(u, oauth))
        }));

        app.MapGet("/api/admin/users/{id}", (string id, UserDirectory users, Mcp.OAuthStore oauth)
            => users.Get(id) is { } user ? Results.Ok(View(user, oauth)) : NotFound(id));

        app.MapPost("/api/admin/users", (CreateUserRequest body, UserDirectory users, Mcp.OAuthStore oauth) => Run(() =>
        {
            var (user, key) = users.Create(
                body.Id ?? string.Empty,
                body.DisplayName,
                body.Role,
                new UserChange(null, null, null, body.Organisation, body.Priority, body.Collections, body.Limits),
                body.KeyName);
            return Results.Created($"/api/admin/users/{user.Id}", new { user = View(user, oauth), key, keyId = user.Keys[^1].Id });
        }));

        app.MapMethods("/api/admin/users/{id}", ["PATCH"], (string id, ChangeUserRequest body, UserDirectory users, Mcp.OAuthStore oauth) => Run(() =>
        {
            var user = users.Update(id, new UserChange(body.DisplayName, body.Role, body.Disabled, body.Organisation, body.Priority, body.Collections, body.Limits));
            return Results.Ok(View(user, oauth));
        }));

        app.MapDelete("/api/admin/users/{id}", (string id, UserDirectory users, Mcp.OAuthStore oauth) =>
        {
            if (!users.Delete(id))
            {
                return NotFound(id);
            }

            oauth.RevokeUser(id.Trim().ToLowerInvariant());
            return Results.NoContent();
        });

        app.MapPost("/api/admin/users/{id}/keys", (string id, NewKeyRequest? body, UserDirectory users) => Run(() =>
        {
            var (key, secret) = users.AddKey(id, body?.Name);
            return Results.Created($"/api/admin/users/{id}/keys/{key.Id}", new { key = secret, keyId = key.Id, name = key.Name, hint = key.Hint });
        }));

        app.MapDelete("/api/admin/users/{id}/keys/{keyId}", (string id, string keyId, UserDirectory users) => Run(()
            => users.RemoveKey(id, keyId) ? Results.NoContent() : Results.Json(new { error = $"key '{keyId}' not found" }, statusCode: 404)));

        // Every OAuth session this user signed in to (an MCP client such as claude.ai or ChatGPT). Their keys stay.
        app.MapDelete("/api/admin/users/{id}/sessions", (string id, UserDirectory users, Mcp.OAuthStore oauth) =>
        {
            if (users.Get(id) is not { } user)
            {
                return NotFound(id);
            }

            return Results.Ok(new { revoked = oauth.RevokeUser(user.Id) });
        });

        return app;
    }

    private static object View(UserRecord user, Mcp.OAuthStore oauth) => new
    {
        id = user.Id,
        displayName = user.DisplayName,
        role = user.Role,
        disabled = user.Disabled,
        organisation = user.Organisation,
        priority = user.Priority,
        collections = user.Collections,
        limits = user.Limits,
        createdUtc = user.CreatedUtc,
        keys = user.Keys.Select(k => new { id = k.Id, name = k.Name, hint = "…" + k.Hint, createdUtc = k.CreatedUtc }),
        sessions = oauth.SessionsOf(user.Id)
    };

    private static IResult NotFound(string id) => Results.Json(new { error = $"user '{id}' not found" }, statusCode: 404);

    private static IResult Run(Func<IResult> action)
    {
        try
        {
            return action();
        }
        catch (UserDirectoryException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: ex.Status);
        }
    }
}
