using System.Buffers.Binary;
using Chimera;
using NUnit.Framework;

namespace Chimera.Tests;

[TestFixture]
public sealed class ChimeraClientTests
{
    [Test]
    public async Task TextUsesLiteralArgumentsAndReturnsWave()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("The fake CLI uses a POSIX shell; native integration is cross-platform.");
        }

        using var fake = new FakeCli();
        var client = new ChimeraClient(new ChimeraClientOptions { ExecutablePath = fake.Executable });
        var input = "Hello \"world\"; $(touch /tmp/should-never-exist)";
        var audio = await client.SynthesizeTextAsync(input, ChimeraVoice.Female, ChimeraSampleFormat.Float32);

        Assert.That(audio.WaveBytes.AsSpan(0, 4).SequenceEqual("RIFF"u8), Is.True);
        Assert.That(audio.SampleRate, Is.EqualTo(48000));
        Assert.That(audio.StandardOutput, Does.Contain("arg:" + input));
        Assert.That(audio.StandardOutput, Does.Contain("arg:female"));
        Assert.That(audio.StandardOutput, Does.Contain("arg:--float"));
    }

    [Test]
    public async Task IpaUsesSpeechCommand()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX fake CLI.");

        using var fake = new FakeCli();
        var client = new ChimeraClient(new ChimeraClientOptions { ExecutablePath = fake.Executable });
        var audio = await client.SynthesizeIpaAsync("ˈɑpɑ", ChimeraVoice.Child);
        Assert.That(audio.StandardOutput, Does.Contain("arg:speech"));
        Assert.That(audio.StandardOutput, Does.Contain("arg:child"));
    }

    [Test]
    public async Task NotesJsonIsWrittenToASeparateInputFile()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX fake CLI.");

        using var fake = new FakeCli();
        var client = new ChimeraClient(new ChimeraClientOptions { ExecutablePath = fake.Executable });
        const string json = """{"bpm":120,"lyrics":"hello","notes":[[60,1]]}""";
        var audio = await client.SingNotesAsync(json);
        Assert.That(audio.StandardOutput, Does.Contain("arg:notes"));
        Assert.That(audio.StandardOutput, Does.Contain("json:" + json));
    }

    [Test]
    public async Task SyllablesAreReturnedFromStandardOutput()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX fake CLI.");

        using var fake = new FakeCli();
        var client = new ChimeraClient(new ChimeraClientOptions { ExecutablePath = fake.Executable });
        Assert.That(await client.GetSyllablesJsonAsync("hello"), Is.EqualTo("""[{"text":"hello"}]"""));
    }

    [Test]
    public async Task InputAndVoiceAreValidatedBeforeStartingAProcess()
    {
        var client = new ChimeraClient(new ChimeraClientOptions { ExecutablePath = "not-installed-chimera" });
        await Assert.ThrowsAsync<ArgumentException>(async () => await client.SynthesizeTextAsync(" "));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await client.SynthesizeIpaAsync("a", (ChimeraVoice)99));
    }

    [Test]
    public async Task ExitCodesAndStderrArePreserved()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX fake CLI.");

        using var fake = new FakeCli();
        var client = new ChimeraClient(new ChimeraClientOptions { ExecutablePath = fake.Executable });
        var error = await Assert.ThrowsAsync<ChimeraException>(async () => await client.SynthesizeTextAsync("FAIL"));
        Assert.That(error!.ExitCode, Is.EqualTo(7));
        Assert.That(error.StandardError, Does.Contain("fake engine failed"));
    }

    [Test]
    public async Task TimeoutTerminatesAChildProcess()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX fake CLI.");

        using var fake = new FakeCli();
        var client = new ChimeraClient(new ChimeraClientOptions
        {
            ExecutablePath = fake.Executable,
            Timeout = TimeSpan.FromMilliseconds(100),
        });
        await Assert.ThrowsAsync<TimeoutException>(async () => await client.SynthesizeTextAsync("SLEEP"));
    }

    [Test]
    [Category("Native")]
    public async Task UpstreamRustCliProducesRealWav()
    {
        var executable = Environment.GetEnvironmentVariable("CHIMERA_CLI_PATH");
        if (string.IsNullOrWhiteSpace(executable))
        {
            Assert.Ignore("Set CHIMERA_CLI_PATH to run against the real upstream CLI.");
        }

        var client = new ChimeraClient(new ChimeraClientOptions
        {
            ExecutablePath = Path.GetFullPath(executable),
            Timeout = TimeSpan.FromMinutes(3),
        });
        var speech = await client.SynthesizeTextAsync("Hello from CHIMERA.");
        Assert.That(speech.WaveBytes.Length, Is.GreaterThan(4400));
        Assert.That(BinaryPrimitives.ReadInt32LittleEndian(speech.WaveBytes.AsSpan(24, 4)), Is.EqualTo(48000));

        var ipa = await client.SynthesizeIpaAsync("ˈɑpɑ", ChimeraVoice.Female, ChimeraSampleFormat.Float32);
        Assert.That(ipa.WaveBytes.Length, Is.GreaterThan(4400));
        Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(ipa.WaveBytes.AsSpan(20, 2)), Is.EqualTo(3));

        var singing = await client.SingNotesAsync("""
            {"bpm":120,"lyrics":"a","notes":[[60,1]]}
            """);
        Assert.That(singing.WaveBytes.Length, Is.GreaterThan(4400));
    }

    private sealed class FakeCli : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "tryagi-chimera-test-" + Guid.NewGuid().ToString("N"));

        public FakeCli()
        {
            if (OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("POSIX fake CLI requires Unix.");
            }

            Directory.CreateDirectory(_directory);
            Executable = Path.Combine(_directory, "chimera");
            File.WriteAllText(Executable, """
#!/bin/sh
if [ "$1" = "syllables" ]; then
  printf '[{"text":"hello"}]\n'
  exit 0
fi
printf 'arg:%s\n' "$@"
if [ "$2" = "notes" ]; then
  printf 'json:'
  cat "$3"
  printf '\n'
  output="$4"
else
  output="$3"
fi
if [ "$4" = "FAIL" ]; then
  printf 'fake engine failed\n' >&2
  exit 7
fi
if [ "$4" = "SLEEP" ]; then
  sleep 3
fi
printf 'RIFF\044\000\000\000WAVEfmt \020\000\000\000\001\000\001\000\200\273\000\000\000\167\001\000\002\000\020\000data\000\000\000\000' > "$output"
""");
            File.SetUnixFileMode(Executable,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public string Executable { get; }

        public void Dispose()
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
