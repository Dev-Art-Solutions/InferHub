namespace InferHub.Shared.Vector;

/// <summary>
/// The logging seam the shared retrieval stack writes through (phase-38 D3).
/// </summary>
/// <remarks>
/// <para>
/// <c>InferHub.Shared</c> is a plain class library (rule 2) and takes no packages (rule 5), so it
/// cannot see <c>ILogger</c> — that is a NuGet dependency for a library outside the ASP.NET shared
/// framework. Rather than end an eight-phase "zero new dependencies" streak for a convenience, the
/// two hosts adapt their own logger to this in a few lines.
/// </para>
/// <para>
/// The arguments are message <em>templates</em> and their values, deliberately, not formatted
/// strings: the coordinator's log output has to be byte-identical to what it emitted before this
/// code moved projects, structured fields included.
/// </para>
/// </remarks>
public interface IVectorLog
{
    void Info(string message, params object?[] args);

    void Warn(Exception? error, string message, params object?[] args);

    void Error(Exception? error, string message, params object?[] args);

    void Debug(string message, params object?[] args);
}

/// <summary>Says nothing. The default wherever a host has not supplied one.</summary>
public sealed class NullVectorLog : IVectorLog
{
    public static readonly NullVectorLog Instance = new();

    public void Info(string message, params object?[] args) { }

    public void Warn(Exception? error, string message, params object?[] args) { }

    public void Error(Exception? error, string message, params object?[] args) { }

    public void Debug(string message, params object?[] args) { }
}
