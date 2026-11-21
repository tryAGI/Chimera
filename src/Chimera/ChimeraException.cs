namespace Chimera;

/// <summary>CHIMERA failed to launch, execute, or produce a valid WAV.</summary>
public sealed class ChimeraException : Exception
{
    public ChimeraException(string message, int? exitCode = null, string? standardError = null, Exception? innerException = null)
        : base(message, innerException)
    {
        ExitCode = exitCode;
        StandardError = standardError;
    }

    /// <summary>The subprocess exit code when available.</summary>
    public int? ExitCode { get; }

    /// <summary>Any stderr text emitted by the subprocess.</summary>
    public string? StandardError { get; }
}
