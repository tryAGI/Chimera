using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Chimera;

/// <summary>Options for the allocation-free native CHIMERA DSP engine.</summary>
public sealed class ChimeraStreamingOptions
{
    /// <summary>Samples per PCM16 chunk. Upstream engine supports at most 4096 per render call.</summary>
    public int FramesPerChunk { get; init; } = 1024;

    /// <summary>Maximum planned duration to prevent unbounded synthesis.</summary>
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Path to CMU dictionary text or gzip file for English text and lyrics synthesis.</summary>
    public string? EnglishDictionaryPath { get; init; }
}

/// <summary>A PCM16 little-endian mono block, copied from the native DSP engine.</summary>
public sealed record ChimeraPcmChunk(byte[] Pcm16, int SampleRate, long FirstFrame, TimeSpan ElapsedSinceRequest);

/// <summary>
/// In-process native speech/vocal streaming from CHIMERA's stable C ABI, with no WAV or subprocess.
/// This client is safe for concurrent requests; each stream owns a distinct native engine handle.
/// </summary>
public sealed class ChimeraStreamingClient
{
    private const int SampleRate = 48_000;
    private const int RecordSize = 272;
    private const int MaxGestures = 4000;
    private const int MaxRenderFrames = 4096;

    private static readonly string[] ControlNames =
    [
        "jaw", "tbX", "tbY", "ttX", "ttY", "ttCurl", "root", "lipAp",
        "lipProt", "lipWidth", "labiodental", "velum", "larynx", "epilarynx",
        "ventricular", "lateral", "dorsum", "abduction", "closure", "breath",
        "f0", "vibrato",
    ];

    // Upstream's JSON planner uses thread-local state. A dictionary must be loaded
    // on the thread executing g2pPhrases, not on a different asynchronous continuation.
    [ThreadStatic] private static string? s_loadedDictionaryPath;

    private readonly ChimeraStreamingOptions _options;
    private readonly Lazy<string>? _dictionary;

