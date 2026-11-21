namespace Chimera;

/// <summary>Configuration of the external CHIMERA CLI process.</summary>
public sealed class ChimeraClientOptions
{
    /// <summary>Path to the locally built <c>chimera</c> executable, or its PATH entry.</summary>
    public string ExecutablePath { get; init; } = "chimera";

    /// <summary>Optional working directory for the subprocess.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Maximum time per command, including the full audio render.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);
}
