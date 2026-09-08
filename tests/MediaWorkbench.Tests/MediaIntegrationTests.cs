using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MediaWorkbench.Core;

namespace MediaWorkbench.Tests;

[Trait("Category", "Integration")]
public sealed class MediaIntegrationTests(MediaFixture fixture) : IClassFixture<MediaFixture>
{
    [Fact]
    public async Task ProbeAndIndexUseDecodedPresentationOrder()
    {
        var info = await fixture.Engine.ProbeAsync(fixture.VideoPath);
        var frames = await fixture.Engine.IndexFramesAsync(fixture.VideoPath, info);
        Assert.True(info.HasVideo);
        Assert.Single(info.AudioTracks);
        Assert.Equal(2, info.AudioTracks[0].Channels);
        Assert.Equal(12, frames.Count);
        Assert.InRange(info.Duration, 1.99, 2.05);
        Assert.Equal(0, frames[0].Time);
        Assert.True(frames.Zip(frames.Skip(1)).All(pair => pair.First.Time < pair.Second.Time));
    }

    [Fact]
    public async Task ForwardAndBackwardFramesMatchIndependentRgbDecode()
    {
        using var output = new TemporaryDirectory();
        var originalHash = SHA256.HashData(await File.ReadAllBytesAsync(fixture.VideoPath));
        foreach (var index in new[] { 0, 1, 6, 5, 11 })
        {
            var path = await fixture.Engine.ExportFrameAsync(fixture.Asset(fixture.VideoPath), index, output.Path);
            var expected = await RgbHashAsync(fixture.VideoPath, index);
            var actual = await RgbHashAsync(path, 0);
            Assert.Equal(expected, actual);
        }
        Assert.Equal(originalHash, SHA256.HashData(await File.ReadAllBytesAsync(fixture.VideoPath)));
    }

    [Fact]
    public async Task FrameExportUsesIdenticalPreviewBytesAndUniqueNames()
    {
        using var output = new TemporaryDirectory();
        var asset = fixture.Asset(fixture.VideoPath);
        var preview = await fixture.Engine.GetFrameAsync(asset, 3);
        var first = await MediaEngine.SaveFrameAsync(asset, 3, preview, output.Path);
        var second = await MediaEngine.SaveFrameAsync(asset, 3, preview, output.Path);
        Assert.NotEqual(first, second);
        Assert.Equal(preview, await File.ReadAllBytesAsync(first));
        Assert.Equal(preview, await File.ReadAllBytesAsync(second));
    }

    [Theory]
    [InlineData(0, 11)]
    [InlineData(3, 6)]
    [InlineData(11, 11)]
    public async Task FrameSequenceContainsEverySelectedFrameExactlyOnce(int start, int end)
    {
        using var output = new TemporaryDirectory();
        var directory = await fixture.Engine.ExportFramesAsync(fixture.Asset(fixture.VideoPath), new FrameRange(start, end), 12, output.Path);
        var paths = Directory.GetFiles(directory, "frame_*.png").Order().ToArray();
        Assert.Equal(end - start + 1, paths.Length);
        for (var index = 0; index < paths.Length; index++)
            Assert.Equal(await RgbHashAsync(fixture.VideoPath, start + index), await RgbHashAsync(paths[index], 0));
        Assert.Contains("complete", await File.ReadAllTextAsync(System.IO.Path.Combine(directory, "export.json")));
    }

    [Fact]
    public async Task VariableFrameRateIsIndexedAndExportedWithoutDuplication()
    {
        using var output = new TemporaryDirectory();
        var info = await fixture.Engine.ProbeAsync(fixture.VariablePath);
        var frames = await fixture.Engine.IndexFramesAsync(fixture.VariablePath, info);
        Assert.Equal(10, frames.Count);
        Assert.InRange(frames[9].Time, 1.29, 1.31);
        var gaps = frames.Zip(frames.Skip(1)).Select(pair => Math.Round(pair.Second.Time - pair.First.Time, 2)).Distinct().ToArray();
        Assert.True(gaps.Length > 1);
        var range = new FrameRange(4, 6).ToTimeRange(frames, info.Duration);
        Assert.Equal(0.4, range.Start, 3);
        Assert.Equal(0.9, range.End, 3);
        var directory = await fixture.Engine.ExportFramesAsync(fixture.Asset(fixture.VariablePath), new FrameRange(0, 9), 10, output.Path);
        Assert.Equal(10, Directory.GetFiles(directory, "frame_*.png").Length);
        Assert.Equal(await RgbHashAsync(fixture.VariablePath, 6), await RgbHashAsync(System.IO.Path.Combine(directory, "frame_00000006.png"), 0));
    }

    [Fact]
    public async Task PreciseTrimHasSelectedFrameCountAndAudio()
    {
        using var output = new TemporaryDirectory();
        var info = await fixture.Engine.ProbeAsync(fixture.VideoPath);
        var frames = await fixture.Engine.IndexFramesAsync(fixture.VideoPath, info);
        var path = await fixture.Engine.TrimVideoAsync(fixture.Asset(fixture.VideoPath), new FrameRange(2, 4), frames, info, output.Path);
        var trimmed = await fixture.Engine.ProbeAsync(path);
        var trimmedFrames = await fixture.Engine.IndexFramesAsync(path, trimmed);
        Assert.Equal(3, trimmedFrames.Count);
        Assert.Single(trimmed.AudioTracks);
        Assert.InRange(trimmed.Duration, 0.45, 0.6);
    }

