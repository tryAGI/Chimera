using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;

namespace Chimera;

/// <summary>Runs the upstream CHIMERA synthesizer as a cancellable offline subprocess.</summary>
public sealed class ChimeraClient
{
    private readonly ChimeraClientOptions _options;

    public ChimeraClient(ChimeraClientOptions? options = null)
    {
        _options = options ?? new ChimeraClientOptions();
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.ExecutablePath);
        if (_options.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Timeout must be positive.");
        }
    }

    /// <summary>Speak English text and return a complete WAV file.</summary>
    public Task<ChimeraAudio> SynthesizeTextAsync(
        string text,
        ChimeraVoice voice = ChimeraVoice.Male,
        ChimeraSampleFormat format = ChimeraSampleFormat.Pcm16,
        CancellationToken cancellationToken = default)
        => RenderAsync("text", text, notesJson: false, voice, format, cancellationToken);

    /// <summary>Speak IPA phonemes and return a complete WAV file.</summary>
    public Task<ChimeraAudio> SynthesizeIpaAsync(
        string ipa,
        ChimeraVoice voice = ChimeraVoice.Male,
        ChimeraSampleFormat format = ChimeraSampleFormat.Pcm16,
        CancellationToken cancellationToken = default)
        => RenderAsync("speech", ipa, notesJson: false, voice, format, cancellationToken);

    /// <summary>Sing notes from the upstream melody JSON format.</summary>
    public Task<ChimeraAudio> SingNotesAsync(
        string notesJson,
        ChimeraVoice voice = ChimeraVoice.Male,
        ChimeraSampleFormat format = ChimeraSampleFormat.Pcm16,
        CancellationToken cancellationToken = default)
        => RenderAsync("notes", notesJson, notesJson: true, voice, format, cancellationToken);

    /// <summary>Return the CLI's JSON representation of English lyric syllables.</summary>
    public async Task<string> GetSyllablesJsonAsync(string lyrics, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lyrics);
        var result = await RunAsync(["syllables", lyrics], cancellationToken).ConfigureAwait(false);
        return result.Stdout.Trim();
    }

    private async Task<ChimeraAudio> RenderAsync(
        string command,
        string input,
        bool notesJson,
        ChimeraVoice voice,
        ChimeraSampleFormat format,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        var body = GetBody(voice);
        if (!Enum.IsDefined(format))
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var tempDirectory = Path.Combine(Path.GetTempPath(), "tryagi-chimera-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var outputPath = Path.Combine(tempDirectory, "output.wav");
            var inputArgument = input;
            if (notesJson)
            {
                inputArgument = Path.Combine(tempDirectory, "notes.json");
                await File.WriteAllTextAsync(inputArgument, input, cancellationToken).ConfigureAwait(false);
            }

            List<string> arguments = ["render", command];
            if (notesJson)
            {
                arguments.Add(inputArgument);
                arguments.Add(outputPath);
            }
            else
            {
                arguments.Add(outputPath);
                arguments.Add(inputArgument);
            }

            arguments.Add(body);
            if (format == ChimeraSampleFormat.Float32)
            {
                arguments.Add("--float");
            }

            var result = await RunAsync(arguments, cancellationToken).ConfigureAwait(false);
            if (!File.Exists(outputPath))
            {
                throw new ChimeraException("CHIMERA exited without producing the expected WAV file.", standardError: result.Stderr);
            }

            var wave = await File.ReadAllBytesAsync(outputPath, cancellationToken).ConfigureAwait(false);
            if (wave.Length < 44 ||
                !wave.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
                !wave.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            {
                throw new ChimeraException("CHIMERA produced an invalid WAV header.", standardError: result.Stderr);
            }

            var sampleRate = BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(24, 4));
            if (sampleRate <= 0)
            {
                throw new ChimeraException("CHIMERA produced an invalid WAV sample rate.", standardError: result.Stderr);
            }

            return new ChimeraAudio(wave, format, sampleRate, result.Stdout, result.Stderr);
        }
        finally
        {
            // Cleanup is best-effort and must not hide a synthesis error.
            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private async Task<(string Stdout, string Stderr)> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(_options.ExecutablePath)
        {
            WorkingDirectory = _options.WorkingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            if (!process.Start())
            {
                throw new ChimeraException("CHIMERA could not be started.");
            }
        }
        catch (Win32Exception error)
        {
            throw new ChimeraException(
                "CHIMERA executable not found or could not be started: " + _options.ExecutablePath,
                innerException: error);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException) { }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("CHIMERA exceeded the configured timeout of " + _options.Timeout + ".");
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new ChimeraException(
                "CHIMERA exited with code " + process.ExitCode + ": " + stderr.Trim(),
                process.ExitCode,
                stderr);
        }

        return (stdout, stderr);
    }

    private static string GetBody(ChimeraVoice voice) => voice switch
    {
        ChimeraVoice.Male => "male",
        ChimeraVoice.Female => "female",
        ChimeraVoice.Child => "child",
        _ => throw new ArgumentOutOfRangeException(nameof(voice)),
    };
}
