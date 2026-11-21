namespace Chimera;

/// <summary>A body preset available in the CHIMERA CLI.</summary>
public enum ChimeraVoice
{
    Male,
    Female,
    Child,
}

/// <summary>The WAV sample representation.</summary>
public enum ChimeraSampleFormat
{
    Pcm16,
    Float32,
}

/// <summary>A complete offline WAV render from CHIMERA.</summary>
/// <param name="WaveBytes">Complete WAV file including its RIFF header.</param>
/// <param name="Format">The requested sample encoding.</param>
/// <param name="SampleRate">Samples per second (48,000 in the upstream CLI).</param>
/// <param name="StandardOutput">CLI diagnostic output.</param>
/// <param name="StandardError">CLI warnings, if any.</param>
public sealed record ChimeraAudio(
    byte[] WaveBytes,
    ChimeraSampleFormat Format,
    int SampleRate,
    string StandardOutput,
    string StandardError);