    [Fact]
    public async Task AudioRangeExtractsTheChosenChannelNotAMonoMix()
    {
        using var output = new TemporaryDirectory();
        var info = await fixture.Engine.ProbeAsync(fixture.VideoPath);
        var path = await fixture.Engine.ExportAudioAsync(fixture.Asset(fixture.VideoPath), info, info.AudioTracks[0], 1, new TimeRange(0.25, 0.75), output.Path);
        var audio = await fixture.Engine.ProbeAsync(path);
        Assert.False(audio.HasVideo);
        Assert.Equal(1, Assert.Single(audio.AudioTracks).Channels);
        Assert.InRange(audio.Duration, 0.499, 0.501);
        var samples = ReadWaveSamples(await File.ReadAllBytesAsync(path));
        Assert.True(Magnitude(samples, 880, 48000) > 10 * Magnitude(samples, 440, 48000));
    }

    [Fact]
    public async Task AudioOnlyInputsWorkAndInvalidChannelsFail()
    {
        using var output = new TemporaryDirectory();
        var info = await fixture.Engine.ProbeAsync(fixture.VideoPath);
        var wave = await fixture.Engine.ExportAudioAsync(fixture.Asset(fixture.VideoPath), info, info.AudioTracks[0], null, new TimeRange(0, 1), output.Path);
        var audio = await fixture.Engine.ProbeAsync(wave);
        var clip = await fixture.Engine.ExportAudioAsync(fixture.Asset(wave) with { Kind = MediaKind.Audio }, audio, audio.AudioTracks[0], null, new TimeRange(0.2, 0.4), output.Path);
        Assert.InRange((await fixture.Engine.ProbeAsync(clip)).Duration, 0.199, 0.201);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Engine.ExportAudioAsync(fixture.Asset(fixture.VideoPath), info, info.AudioTracks[0], 2, new TimeRange(0, 1), output.Path));
    }

    [Fact]
    public async Task InvalidMediaReportsAnErrorInsteadOfProducingAnExport()
    {
        using var temporary = new TemporaryDirectory();
        var invalid = temporary.FilePath("invalid.mp4");
        await File.WriteAllTextAsync(invalid, "not a video");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Engine.ProbeAsync(invalid));
        await Assert.ThrowsAsync<IOException>(() => fixture.Engine.ExportFrameAsync(fixture.Asset(fixture.VideoPath), 1000, temporary.Path));
        Assert.Empty(Directory.GetFiles(temporary.Path, "*.png"));
    }

    [Fact]
    public async Task ContainerStartOffsetDoesNotChangeFrameIdentity()
    {
        using var output = new TemporaryDirectory();
        var shifted = output.FilePath("offset.mkv");
        await new ProcessRunner().RunAsync(fixture.Tools.Ffmpeg,
            ["-v", "error", "-nostdin", "-i", fixture.VideoPath, "-c", "copy", "-output_ts_offset", "5", shifted]);
        var info = await fixture.Engine.ProbeAsync(shifted);
        Assert.InRange(info.StartTime, 4.99, 5.01);
        var frames = await fixture.Engine.IndexFramesAsync(shifted, info);
        Assert.Equal(0, frames[0].Time, 3);
        Assert.Equal(12, frames.Count);
        var file = new FileInfo(shifted);
        var asset = new MediaAsset(output.Path, file.Name, MediaKind.Video, file.Length, file.LastWriteTimeUtc.Ticks);
        var image = await fixture.Engine.ExportFrameAsync(asset, 4, output.Path);
        Assert.Equal(await RgbHashAsync(fixture.VideoPath, 4), await RgbHashAsync(image, 0));
        var trimmed = await fixture.Engine.TrimVideoAsync(asset, new FrameRange(2, 4), frames, info, output.Path);
        Assert.InRange((await fixture.Engine.ProbeAsync(trimmed)).Duration, 0.45, 0.6);
    }

    [Fact]
    public async Task CancellingProcessingTerminatesTheChildPromptly()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var clock = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProcessRunner().RunAsync(fixture.Tools.Ffmpeg,
            ["-v", "error", "-nostdin", "-re", "-f", "lavfi", "-i", "testsrc=size=16x16:rate=1", "-t", "60", "-f", "null", "-"], cancellationToken: cancellation.Token));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
    }

    private async Task<string> RgbHashAsync(string path, int index) =>
        (await new ProcessRunner().CaptureAsync(fixture.Tools.Ffmpeg,
            ["-v", "error", "-nostdin", "-i", path, "-map", "0:v:0", "-vf", $"select=eq(n\\,{index.ToString(CultureInfo.InvariantCulture)}),format=rgb24", "-frames:v", "1", "-fps_mode", "passthrough", "-f", "hash", "-hash", "md5", "-"])).Trim();

    private static short[] ReadWaveSamples(byte[] bytes)
    {
        using var reader = new BinaryReader(new MemoryStream(bytes));
        reader.BaseStream.Position = 12;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var name = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var length = reader.ReadUInt32();
            if (name == "data")
            {
                var samples = new short[Math.Min(length / 2, 4800)];
                for (var index = 0; index < samples.Length; index++)
                    samples[index] = reader.ReadInt16();
                return samples;
            }
            reader.BaseStream.Position += length + length % 2;
        }
        throw new InvalidDataException("No WAV sample data.");
    }

    private static double Magnitude(short[] samples, double frequency, int sampleRate)
    {
        var real = 0.0;
        var imaginary = 0.0;
        for (var index = 0; index < samples.Length; index++)
        {
            var angle = 2 * Math.PI * frequency * index / sampleRate;
            real += samples[index] * Math.Cos(angle);
            imaginary += samples[index] * Math.Sin(angle);
        }
        return Math.Sqrt(real * real + imaginary * imaginary);
    }
}
