using System.Text.Json.Serialization;

namespace InferHub.Shared.Contracts;

/// <summary>
/// Phase 153: what a llama.cpp engine with <c>Serve:Context</c> says about its context tiers — the slot, llama.cpp's
/// prompt cache in RAM, the slot files on disk — and how requests were served from them. Counts, token numbers, bytes
/// and times only: never a prompt, a hash of one or a file name a person chose (rule 7).
/// </summary>
public sealed record ContextTierState(
    [property: JsonPropertyName("directory")] string Directory,
    [property: JsonPropertyName("ramBudgetBytes")] long RamBudgetBytes,
    [property: JsonPropertyName("ramHeldBytes")] long RamHeldBytes,
    [property: JsonPropertyName("diskBudgetBytes")] long DiskBudgetBytes,
    [property: JsonPropertyName("diskHeldBytes")] long DiskHeldBytes,
    [property: JsonPropertyName("slotTokens")] long SlotTokens,
    [property: JsonPropertyName("counts")] ContextTierCounts Counts,
    [property: JsonPropertyName("entries")] IReadOnlyList<ContextTierEntryInfo> Entries,
    [property: JsonPropertyName("lastError")] string? LastError = null);

/// <summary>How each request found its context, and what the disk tier did.</summary>
public sealed record ContextTierCounts(
    /// <summary>Still in the slot: the conversation went on where it was.</summary>
    [property: JsonPropertyName("slot")] long Slot,
    /// <summary>Held by llama.cpp's prompt cache in RAM, by the node's reckoning of what that cache keeps.</summary>
    [property: JsonPropertyName("ram")] long Ram,
    /// <summary>Restored from a slot file on disk.</summary>
    [property: JsonPropertyName("disk")] long Disk,
    /// <summary>Found nowhere: computed from the start.</summary>
    [property: JsonPropertyName("miss")] long Miss,
    [property: JsonPropertyName("saved")] long Saved,
    [property: JsonPropertyName("evicted")] long Evicted,
    [property: JsonPropertyName("failed")] long Failed,
    [property: JsonPropertyName("lastRestoreMs")] long? LastRestoreMs = null,
    [property: JsonPropertyName("lastSaveMs")] long? LastSaveMs = null);

/// <summary>One slot file: a prefix of some conversation, by size and use.</summary>
public sealed record ContextTierEntryInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("tokens")] long Tokens,
    [property: JsonPropertyName("bytes")] long Bytes,
    [property: JsonPropertyName("uses")] long Uses,
    [property: JsonPropertyName("createdUtc")] DateTimeOffset CreatedUtc,
    [property: JsonPropertyName("lastUsedUtc")] DateTimeOffset LastUsedUtc,
    [property: JsonPropertyName("pinned")] bool Pinned);
