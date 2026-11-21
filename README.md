# tryAGI.Chimera

A C# / .NET 10 SDK for [CHIMERA](https://github.com/jamesondh/chimera-vocal-synth), the MIT-licensed **physical-modeling voice and singing** engine in Rust. This is a CPU-based synthesizer: no cloud account, model download, or GPU is required.

The package offers **two distinct APIs**:

- `ChimeraStreamingClient` runs the native C ABI **in process**, emitting PCM16 mono / 48 kHz blocks immediately as rendering progresses. It never creates a WAV or spawns a process.
- `ChimeraClient` preserves the original offline subprocess API for WAV output through the upstream `chimera` CLI.

## Native PCM streaming

```csharp
using Chimera;

var synth = new ChimeraStreamingClient(new ChimeraStreamingOptions
{
    FramesPerChunk = 1024, // About 21 ms of audio per block at 48 kHz
    EnglishDictionaryPath = "/opt/chimera/cmudict.txt.gz",
});

await foreach (var chunk in synth.StreamTextAsync("Hello from CHIMERA."))
{
    // Write chunk.Pcm16 to the audio transport without collecting a WAV.
    await audioOutput.WriteAsync(chunk.Pcm16);
    Console.WriteLine($"Generated block at {chunk.ElapsedSinceRequest.TotalMilliseconds:F1} ms");
}

await foreach (var chunk in synth.StreamIpaAsync("ˈɑpɑ", ChimeraVoice.Female))
{
    await audioOutput.WriteAsync(chunk.Pcm16);
}

var notes = """
{
    "bpm": 120,
    "syllables": [{"text":"a","onset":[],"nucleus":{"ipa":"ɑ"},"coda":[]}],
    "notes": [[60, 1]]
}
""";
await foreach (var chunk in synth.StreamNotesAsync(notes))
{
    await audioOutput.WriteAsync(chunk.Pcm16);
}
```

The first emitted block's `ElapsedSinceRequest` is the measured **time to first generated PCM block** (not first audible sample or time to playback on the device). Each enumeration allocates its own native engine and frees it on completion or cancellation. The upstream engine's C ABI layout is verified at runtime; the SDK rejects incompatible layouts.

**English dictionary:** `StreamTextAsync` requires a local CMU dictionary (text or gzip). The package does not redistribute dictionary data. IPA rendering and singing from explicit `syllables` work without this dictionary. The English text frontend is not suitable for arbitrary Russian prose; use IPA or a language-appropriate phonemizer.

## Native binaries and NuGet

The CI pipeline compiles upstream Rust revision `7c0f722e6a375f0acae543364565741d58c100c9` into platform-native `chimera_wasm` C ABI libraries and assembles a single multi-RID `tryAGI.Chimera` NuGet artifact:

| Runtime | Native library |
|---|---|
| linux-x64, linux-arm64 | `libchimera_wasm.so` |
| osx-arm64, osx-x64 | `libchimera_wasm.dylib` |
| win-x64 | `chimera_wasm.dll` |

These are placed under the standard `runtimes/<RID>/native/` NuGet asset layout, so normal .NET runtime resolution finds the correct binary. For development with a locally built Rust library, set `CHIMERA_NATIVE_PATH` to its absolute path. The upstream `chimera-wasm` crate exposes a C ABI on native targets too; despite its name, it does not require WebAssembly at runtime.

Build the native library independently:

```sh
git clone https://github.com/jamesondh/chimera-vocal-synth.git
cargo build --locked --release --manifest-path chimera-vocal-synth/Cargo.toml -p chimera-wasm
```

For the release NuGet pack, CI passes `ChimeraRequireNative=true` and verifies that all five native assets exist. An unqualified local `dotnet pack` only produces a managed development package; use the CI artifact for all-platform native distribution. See [third-party notices](THIRD_PARTY_NOTICES.md).

## Offline WAV CLI compatibility

```csharp
var cli = new ChimeraClient(new ChimeraClientOptions
{
    ExecutablePath = "/opt/chimera/target/release/chimera",
    Timeout = TimeSpan.FromSeconds(30),
});
ChimeraAudio wave = await cli.SynthesizeTextAsync("Hello world.");
await File.WriteAllBytesAsync("speech.wav", wave.WaveBytes);
```

Build CLI with `cargo build --locked --release --manifest-path chimera-vocal-synth/Cargo.toml -p chimera-cli`. The CLI needs upstream's original pronunciation dictionary at its source path; moving only the executable is insufficient.

## Validation

```sh
dotnet test Chimera.slnx -c Release --filter "TestCategory!=Native&TestCategory!=NativeStreaming"
dotnet pack src/Chimera/Chimera.csproj -c Release
```

Native smoke tests (including spoken PCM, IPA, sung syllables and cancellation) run on Linux x64/arm64, macOS arm64/x64 and Windows x64 in GitHub Actions. Set `CHIMERA_NATIVE_PATH` and `CHIMERA_DICTIONARY_PATH` to run them locally. CLI smoke tests remain separate.

A real-time server should monitor synthesis request-to-first-PCM latency independently of the device's playback-start latency, and keep CHIMERA opt-in until voice/language compatibility is evaluated. [tryAGI](https://github.com/tryAGI) maintains the C# integration; the original CHIMERA engine belongs to its respective copyright holders.
