using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using InferHub.Shared.Ollama;
using InferHub.Shared.Vector;
using P = InferHub.Coordinator.Users.Permissions;

namespace InferHub.Coordinator.Mcp;

/// <summary>
/// The tools <c>/mcp</c> offers, each behind one permission (154 D4). They are thin on purpose: <c>chat</c> is
/// <see cref="InferenceCore.DispatchAsync"/> with an Ollama body, exactly what <c>/api/chat</c> runs — admission, the
/// organisation scope, the warm tier, failover, a provider's turn, the usage meter and the audit line all happen there, once
/// (154 D6). A tool that reached a node by any other path would be a second dispatcher.
/// </summary>
public sealed class McpTools(ILogger<McpTools> logger)
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private sealed record Tool(string Name, string Title, string Permission, string Description, JsonObject Schema, bool ReadOnly);

    private static readonly Tool[] All =
    [
        new("list_models", "List models", P.Models,
            "Every model you may use on this InferHub: models on its nodes, node groups that split a big model across machines, "
            + "models other coordinators in its cluster serve, and cloud models its operator connected. Each comes with what it can "
            + "do (chat, embed, …) and where it runs.",
            Schema(new JsonObject
            {
                ["capability"] = new JsonObject { ["type"] = "string", ["description"] = "Only models that can do this: chat, embed, …" }
            }),
            ReadOnly: true),
        new("chat", "Chat with a model", P.Inference,
            "Send a conversation to a model on this InferHub and get its reply. Use list_models for the names. Give either "
            + "messages (the whole conversation) or prompt (one user turn); system is an optional instruction.",
            Schema(new JsonObject
            {
                ["model"] = new JsonObject { ["type"] = "string", ["description"] = "A model name from list_models." },
                ["prompt"] = new JsonObject { ["type"] = "string", ["description"] = "One user message. Appended after messages." },
                ["messages"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "The conversation so far.",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["role"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("system", "user", "assistant") },
                            ["content"] = new JsonObject { ["type"] = "string" }
                        },
                        ["required"] = new JsonArray("role", "content")
                    }
                },
                ["system"] = new JsonObject { ["type"] = "string", ["description"] = "An instruction placed before the conversation." },
                ["temperature"] = new JsonObject { ["type"] = "number" },
                ["max_tokens"] = new JsonObject { ["type"] = "integer", ["description"] = "Most tokens to generate." }
            }, "model"),
            ReadOnly: true),
        new("embed", "Embed text", P.Inference,
            "Turn text into vectors with an embedding model on this InferHub (list_models with capability=embed).",
            Schema(new JsonObject
            {
                ["model"] = new JsonObject { ["type"] = "string" },
                ["input"] = new JsonObject
                {
                    ["description"] = "A string, or an array of strings.",
                    ["anyOf"] = new JsonArray(
                        new JsonObject { ["type"] = "string" },
                        new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } })
                }
            }, "model", "input"),
            ReadOnly: true),
        new("fleet_status", "Fleet status", P.AdminRead,
            "The nodes connected to this InferHub: health, what each holds, work in flight, cordons and owners' pauses.",
            Schema(new JsonObject()),
            ReadOnly: true)
    ];

    public JsonArray List(McpCaller caller)
    {
        var tools = new JsonArray();
        foreach (var tool in All.Where(t => caller.Can(t.Permission)))
        {
            tools.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["title"] = tool.Title,
                ["description"] = tool.Description,
                ["inputSchema"] = tool.Schema.DeepClone(),
                ["annotations"] = new JsonObject
                {
                    ["title"] = tool.Title,
                    ["readOnlyHint"] = tool.ReadOnly,
                    ["destructiveHint"] = false,
                    ["openWorldHint"] = false
                }
            });
        }

        return tools;
    }

    /// <summary>Null when no such tool exists <i>for this caller</i>: a tool a role does not hold is as absent as one never written.</summary>
    public async Task<JsonObject?> CallAsync(HttpContext context, McpCaller caller, string name, JsonObject arguments, CancellationToken cancellationToken)
    {
        if (All.FirstOrDefault(t => t.Name == name) is not { } tool || !caller.Can(tool.Permission))
        {
            return null;
        }

        return name switch
        {
            "list_models" => ListModels(context, caller, McpEndpoints.Str(arguments["capability"])),
            "chat" => await ChatAsync(context, arguments, cancellationToken),
            "embed" => await EmbedAsync(context, arguments, cancellationToken),
            "fleet_status" => FleetStatus(context),
            _ => null
        };
    }

    private static JsonObject ListModels(HttpContext context, McpCaller caller, string? capability)
    {
        var services = context.RequestServices;
        var registry = services.GetRequiredService<INodeRegistry>();
        var providers = services.GetService<IProviderRegistry>();
        var models = ModelDiscovery.Merge(registry, providers, services.GetService<Cluster.ClusterPeers>());

        // What each model can do, from what the nodes declared (40 D2); a model only a provider serves is chat (65 D5).
        var kinds = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in registry.Snapshot(DateTimeOffset.UtcNow))
        {
            foreach (var declared in node.Capabilities ?? [])
            {
                foreach (var model in declared.Models)
                {
                    (kinds.TryGetValue(model, out var set) ? set : kinds[model] = new SortedSet<string>(StringComparer.Ordinal)).Add(declared.Kind);
                }
            }
        }

        var allowed = caller.Client.Limits?.AllowedModels is { Count: > 0 } list ? list : null;
        var rows = new JsonArray();
        var text = new StringBuilder();

        foreach (var model in models)
        {
            // An allowlist hides what is not on it, as the 404 does (25 D-note): not told what exists but is not for them.
            if (allowed is not null && !allowed.Any(a => string.Equals(a?.Trim(), model.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var source = ModelDiscovery.IsProviderOnly(registry, providers, model.Name)
                ? "provider"
                : registry.FindNodesWithModel(model.Name, includeUnserviceable: true).Count > 0 ? "node" : "cluster";
            var can = kinds.TryGetValue(model.Name, out var set) ? set.ToArray() : source == "node" ? [] : [CapabilityKinds.Chat];

            if (capability is not null && !can.Contains(capability, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var row = new JsonObject
            {
                ["name"] = model.Name,
                ["capabilities"] = new JsonArray(can.Select(c => (JsonNode)c).ToArray()),
                ["source"] = source
            };
            if (model.SizeBytes is { } size)
            {
                row["size_bytes"] = size;
            }

            rows.Add(row);
            text.Append("- ").Append(model.Name).Append(" (").Append(can.Length == 0 ? "?" : string.Join(", ", can)).Append("; ").Append(source).AppendLine(")");
        }

        var summary = rows.Count == 0
            ? "No models are available to you on this InferHub right now."
            : $"{rows.Count} model(s) available:\n{text}";
        return Ok(summary, new JsonObject { ["models"] = rows });
    }

    private async Task<JsonObject> ChatAsync(HttpContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        var model = McpEndpoints.Str(arguments["model"]);
        if (string.IsNullOrWhiteSpace(model))
        {
            return Fail("model is required — call list_models for the names");
        }

        var messages = new JsonArray();
        if (McpEndpoints.Str(arguments["system"]) is { Length: > 0 } system)
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = system });
        }

        if (arguments["messages"] is JsonArray given)
        {
            foreach (var item in given)
            {
                var role = McpEndpoints.Str(item?["role"]);
                var content = McpEndpoints.Str(item?["content"]);
                if (role is not ("system" or "user" or "assistant") || content is null)
                {
                    return Fail("each message needs role (system, user or assistant) and content (a string)");
                }

                messages.Add(new JsonObject { ["role"] = role, ["content"] = content });
            }
        }

        if (McpEndpoints.Str(arguments["prompt"]) is { Length: > 0 } prompt)
        {
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = prompt });
        }

        if (!messages.Any(m => McpEndpoints.Str(m?["role"]) == "user"))
        {
            return Fail("give a prompt, or messages with at least one user message");
        }

        var options = new JsonObject();
        if (arguments["temperature"] is JsonValue temperature && temperature.TryGetValue<double>(out var t))
        {
            options["temperature"] = t;
        }

        if (arguments["max_tokens"] is JsonValue maxTokens && maxTokens.TryGetValue<int>(out var n) && n > 0)
        {
            options["num_predict"] = n;
        }

        var body = new JsonObject { ["model"] = model, ["messages"] = messages, ["stream"] = false };
        if (options.Count > 0)
        {
            body["options"] = options;
        }

        var rawJson = body.ToJsonString();
        var request = JsonSerializer.Deserialize<ChatRequest>(rawJson, WebJson)!;
        var services = context.RequestServices;

        var outcome = await InferenceCore.DispatchAsync(
            "chat",
            rawJson,
            model,
            stream: false,
            InferenceEndpoints.DeriveConversationKey(request.Messages),
            InferenceCore.ClientContext.From(context),
            services.GetRequiredService<Services.IRouter>(),
            services.GetRequiredService<INodeRegistry>(),
            services.GetRequiredService<IDispatcher>(),
            services.GetRequiredService<IProviderDispatcher>(),
            services.GetRequiredService<Metrics>(),
            logger,
            cancellationToken,
            ProviderSteer.From(context.Request));

        if (outcome.IsError)
        {
            return Fail($"{outcome.ErrorStatus}: {outcome.ErrorMessage}"
                + (outcome.RetryAfterSeconds is { } retry ? $" (try again in {retry} s)" : ""));
        }

        context.Response.Headers[InferenceCore.ServedByHeader] = outcome.ServedBy;

        string? responseJson = outcome.ResponseJson;
        var answer = new StringBuilder();
        var thinking = new StringBuilder();
        JsonNode? last = null;

        try
        {
            if (outcome.Stream is { } stream)
            {
                await foreach (var chunk in stream.ReadAllAsync(cancellationToken))
                {
                    last = JsonNode.Parse(chunk.ResponseJson);
                    answer.Append(McpEndpoints.Str(last?["message"]?["content"]));
                    thinking.Append(McpEndpoints.Str(last?["message"]?["thinking"]));
                    if (McpEndpoints.Str(last?["error"]) is { } error)
                    {
                        return Fail(InferenceCore.ReadableNodeError(error));
                    }

                    if (chunk.Done)
                    {
                        break;
                    }
                }
            }
            else
            {
                last = JsonNode.Parse(responseJson ?? "{}");
                answer.Append(McpEndpoints.Str(last?["message"]?["content"]));
                thinking.Append(McpEndpoints.Str(last?["message"]?["thinking"]));
                if (McpEndpoints.Str(last?["error"]) is { } error)
                {
                    return Fail(InferenceCore.ReadableNodeError(error));
                }
            }
        }
        catch (Exception ex) when (ex is NodeDisconnectedException or TimeoutException)
        {
            return Fail(ex.Message);
        }

        var structured = new JsonObject
        {
            ["model"] = model,
            ["content"] = answer.ToString(),
            ["served_by"] = outcome.ServedBy
        };
        if (thinking.Length > 0)
        {
            structured["thinking"] = thinking.ToString();
        }

        if (last?["prompt_eval_count"] is JsonValue promptTokens)
        {
            structured["prompt_tokens"] = promptTokens.DeepClone();
        }

        if (last?["eval_count"] is JsonValue completionTokens)
        {
            structured["completion_tokens"] = completionTokens.DeepClone();
        }

        return Ok(answer.ToString(), structured);
    }

    private static async Task<JsonObject> EmbedAsync(HttpContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        var model = McpEndpoints.Str(arguments["model"]);
        var input = arguments["input"];
        if (string.IsNullOrWhiteSpace(model) || input is null || (input is JsonArray { Count: 0 }))
        {
            return Fail("model and input are required");
        }

        // The same admission /api/chat takes: an allowlist hides the model, a budget or a rate refuses. (The embed routes on
        // /api and /v1 take none — named in the release notes, not changed by this phase.)
        var client = InferenceCore.ClientContext.From(context);
        var admission = client.Admission.TryAdmit(client.Client, model);
        if (!admission.Allowed)
        {
            return Fail($"{admission.Status}: {admission.Message}");
        }

        using var lease = admission.Lease;
        var body = new JsonObject { ["model"] = model, ["input"] = input.DeepClone() }.ToJsonString();

        try
        {
            var responseJson = await context.RequestServices.GetRequiredService<IEmbeddingDispatcher>()
                .DispatchEmbedAsync(body, modelOverride: null, cancellationToken);
            var response = JsonNode.Parse(responseJson) as JsonObject ?? new JsonObject();
            var vectors = response["embeddings"] as JsonArray ?? new JsonArray();
            var dimensions = vectors.FirstOrDefault() is JsonArray first ? first.Count : 0;

            var structured = new JsonObject
            {
                ["model"] = model,
                ["dimensions"] = dimensions,
                ["embeddings"] = vectors.DeepClone()
            };
            return Ok(structured.ToJsonString(), structured);
        }
        catch (NoEmbeddingNodeException ex)
        {
            return Fail("404: " + ex.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NodeDisconnectedException or TimeoutException)
        {
            return Fail(ex.Message);
        }
    }

    private static JsonObject FleetStatus(HttpContext context)
    {
        var nodes = context.RequestServices.GetRequiredService<INodeRegistry>().Snapshot(DateTimeOffset.UtcNow)
            .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var rows = new JsonArray();
        var text = new StringBuilder();
        foreach (var node in nodes)
        {
            var healthy = node.BackendHealth is null or BackendHealth.Healthy;
            var paused = node.Sharing is { Paused: true };
            rows.Add(new JsonObject
            {
                ["name"] = node.Name,
                ["node_id"] = node.NodeId,
                ["version"] = node.Version,
                ["healthy"] = healthy,
                ["cordoned"] = node.Cordoned,
                ["sharing_paused"] = paused,
                ["models"] = node.ModelCount,
                ["in_flight"] = node.InFlight,
                ["organisation"] = node.Organisation,
                ["last_seen_seconds"] = Math.Round(node.AgeSeconds, 1)
            });
            text.Append("- ").Append(node.Name).Append(" v").Append(node.Version)
                .Append(healthy ? ", healthy" : ", UNHEALTHY")
                .Append(node.Cordoned ? ", cordoned" : "")
                .Append(paused ? ", paused by its owner" : "")
                .Append(", ").Append(node.ModelCount).Append(" models, ").Append(node.InFlight).AppendLine(" in flight");
        }

        var summary = nodes.Length == 0 ? "No nodes are connected." : $"{nodes.Length} node(s):\n{text}";
        return Ok(summary, new JsonObject { ["nodes"] = rows });
    }

    private static JsonObject Schema(JsonObject properties, params string[] required)
    {
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Length > 0)
        {
            schema["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray());
        }

        return schema;
    }

    private static JsonObject Ok(string text, JsonObject structured) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["structuredContent"] = structured
    };

    /// <summary>A tool that ran and failed: in the result, where the model can read it (the spec's tool-error shape).</summary>
    private static JsonObject Fail(string message) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = message }),
        ["isError"] = true
    };
}