    public ChimeraStreamingClient(ChimeraStreamingOptions? options = null)
    {
        _options = options ?? new ChimeraStreamingOptions();
        if (_options.FramesPerChunk is < 1 or > MaxRenderFrames)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "FramesPerChunk must be 1..4096.");
        }
        if (_options.MaxDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxDuration must be positive.");
        }

        if (_options.EnglishDictionaryPath is { Length: > 0 } path)
        {
            // Resolve the path once to preserve dictionary identity across concurrent calls.
            DictionaryPath = Path.GetFullPath(path);
            _dictionary = new Lazy<string>(() => ReadDictionary(DictionaryPath));
        }
    }

    public string? DictionaryPath { get; }

    /// <summary>Stream phonetic IPA as PCM16 blocks, without waiting for the whole utterance.</summary>
    public IAsyncEnumerable<ChimeraPcmChunk> StreamIpaAsync(
        string ipa,
        ChimeraVoice voice = ChimeraVoice.Male,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ipa);
        return StreamAsync(() => PlanIpa(ipa, voice), voice, cancellationToken);
    }

    /// <summary>Stream English text, using the configured CMU pronunciation dictionary.</summary>
    public IAsyncEnumerable<ChimeraPcmChunk> StreamTextAsync(
        string text,
        ChimeraVoice voice = ChimeraVoice.Male,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        RequireDictionary();
        return StreamAsync(() => PlanText(text, voice), voice, cancellationToken);
    }

    /// <summary>Stream sung notes from the CHIMERA melody JSON format.</summary>
    public IAsyncEnumerable<ChimeraPcmChunk> StreamNotesAsync(
        string notesJson,
        ChimeraVoice voice = ChimeraVoice.Male,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(notesJson);
        return StreamAsync(() => PlanNotes(notesJson, voice), voice, cancellationToken);
    }

    private async IAsyncEnumerable<ChimeraPcmChunk> StreamAsync(
        Func<NativePlan> planFactory,
        ChimeraVoice voice,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var started = Stopwatch.GetTimestamp();
        EnsureAbiCompatible();
        var plan = planFactory();
        ValidatePlan(plan);

        var native = ChimeraNativeMethods.EngineNew(SampleRate);
        if (native == IntPtr.Zero)
        {
            throw new ChimeraException("CHIMERA native engine allocation failed.");
        }

        try
        {
            var body = GetBody(voice);
            ChimeraNativeMethods.EngineSetBody(native, body.Female, body.Child);
            var records = EncodeGestures(plan.Gestures);
            Marshal.Copy(records, 0, ChimeraNativeMethods.EngineRecords(native), records.Length);
            ChimeraNativeMethods.EngineSchedule(native, (nuint)plan.Gestures.Count, 0.0, 1);

            long totalFrames = (long)Math.Ceiling((plan.EndSeconds + 0.3) * SampleRate);
            var floatBuffer = new float[_options.FramesPerChunk];
            long offset = 0;
            while (offset < totalFrames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var frames = (int)Math.Min(_options.FramesPerChunk, totalFrames - offset);
                var pointer = ChimeraNativeMethods.EngineRender(native, (nuint)frames);
                if (pointer == IntPtr.Zero)
                {
                    throw new ChimeraException("CHIMERA native render returned a null buffer.");
                }

                Marshal.Copy(pointer, floatBuffer, 0, frames);
                var pcm16 = new byte[frames * 2];
                for (var i = 0; i < frames; i++)
                {
                    var sample = float.IsFinite(floatBuffer[i]) ? Math.Clamp(floatBuffer[i], -1f, 1f) : 0f;
                    var signed = (short)Math.Clamp(Math.Round(sample * 32767f, MidpointRounding.AwayFromZero), -32768d, 32767d);
                    BinaryPrimitives.WriteInt16LittleEndian(pcm16.AsSpan(i * 2, 2), signed);
                }

                // ElapsedSinceRequest on the first block is the measured time-to-first-PCM.
                yield return new ChimeraPcmChunk(pcm16, SampleRate, offset, Stopwatch.GetElapsedTime(started));
                offset += frames;
                await Task.CompletedTask.ConfigureAwait(false);
            }
        }
        finally
        {
            ChimeraNativeMethods.EngineFree(native);
        }
    }

    private NativePlan PlanIpa(string ipa, ChimeraVoice voice)
    {
        var segments = Call(new JsonObject { ["fn"] = "tokenizeIpa", ["text"] = ipa });
        return ParsePlan(Call(new JsonObject
        {
            ["fn"] = "planUtterance",
            ["segments"] = segments,
            ["options"] = PlanOptions(voice, 0.05),
        }));
    }

    private NativePlan PlanText(string text, ChimeraVoice voice)
    {
        EnsureDictionaryLoaded();
        var phrases = Call(new JsonObject { ["fn"] = "g2pPhrases", ["text"] = text });
        return ParsePlan(Call(new JsonObject
        {
            ["fn"] = "planPhrases",
            ["phrases"] = phrases,
            ["options"] = PlanOptions(voice, 0.05),
        }));
    }

    private NativePlan PlanNotes(string json, ChimeraVoice voice)
    {
        var input = JsonNode.Parse(json) as JsonObject
            ?? throw new ArgumentException("Notes must contain a JSON object.", nameof(json));
        var bpm = input["bpm"]?.GetValue<double>() ?? throw new ArgumentException("Notes require bpm.", nameof(json));
        if (!double.IsFinite(bpm) || bpm <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(json), "BPM must be finite and positive.");
        }

        var noteRows = input["notes"] as JsonArray
            ?? throw new ArgumentException("Notes require an array.", nameof(json));
        JsonArray syllables;
        if (input["syllables"] is JsonArray supplied)
        {
            syllables = (JsonArray)supplied.DeepClone();
        }
        else
        {
            EnsureDictionaryLoaded();
            syllables = Call(new JsonObject
            {
                ["fn"] = "lyricsToSyllables",
                ["text"] = input["lyrics"]?.GetValue<string>()
                    ?? throw new ArgumentException("Notes require lyrics or syllables.", nameof(json)),
                ["lang"] = input["lang"]?.GetValue<string>() ?? "en",
            }) as JsonArray ?? throw new ChimeraException("CHIMERA produced invalid sung syllables.");
        }

        int sounds = noteRows.Count(row => row is JsonArray pair && pair.Count == 2 && pair[0]!.GetValue<double>() > 0);
        if (sounds == 0 || sounds != syllables.Count)
        {
            throw new ArgumentException("Exactly one syllable is required per sounding note.", nameof(json));
        }

        var all = new JsonArray();
        double elapsedBeats = 0, startBeats = 0, end = 0;
        var phrase = new JsonArray();
        var syllableIndex = 0;

        void Flush()
        {
            if (phrase.Count == 0) return;
            var plan = ParsePlan(Call(new JsonObject
            {
                ["fn"] = "planSong",
                ["notes"] = phrase.DeepClone(),
                ["options"] = new JsonObject
                {
                    ["body"] = GetBody(voice).Name,
                    ["bpm"] = bpm,
                    ["t0"] = (input["t0"]?.GetValue<double>() ?? 0.3) + startBeats * 60 / bpm,
                    ["vibrato"] = input["vibrato"]?.GetValue<double>() ?? 35,
                },
            }));
            foreach (var gesture in plan.Gestures)
                all.Add(gesture!.DeepClone());
            end = Math.Max(end, plan.EndSeconds);
            phrase.Clear();
        }

        foreach (var row in noteRows)
        {
            if (row is not JsonArray pair || pair.Count != 2)
                throw new ArgumentException("Each note must be [midi, beats].", nameof(json));
            var midi = pair[0]!.GetValue<double>();
            var beats = pair[1]!.GetValue<double>();
            if (!double.IsFinite(midi) || !double.IsFinite(beats) || beats <= 0)
                throw new ArgumentException("Note pitch and duration must be finite; duration must be positive.", nameof(json));

            if (midi > 0)
            {
                if (phrase.Count == 0) startBeats = elapsedBeats;
                phrase.Add((JsonNode)new JsonObject
                {
                    ["midi"] = midi,
                    ["beats"] = beats,
                    ["syl"] = syllables[syllableIndex++]!.DeepClone(),
                });
            }
            else
            {
                Flush();
            }
            elapsedBeats += beats;
        }
        Flush();
        return new NativePlan(all, end);
    }

    private static JsonObject PlanOptions(ChimeraVoice voice, double t0) => new()
    {
        ["body"] = GetBody(voice).Name,
        ["t0"] = t0,
        ["f0"] = voice switch
        {
            ChimeraVoice.Female => 205,
            ChimeraVoice.Child => 250,
            _ => 112,
        },
    };

    private void ValidatePlan(NativePlan plan)
    {
        if (!double.IsFinite(plan.EndSeconds) || plan.EndSeconds <= 0 ||
            plan.EndSeconds + 0.3 > _options.MaxDuration.TotalSeconds)
        {
            throw new ChimeraException("CHIMERA plan is empty or exceeds the configured maximum duration.");
        }
        if (plan.Gestures.Count is < 1 or > MaxGestures)
        {
            throw new ChimeraException("CHIMERA plan gesture count is out of range.");
        }

    }

    private static void EnsureAbiCompatible()
    {
        try
        {
            if (ChimeraNativeMethods.LayoutVersion() != 1 ||
                ChimeraNativeMethods.GestureRecordSize() != RecordSize)
            {
                throw new ChimeraException("CHIMERA native library uses an unsupported gesture record layout.");
            }
        }
        catch (DllNotFoundException ex)
        {
            throw new ChimeraException("Missing native CHIMERA library. Install a NuGet package with matching RID native assets.", innerException: ex);
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new ChimeraException("Native CHIMERA library is incompatible with this C# package.", innerException: ex);
        }
    }

    private void RequireDictionary()
    {
        if (_dictionary is null)
            throw new InvalidOperationException("English text synthesis requires EnglishDictionaryPath (CMU dictionary .txt or .gz).");
    }

    private void EnsureDictionaryLoaded()
    {
        RequireDictionary();
        if (s_loadedDictionaryPath == DictionaryPath) return;
        Call(new JsonObject { ["fn"] = "g2pLoad", ["text"] = _dictionary!.Value });
        s_loadedDictionaryPath = DictionaryPath;
    }

    private static string ReadDictionary(string path)
    {
        using var file = File.OpenRead(path);
        using Stream input = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? new GZipStream(file, CompressionMode.Decompress, leaveOpen: false)
            : file;
        using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static unsafe JsonNode? Call(JsonObject request)
    {
        var input = Encoding.UTF8.GetBytes(request.ToJsonString());
        fixed (byte* bytes = input)
        {
            if (ChimeraNativeMethods.Call(bytes, (nuint)input.Length) != 0)
            {
                var error = GetNativeResult();
                throw new ChimeraException("CHIMERA native planner rejected request: " + error);
            }
        }
        return JsonNode.Parse(GetNativeResult());
    }

    private static string GetNativeResult()
    {
        var length = ChimeraNativeMethods.ResultLength();
        if (length > 64 * 1024 * 1024)
            throw new ChimeraException("CHIMERA native response exceeded 64 MiB.");
        var bytes = new byte[(int)length];
        Marshal.Copy(ChimeraNativeMethods.ResultPointer(), bytes, 0, bytes.Length);
        return Encoding.UTF8.GetString(bytes);
    }

    private static NativePlan ParsePlan(JsonNode? value)
    {
        if (value is not JsonObject plan || plan["gestures"] is not JsonArray gestures ||
            plan["end"] is null)
            throw new ChimeraException("CHIMERA native planner returned an invalid plan.");
        return new NativePlan(gestures, plan["end"]!.GetValue<double>());
    }

    private static byte[] EncodeGestures(JsonArray gestures)
    {
        var bytes = new byte[gestures.Count * RecordSize];
        for (var i = 0; i < gestures.Count; i++)
        {
            var gesture = gestures[i] as JsonObject
                ?? throw new ChimeraException("CHIMERA returned an invalid gesture.");
            var record = bytes.AsSpan(i * RecordSize, RecordSize);
            WriteDouble(record, 0, GetNumber(gesture, "t0"));
            WriteDouble(record, 8, GetNumber(gesture, "t1"));
            WriteDouble(record, 16, GetNumber(gesture, "rampIn"));
            WriteDouble(record, 24, GetNumber(gesture, "rampOut"));
            WriteDouble(record, 32, GetNumber(gesture, "strength"));
            WriteDouble(record, 40, GetNumber(gesture, "speed", double.NaN));
            WriteDouble(record, 48, GetNumber(gesture, "releaseHold", double.NaN));
            WriteDouble(record, 56, GetNumber(gesture, "tag", double.NaN));
            var targets = gesture["targets"] as JsonObject
                ?? throw new ChimeraException("CHIMERA returned gesture without targets.");
            for (var k = 0; k < ControlNames.Length; k++)
                WriteDouble(record, 64 + k * 8, GetNumber(targets, ControlNames[k], double.NaN));
            record[240] = 255; // No diagnostic label on the audio thread.
        }
        return bytes;
    }

    private static double GetNumber(JsonObject node, string name, double fallback = 0)
        => node[name]?.GetValue<double>() ?? fallback;

    private static void WriteDouble(Span<byte> record, int offset, double value)
        => BinaryPrimitives.WriteInt64LittleEndian(record.Slice(offset, sizeof(long)), BitConverter.DoubleToInt64Bits(value));

    private static (string Name, double Female, double Child) GetBody(ChimeraVoice voice) => voice switch
    {
        ChimeraVoice.Male => ("male", 0, 0),
        ChimeraVoice.Female => ("female", 1, 0),
        ChimeraVoice.Child => ("child", 0, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(voice)),
    };

    private sealed record NativePlan(JsonArray Gestures, double EndSeconds);
}
