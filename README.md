# tryAGI.Chimera

A .NET 10 C# client for [CHIMERA](https://github.com/jamesondh/chimera-vocal-synth), an MIT-licensed physical-modeling speech and singing synthesizer written in Rust.

This is an **offline CLI adapter**. The NuGet package contains managed C# code only; it does **not** redistribute the Rust engine, models, dictionaries, or executable. CHIMERA is CPU-based and has no GPU or API-key requirement.

## Build CHIMERA

```sh
git clone https://github.com/jamesondh/chimera-vocal-synth.git
cargo build --release -p chimera-cli --manifest-path chimera-vocal-synth/Cargo.toml
```

**Important:** The upstream CLI loads `public/data/cmudict.txt.gz` using the Rust compile-time repository path. Keep the checkout and its data files available where the CLI was compiled. Moving only the executable may break English text synthesis.

## Use from C#

```csharp
using Chimera;

var client = new ChimeraClient(new ChimeraClientOptions
{
    ExecutablePath = "/path/to/chimera-vocal-synth/target/release/chimera",
    Timeout = TimeSpan.FromSeconds(30),
});

// English text -> PCM16 mono, 48-kHz WAV
ChimeraAudio speech = await client.SynthesizeTextAsync("Hello from CHIMERA.");
await File.WriteAllBytesAsync("speech.wav", speech.WaveBytes);

// IPA -> 32-bit floating-point WAV
ChimeraAudio ipa = await client.SynthesizeIpaAsync("ˈɑpɑ", ChimeraVoice.Female,
    ChimeraSampleFormat.Float32);
await File.WriteAllBytesAsync("ipa.wav", ipa.WaveBytes);

// Singing notes: one pitch/duration pair per note; this is the upstream JSON format.
const string notes = """
{
  "bpm": 110,
  "lyrics": "hello world",
  "notes": [[60, 1], [62, 1], [64, 2]]
}
""";
ChimeraAudio singing = await client.SingNotesAsync(notes);
await File.WriteAllBytesAsync("song.wav", singing.WaveBytes);
```

`SynthesizeTextAsync` uses the **English** text pathway; for other spoken languages use the IPA route when available. `SingNotesAsync` accepts upstream's `notes` JSON, including `syllables` produced by a language-specific front end (Russian singing is supported upstream). `GetSyllablesJsonAsync` currently uses the CLI's English syllable command.

All commands run via argument arrays (no shell), support cancellation, a process timeout, and separate captures of stdout/stderr. Temporary files are isolated for concurrent calls. Nonzero exit codes, missing files and malformed WAV headers throw `ChimeraException`.

## Build and tests

```sh
dotnet test Chimera.slnx -c Release --filter "TestCategory!=Native"
dotnet pack src/Chimera/Chimera.csproj -c Release
```

The GitHub Actions workflow also compiles a **pinned upstream Rust revision** and runs a real audio-rendering smoke test. To run it locally, set `CHIMERA_CLI_PATH` to the compiled executable and run the tests with `--filter TestCategory=Native`.

## Scope

- Supported: English text-to-WAV, IPA-to-WAV, song notes-to-WAV, syllable JSON; male/female/child presets; PCM16/float32; cancellation and diagnostics.
- Not yet supported: native in-process streaming, live articulation, streaming PCM chunks, automatic Rust binary deployment, and real-time resource/latency guarantees.
- Backend integration should use a future in-process native ABI for low-latency audio, while keeping the public `ChimeraClient` contract compatible.

The C# wrapper is maintained by [tryAGI](https://github.com/tryAGI). CHIMERA engine copyright and third-party data licenses remain with their respective authors.