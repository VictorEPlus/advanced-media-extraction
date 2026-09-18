using MediaWorkbench.Core;

namespace MediaWorkbench.Tests;

public sealed class WaveformAndFrameRateTests
{
    private static MemoryStream Pcm(params short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return new MemoryStream(bytes);
    }

    /// <summary>Hands out one byte at a time so every sample straddles two reads.</summary>
    private sealed class TrickleStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(1, count));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task BucketsHoldTheLowestAndHighestSampleAndKeepTheRemainder()
    {
        var waveform = await Waveform.FromPcmAsync(Pcm(100, -200, 16384, -32768, 5, 7, 9), 3);
        Assert.Equal(3, waveform.Count);
        Assert.Equal(-200 / 32768f, waveform.Minimum[0]);
        Assert.Equal(16384 / 32768f, waveform.Maximum[0]);
        Assert.Equal(-1f, waveform.Minimum[1]);
        Assert.Equal(7 / 32768f, waveform.Maximum[1]);
        Assert.Equal(9 / 32768f, waveform.Minimum[2]);
        Assert.Equal(3.0 / Waveform.SampleRate, waveform.BucketSeconds);
    }

    [Fact]
    public async Task SamplesSplitAcrossReadsAreReassembled()
    {
        var whole = await Waveform.FromPcmAsync(Pcm(1000, -3000, 12345, -12345), 2);
        var trickled = await Waveform.FromPcmAsync(new TrickleStream(Pcm(1000, -3000, 12345, -12345)), 2);
        Assert.Equal(whole.Minimum, trickled.Minimum);
        Assert.Equal(whole.Maximum, trickled.Maximum);
    }

    [Fact]
    public async Task StoredWaveformRoundTripsAndDamagedFilesAreIgnored()
    {
        var waveform = await Waveform.FromPcmAsync(Pcm(0, 32767, -32768, 16384, -8192, 1), 2);
        var bytes = waveform.ToBytes();
        var restored = Waveform.FromBytes(bytes);
        Assert.NotNull(restored);
        Assert.Equal(waveform.Count, restored.Count);
        Assert.Equal(waveform.BucketSeconds, restored.BucketSeconds);
        for (var index = 0; index < waveform.Count; index++)
        {
            Assert.InRange(restored.Maximum[index] - waveform.Maximum[index], -0.01, 0.01);
            Assert.InRange(restored.Minimum[index] - waveform.Minimum[index], -0.01, 0.01);
        }
        Assert.Null(Waveform.FromBytes(bytes[..^1]));
        Assert.Null(Waveform.FromBytes([1, 2, 3]));
        Assert.Null(Waveform.FromBytes(new byte[16]));
    }

    [Fact]
    public void LongFilesGetWiderBucketsInsteadOfMoreOfThem()
    {
        Assert.Equal(80, Waveform.SamplesPerBucket(60));
        var tenHours = Waveform.SamplesPerBucket(36000);
        Assert.True(tenHours > 80);
        Assert.True(36000.0 * Waveform.SampleRate / tenHours <= Waveform.MaximumBuckets);
    }

    [Fact]
    public void ConstantFrameRateIsMeasuredDespiteMillisecondRounding()
    {
        // Matroska stores whole milliseconds, so 29.97 fps gaps alternate between 33 and 34 ms.
        var frames = Enumerable.Range(0, 300).Select(index => new VideoFrame(index, Math.Round(index * 1001 / 30000.0, 3), 0)).ToArray();
        var rate = FrameRateInfo.FromFrames(frames, FrameRateInfo.FromRatio("30000/1001"));
        Assert.NotNull(rate);
        Assert.False(rate.IsVariable);
        Assert.True(rate.IsMeasured);
        Assert.Equal("29.97", rate.Number);
        // Without a header the raw measurement is reported, and a header that disagrees with the frames is not believed.
        Assert.InRange(FrameRateInfo.FromFrames(frames)!.FramesPerSecond, 29.96, 29.98);
        Assert.InRange(FrameRateInfo.FromFrames(frames, FrameRateInfo.FromRatio("25/1"))!.FramesPerSecond, 29.96, 29.98);
    }

    [Fact]
    public void UnevenFrameGapsAreReportedAsVariable()
    {
        var times = new[] { 0, 0.1, 0.2, 0.3, 0.4, 0.5, 0.7, 0.9, 1.1, 1.3 };
        var rate = FrameRateInfo.FromFrames([.. times.Select((time, index) => new VideoFrame(index, time, 0))]);
        Assert.NotNull(rate);
        Assert.True(rate.IsVariable);
        Assert.InRange(rate.FramesPerSecond, 6.9, 7.0);
    }

    [Theory]
    [InlineData("30000/1001", "29.97")]
    [InlineData("24000/1001", "23.976")]
    [InlineData("60/1", "60")]
    public void HeaderRatiosBecomeReadableNumbers(string ratio, string expected) => Assert.Equal(expected, FrameRateInfo.FromRatio(ratio)!.Number);

    [Theory]
    [InlineData("0/0")]
    [InlineData("abc")]
    [InlineData(null)]
    public void UnusableHeaderRatiosAreIgnored(string? ratio) => Assert.Null(FrameRateInfo.FromRatio(ratio));

    [Fact]
    public void TooFewFramesGiveNoMeasuredRate() => Assert.Null(FrameRateInfo.FromFrames([new VideoFrame(0, 0, 0)]));
}
