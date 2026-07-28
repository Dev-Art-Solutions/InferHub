using Microsoft.Extensions.Logging;

namespace InferHub.Coordinator.Vector;

/// <summary>
/// Adapts the coordinator's <see cref="ILogger"/> to the shared retrieval stack's
/// <see cref="IVectorLog"/> seam (phase-38 D3).
/// </summary>
/// <remarks>
/// The templates and their arguments are passed through untouched, so the hub's log output — the
/// structured fields included — is exactly what it was before this code moved projects. That is
/// the point of the seam taking a template rather than a formatted string.
/// </remarks>
public sealed class VectorLog<T>(ILogger<T> logger) : IVectorLog
{
    public void Info(string message, params object?[] args) => logger.LogInformation(message, args);

    public void Warn(Exception? error, string message, params object?[] args) => logger.LogWarning(error, message, args);

    public void Error(Exception? error, string message, params object?[] args) => logger.LogError(error, message, args);

    public void Debug(string message, params object?[] args) => logger.LogDebug(message, args);
}
