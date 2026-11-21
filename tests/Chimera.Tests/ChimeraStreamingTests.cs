using Chimera;
using NUnit.Framework;

namespace Chimera.Tests;

[TestFixture]
[Category("NativeStreaming")]
public sealed class ChimeraStreamingTests
{
    private static ChimeraStreamingClient NewClient()
    {
        var dictionary = Environment.GetEnvironmentVariable("CHIMERA_DICTIONARY_PATH");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CHIMERA_NATIVE_PATH")))
        {
            Assert.Ignore("Set CHIMERA_NATIVE_PATH to the native C ABI shared library.");
        }

        return new ChimeraStreamingClient(new ChimeraStreamingOptions
        {
            FramesPerChunk = 1024,
            EnglishDictionaryPath = dictionary,
        });
    }

    [Test]
    public async Task IpaStreamsPcmWithAudibleSamplesAndFirstBlockLatency()
    {
        var client = NewClient();
        var chunks = await CollectAsync(client.StreamIpaAsync("ˈɑpɑ", ChimeraVoice.Female));
        Assert.That(chunks.Count, Is.GreaterThan(2));
        Assert.That(chunks[0].FirstFrame, Is.Zero);
        Assert.That(chunks[0].Pcm16.Length, Is.EqualTo(2048));
        Assert.That(chunks[0].SampleRate, Is.EqualTo(48_000));
        Assert.That(chunks[0].ElapsedSinceRequest, Is.GreaterThan(TimeSpan.Zero));
        Assert.That(chunks.Any(c => c.Pcm16.Any(b => b != 0)), Is.True,
            "The stream should contain synthesized audio rather than only silence.");
        Assert.That(chunks.Select(c => c.FirstFrame), Is.Ordered);
    }

    [Test]
    public async Task EnglishTextStreamsUsingUpstreamDictionary()
    {
        var client = NewClient();
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CHIMERA_DICTIONARY_PATH")))
        {
            Assert.Ignore("Set CHIMERA_DICTIONARY_PATH to the upstream CMU gzip dictionary.");
        }

        var chunks = await CollectAsync(client.StreamTextAsync("Hello from Chimera."));
        Assert.That(chunks.Count, Is.GreaterThan(2));
        Assert.That(chunks.Any(c => c.Pcm16.Any(b => b != 0)), Is.True);
    }

    [Test]
    public async Task NotesStreamVocalPcm()
    {
        var client = NewClient();
        var chunks = await CollectAsync(client.StreamNotesAsync("""
            {"bpm":120,"syllables":[{"text":"a","onset":[],"nucleus":{"ipa":"ɑ"},"coda":[]}],"notes":[[60,1]]}
            """));
        Assert.That(chunks.Count, Is.GreaterThan(2));
        Assert.That(chunks.Any(c => c.Pcm16.Any(b => b != 0)), Is.True);
    }

    [Test]
    public async Task CancellationStopsAStreamWithoutLeakingItsNativeHandle()
    {
        var client = NewClient();
        using var cancellation = new CancellationTokenSource();
        await using var iterator = client.StreamIpaAsync("ˈɑpɑ", cancellationToken: cancellation.Token)
            .GetAsyncEnumerator();
        Assert.That(await iterator.MoveNextAsync(), Is.True);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await iterator.MoveNextAsync());
    }

    private static async Task<List<ChimeraPcmChunk>> CollectAsync(IAsyncEnumerable<ChimeraPcmChunk> stream)
    {
        var chunks = new List<ChimeraPcmChunk>();
        await foreach (var chunk in stream)
        {
            chunks.Add(chunk);
        }
        return chunks;
    }
}
