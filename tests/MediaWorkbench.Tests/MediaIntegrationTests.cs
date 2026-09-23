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
    public async Task WindowDecodeFillsNeighbouringFramesWithOrdinalIdenticalPixels()
    {
        using var cache = new TemporaryDirectory();
        var engine = new MediaEngine(fixture.Tools, cache.FilePath("cache"));
        var asset = fixture.Asset(fixture.VideoPath);
        var bytes = await engine.GetFrameAsync(asset, 7, 12);
        Assert.Equal(bytes, await fixture.Engine.GetFrameAsync(asset, 7));
        var cached = Directory.GetFiles(cache.FilePath("cache"), "*.png");
        Assert.Equal(12 - Math.Max(0, 7 - MediaEngine.WindowBefore), cached.Length);
        Assert.Empty(Directory.GetDirectories(cache.FilePath("cache")));
        using var output = new TemporaryDirectory();
        foreach (var index in new[] { 2, 6, 8, 11 })
        {
            var path = await MediaEngine.SaveFrameAsync(asset, index, await engine.GetFrameAsync(asset, index, 12), output.Path);
            Assert.Equal(await RgbHashAsync(fixture.VideoPath, index), await RgbHashAsync(path, 0));
        }
        Assert.Equal(cached.Length, Directory.GetFiles(cache.FilePath("cache"), "*.png").Length);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => engine.GetFrameAsync(asset, 12, 12));
    }

    [Fact]
    public async Task VerifiedSeekDecodesTheSameOrdinalsAsDecodingFromTheStart()
    {
        using var cache = new TemporaryDirectory();
        using var output = new TemporaryDirectory();
        var engine = new MediaEngine(fixture.Tools, cache.FilePath("cache")) { SeekMargin = 20 };
        var asset = fixture.Asset(fixture.LongPath);
        var info = await engine.ProbeAsync(asset.FullPath);
        var frames = await engine.IndexFramesAsync(asset.FullPath, info);
        Assert.Equal(300, frames.Count);
        Assert.InRange(info.StartTime, 4.9, 5.1);
        // 29/30/31 straddle a keyframe; 155 is mid-GOP; 299 is the last frame; 10 is too early to seek and must use the start path.
        foreach (var (index, expectSeek) in new[] { (60, true), (155, true), (299, true), (10, false) })
        {
            var bytes = await engine.GetFrameAsync(asset, index, frames, info);
            Assert.Equal(expectSeek, engine.LastWindowUsedSeek);
            var path = await MediaEngine.SaveFrameAsync(asset, index, bytes, output.Path);
            Assert.Equal(await RgbHashAsync(fixture.LongPath, index), await RgbHashAsync(path, 0));
        }
        foreach (var index in new[] { 55, 61, 75, 150, 170 })
        {
            var path = await MediaEngine.SaveFrameAsync(asset, index, await engine.GetFrameAsync(asset, index, frames, info), output.Path);
            Assert.Equal(await RgbHashAsync(fixture.LongPath, index), await RgbHashAsync(path, 0));
        }
        Assert.Empty(Directory.GetDirectories(cache.FilePath("cache")));
    }

    [Fact]
    public async Task SeekIsRejectedWhenTimestampsDoNotMatchTheIndexAndTheStartPathStillReturnsTheRightFrame()
    {
        using var cache = new TemporaryDirectory();
        using var output = new TemporaryDirectory();
        var engine = new MediaEngine(fixture.Tools, cache.FilePath("cache")) { SeekMargin = 20 };
        var asset = fixture.Asset(fixture.LongPath);
        var info = await engine.ProbeAsync(asset.FullPath);
        var frames = await engine.IndexFramesAsync(asset.FullPath, info);
        // An index that lost frame 100: every later ordinal now carries the next frame's timestamp. Trusting it would return the wrong picture.
        var wrong = frames.Where(frame => frame.Index != 100).Select((frame, position) => frame with { Index = position }).ToArray();
        var bytes = await engine.GetFrameAsync(asset, 120, wrong, info);
        Assert.False(engine.LastWindowUsedSeek);
        var path = await MediaEngine.SaveFrameAsync(asset, 120, bytes, output.Path);
        Assert.Equal(await RgbHashAsync(fixture.LongPath, 120), await RgbHashAsync(path, 0));
        // Once a file has failed verification it is not tried again in this session, even with a correct index.
        await engine.GetFrameAsync(asset, 250, frames, info);
        Assert.False(engine.LastWindowUsedSeek);
    }

    [Fact]
    public async Task BlackBordersAreDetectedAndTheWholeVideoIsCroppedAndTurned()
    {
        using var output = new TemporaryDirectory();
        var path = output.FilePath("letterboxed.mkv");
        // A 160 x 120 picture inside a 240 x 160 black frame, with sound, 3 seconds at 10 fps.
        await new ProcessRunner().RunAsync(fixture.Tools.Ffmpeg,
            ["-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i", "testsrc2=size=160x120:rate=10:duration=3", "-f", "lavfi", "-i", "sine=frequency=440:duration=3",
             "-vf", "pad=240:160:40:20:black", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", path]);
        var asset = fixture.Asset(path);
        var info = await fixture.Engine.ProbeAsync(path);
        var frames = await fixture.Engine.IndexFramesAsync(path, info);
        Assert.Equal(30, frames.Count);

        var bounds = await fixture.Engine.DetectContentBoundsAsync(asset, info, 240, 160);
        Assert.NotNull(bounds);
        Assert.InRange(bounds.X, 38, 42);
        Assert.InRange(bounds.Y, 18, 22);
        Assert.InRange(bounds.X + bounds.Width, 198, 202);
        Assert.InRange(bounds.Y + bounds.Height, 138, 142);

        var transform = new VideoTransform(new PixelCrop(40, 20, 160, 120), 90);
        var whole = await fixture.Engine.ExportTransformedVideoAsync(asset, info, transform, 240, 160, output.Path);
        Assert.EndsWith("letterboxed_edit.mp4", whole);
        var edited = await fixture.Engine.ProbeAsync(whole);
        Assert.Equal("120", edited.Metadata["width"]);
        Assert.Equal("160", edited.Metadata["height"]);
        Assert.Single(edited.AudioTracks);
        Assert.Equal(30, (await fixture.Engine.IndexFramesAsync(whole, edited)).Count);
        // The turned picture has no black border left: detection on the result finds the whole frame.
        Assert.Equal(new PixelCrop(0, 0, 120, 160), await fixture.Engine.DetectContentBoundsAsync(fixture.Asset(whole), edited, 120, 160));

        var trimmed = await fixture.Engine.TrimVideoAsync(asset, new FrameRange(5, 14), frames, info, output.Path, transform: new VideoTransform(new PixelCrop(40, 20, 101, 51), 0), frameWidth: 240, frameHeight: 160);
        var trimmedInfo = await fixture.Engine.ProbeAsync(trimmed);
        Assert.Equal("100", trimmedInfo.Metadata["width"]);
        Assert.Equal("50", trimmedInfo.Metadata["height"]);
        Assert.Equal(10, (await fixture.Engine.IndexFramesAsync(trimmed, trimmedInfo)).Count);

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Engine.ExportTransformedVideoAsync(asset, info, new VideoTransform(null, 0), 240, 160, output.Path));
    }

    [Fact]
    public async Task IndexingReportsProgressWithoutFloodingAndStaysSilentWhenCached()
    {
        var engine = new MediaEngine(fixture.Tools, fixture.Temporary.FilePath("progress cache"));
        var asset = fixture.Asset(fixture.LongPath);
        var info = await engine.ProbeAsync(asset.FullPath);
        var reports = new List<int>();
        var frames = await engine.IndexFramesAsync(asset, info, progress: new Collecting(reports));
        Assert.Equal(300, frames.Count);
        // Reports are throttled to a few a second so they cannot swamp the interface, so a file this short may only report once.
        Assert.NotEmpty(reports);
        Assert.True(reports.Count <= 12, $"A 300-frame file should not report {reports.Count} times.");
        Assert.Equal(reports.Order(), reports);
        Assert.Equal(300, reports[^1]);
        reports.Clear();
        await engine.IndexFramesAsync(asset, info, progress: new Collecting(reports));
        Assert.Empty(reports);
    }

    /// <summary>Synchronous, unlike Progress&lt;T&gt;, so every report has arrived when the call returns.</summary>
    private sealed class Collecting(List<int> reports) : IProgress<int>
    {
        public void Report(int value) { lock (reports) reports.Add(value); }
    }

    [Fact]
    public async Task FrameIndexIsPersistedPerFileIdentityAndReusedOnlyWhenValid()
    {
        using var cache = new TemporaryDirectory();
        var engine = new MediaEngine(fixture.Tools, cache.FilePath("cache"));
        var asset = fixture.Asset(fixture.VideoPath);
        var info = await engine.ProbeAsync(asset.FullPath);
        var first = await engine.IndexFramesAsync(asset, info);
        var indexFile = Assert.Single(Directory.GetFiles(cache.FilePath("cache"), "*.index.json"));
        var second = await engine.IndexFramesAsync(asset, info);
        Assert.Equal(first, second);
        File.WriteAllText(indexFile, "[]");
        var third = await engine.IndexFramesAsync(asset, info);
        Assert.Equal(first, third);
        Assert.NotEqual("[]", await File.ReadAllTextAsync(indexFile));
        await engine.IndexFramesAsync(asset with { ModifiedTicks = asset.ModifiedTicks + 1 }, info);
        Assert.Equal(2, Directory.GetFiles(cache.FilePath("cache"), "*.index.json").Length);
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
    public async Task WaveformFollowsTheSoundInTimeAndIsCachedPerTrack()
    {
        using var output = new TemporaryDirectory();
        var path = output.FilePath("quiet then loud.wav");
        // One second of silence, then one second of a half-scale tone.
        await new ProcessRunner().RunAsync(fixture.Tools.Ffmpeg,
            ["-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i", "aevalsrc=if(lt(t\\,1)\\,0\\,0.5*sin(2*PI*440*t)):s=48000:d=2", "-c:a", "pcm_s16le", path]);
        var asset = fixture.Asset(path) with { Kind = MediaKind.Audio };
        var info = await fixture.Engine.ProbeAsync(path);
        var waveform = await fixture.Engine.GetWaveformAsync(asset, info, info.AudioTracks[0]);
        Assert.InRange(waveform.Count, 198, 202);
        Assert.InRange(waveform.Duration, 1.98, 2.02);
        Assert.All(Enumerable.Range(5, 85), bucket => Assert.InRange(waveform.Maximum[bucket], -0.01f, 0.01f));
        Assert.All(Enumerable.Range(110, 80), bucket => Assert.InRange(waveform.Maximum[bucket], 0.4f, 0.55f));
        Assert.All(Enumerable.Range(110, 80), bucket => Assert.InRange(waveform.Minimum[bucket], -0.55f, -0.4f));

        // The second request must come from the cache: it still answers after the source file is gone.
        File.Delete(path);
        var cached = await fixture.Engine.GetWaveformAsync(asset, info, info.AudioTracks[0]);
        Assert.Equal(waveform.Count, cached.Count);

        var video = await fixture.Engine.ProbeAsync(fixture.VideoPath);
        var mixed = await fixture.Engine.GetWaveformAsync(fixture.Asset(fixture.VideoPath), video, video.AudioTracks[0]);
        Assert.InRange(mixed.Duration, 1.95, 2.05);
        Assert.InRange(mixed.Maximum.Max(), 0.1f, 0.25f);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Engine.GetWaveformAsync(asset, info, new AudioTrack(9, 1, "missing")));
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
