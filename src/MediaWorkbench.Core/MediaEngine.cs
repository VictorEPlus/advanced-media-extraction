using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MediaWorkbench.Core;

public sealed class MediaEngine(ToolPaths tools, string cacheDirectory, int cacheMegabytes = 512)
{
    /// <summary>Frames decoded before the requested ordinal when a cache miss triggers a window decode.</summary>
    public const int WindowBefore = 5;
    /// <summary>Frames decoded after the requested ordinal when a cache miss triggers a window decode.</summary>
    public const int WindowAfter = 15;

    private readonly ProcessRunner runner = new();
    private readonly SemaphoreSlim cacheGate = new(2);
    private readonly object trimLock = new();

    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        var json = await runner.CaptureAsync(tools.Ffprobe,
            ["-v", "error", "-show_format", "-show_streams", "-of", "json", path], cancellationToken);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var format = root.GetProperty("format");
        var duration = ReadNumber(format, "duration");
        var start = ReadNumber(format, "start_time");
        var audio = new List<AudioTrack>();
        var metadata = new Dictionary<string, string>();
        foreach (var key in new[] { "format_name", "bit_rate", "size" })
            if (format.TryGetProperty(key, out var value)) metadata[key] = value.ToString();
        if (format.TryGetProperty("tags", out var formatTags))
            foreach (var tag in formatTags.EnumerateObject()) metadata["Embedded " + tag.Name] = tag.Value.ToString();
        var hasVideo = false;
        foreach (var stream in root.GetProperty("streams").EnumerateArray())
        {
            var kind = stream.GetProperty("codec_type").GetString();
            if (kind == "video" && !hasVideo)
            {
                hasVideo = true;
                foreach (var key in new[] { "width", "height", "codec_name", "pix_fmt", "avg_frame_rate", "display_aspect_ratio", "sample_aspect_ratio", "color_space", "color_transfer", "bits_per_raw_sample" })
                    if (stream.TryGetProperty(key, out var value)) metadata[key] = value.ToString();
                if (stream.TryGetProperty("tags", out var streamTags))
                    foreach (var tag in streamTags.EnumerateObject()) metadata["Video " + tag.Name] = tag.Value.ToString();
            }
            if (kind == "audio")
            {
                var index = stream.GetProperty("index").GetInt32();
                var channels = stream.TryGetProperty("channels", out var count) ? count.GetInt32() : 1;
                var codec = stream.TryGetProperty("codec_name", out var codecName) ? codecName.GetString() : "audio";
                audio.Add(new AudioTrack(index, channels, $"Track {audio.Count + 1} - {codec}, {channels} channels"));
            }
        }
        return new MediaInfo(duration, start, hasVideo, audio) { Metadata = metadata };
    }

    /// <summary>Indexes decoded frames, reusing a persisted index for this exact file identity when one exists.</summary>
    public async Task<IReadOnlyList<VideoFrame>> IndexFramesAsync(MediaAsset asset, MediaInfo info, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(cacheDirectory, CacheKey(asset.Identity + "|index-v1") + ".index.json");
        if (File.Exists(path))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<VideoFrame[]>(await File.ReadAllBytesAsync(path, cancellationToken));
                if (cached is { Length: > 0 })
                {
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                    return cached;
                }
            }
            catch (JsonException) { }
            catch (IOException) { }
        }
        var frames = await IndexFramesAsync(asset.FullPath, info, cancellationToken);
        Directory.CreateDirectory(cacheDirectory);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(frames), cancellationToken);
            lock (trimLock) File.Move(temporary, path, true);
        }
        catch (IOException) { }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
        return frames;
    }

    public async Task<IReadOnlyList<VideoFrame>> IndexFramesAsync(string path, MediaInfo info, CancellationToken cancellationToken = default)
    {
        var frames = new List<VideoFrame>();
        var invalidTimestamp = false;
        await runner.RunAsync(tools.Ffprobe,
            ["-v", "error", "-select_streams", "v:0", "-show_frames", "-show_entries", "frame=best_effort_timestamp_time,duration_time,pkt_duration_time", "-of", "compact=p=0:nk=0", path],
            line =>
            {
                if (!line.Contains("best_effort_timestamp_time=", StringComparison.Ordinal))
                    return;
                double? time = null;
                var duration = 0.0;
                foreach (var entry in line.Split('|'))
                {
                    var pair = entry.Split('=', 2);
                    if (pair.Length != 2 || !double.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                        continue;
                    if (pair[0] == "best_effort_timestamp_time")
                        time = Math.Max(0, number - info.StartTime);
                    if (pair[0] is "duration_time" or "pkt_duration_time")
                        duration = number;
                }
                if (time is null)
                    invalidTimestamp = true;
                frames.Add(new VideoFrame(frames.Count, time ?? 0, duration));
            }, cancellationToken);
        if (invalidTimestamp || frames.Count == 0)
            throw new InvalidDataException("This video has no usable frame timestamps. Precision editing is unavailable.");
        for (var index = 1; index < frames.Count; index++)
            if (frames[index].Time < frames[index - 1].Time)
                throw new InvalidDataException("Non-monotonic frame timestamps prevent safe range editing.");
        return frames;
    }

    public Task<byte[]> GetThumbnailAsync(MediaAsset asset, CancellationToken cancellationToken = default) => CachedAsync(
        asset.Identity + "|thumbnail-v2", temporary => runner.RunAsync(tools.Ffmpeg,
            ["-v", "error", "-nostdin", "-y", "-i", asset.FullPath, "-map", "0:v:0", "-frames:v", "1", "-vf", "thumbnail=24,scale=224:224:force_original_aspect_ratio=decrease", "-f", "image2", temporary],
            cancellationToken: cancellationToken), cancellationToken);

    public Task<byte[]> GetFrameAsync(MediaAsset asset, int frameIndex, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frameIndex);
        return CachedAsync(FrameIdentity(asset, frameIndex), temporary => runner.RunAsync(tools.Ffmpeg,
            ["-v", "error", "-nostdin", "-y", "-i", asset.FullPath, "-map", "0:v:0", "-vf", $"select=eq(n\\,{frameIndex})", "-frames:v", "1", "-fps_mode", "passthrough", "-f", "image2", temporary],
            cancellationToken: cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Returns the exact frame at <paramref name="frameIndex"/>. On a cache miss this decodes a window of neighbouring
    /// ordinals in one pass so that stepping forward or backward hits the cache. Frame identity stays the decoded ordinal.
    /// </summary>
    public async Task<byte[]> GetFrameAsync(MediaAsset asset, int frameIndex, int frameCount, CancellationToken cancellationToken = default)
    {
        if (frameCount <= 0)
            return await GetFrameAsync(asset, frameIndex, cancellationToken);
        ArgumentOutOfRangeException.ThrowIfNegative(frameIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(frameIndex, frameCount);
        var key = CacheKey(FrameIdentity(asset, frameIndex));
        if (TryReadCached(key) is { } cached)
            return cached;
        var start = Math.Max(0, frameIndex - WindowBefore);
        var end = Math.Min(frameCount - 1, frameIndex + WindowAfter);
        await cacheGate.WaitAsync(cancellationToken);
        var window = Path.Combine(cacheDirectory, "window-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (TryReadCached(key) is { } cachedMeanwhile)
                return cachedMeanwhile;
            Directory.CreateDirectory(window);
            await runner.RunAsync(tools.Ffmpeg,
                ["-v", "error", "-nostdin", "-y", "-i", asset.FullPath, "-map", "0:v:0", "-vf", $"select=between(n\\,{start}\\,{end})", "-frames:v", (end - start + 1).ToString(CultureInfo.InvariantCulture), "-fps_mode", "passthrough", "-start_number", start.ToString(CultureInfo.InvariantCulture), Path.Combine(window.Replace("%", "%%", StringComparison.Ordinal), "frame_%08d.png")],
                cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            byte[]? result = null;
            foreach (var file in Directory.EnumerateFiles(window, "frame_*.png"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (!int.TryParse(name.AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < start || index > end || new FileInfo(file).Length == 0)
                    continue;
                var target = CachePath(CacheKey(FrameIdentity(asset, index)));
                lock (trimLock)
                {
                    if (File.Exists(target)) File.SetLastWriteTimeUtc(target, DateTime.UtcNow);
                    else File.Move(file, target);
                }
                if (index == frameIndex)
                    result = await File.ReadAllBytesAsync(target, cancellationToken);
            }
            if (result is null)
                throw new IOException("No image was decoded. The file or selected frame may be unsupported.");
            lock (trimLock) TrimCache(CachePath(key));
            return result;
        }
        finally
        {
            try { if (Directory.Exists(window)) Directory.Delete(window, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            cacheGate.Release();
        }
    }

    public async Task<string> ExportFrameAsync(MediaAsset asset, int frameIndex, string directory, CancellationToken cancellationToken = default)
    {
        var bytes = await GetFrameAsync(asset, frameIndex, cancellationToken);
        return await SaveFrameAsync(asset, frameIndex, bytes, directory, cancellationToken);
    }

    public static async Task<string> SaveFrameAsync(MediaAsset asset, int frameIndex, byte[] bytes, string directory, CancellationToken cancellationToken = default)
    {
        using var output = OutputReservation.Create(directory, $"{Path.GetFileNameWithoutExtension(asset.Name)}_frame_{frameIndex:000000}", ".png");
        await File.WriteAllBytesAsync(output.Path, bytes, cancellationToken);
        output.Complete();
        return output.Path;
    }

    public async Task<string> ExportFramesAsync(MediaAsset asset, FrameRange range, int frameCount, string directory,
        IProgress<ExportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        range.Validate(frameCount);
        if (!Path.IsPathFullyQualified(directory))
            throw new ArgumentException("Choose an absolute export directory in Settings.");
        var stem = Path.GetFileNameWithoutExtension(asset.Name);
        if (stem.Length > 70)
            stem = stem[..70];
        var output = Path.Combine(directory, $"{stem}_frames_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(output);
        var manifest = Path.Combine(output, "export.json");
        await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(new { Status = "incomplete", range.Start, range.EndInclusive }), cancellationToken);
        await runner.RunAsync(tools.Ffmpeg,
            ["-v", "error", "-nostdin", "-n", "-i", asset.FullPath, "-map", "0:v:0", "-vf", $"select=between(n\\,{range.Start}\\,{range.EndInclusive})", "-frames:v", range.Count.ToString(CultureInfo.InvariantCulture), "-fps_mode", "passthrough", "-start_number", range.Start.ToString(CultureInfo.InvariantCulture), "-progress", "pipe:1", "-nostats", Path.Combine(output.Replace("%", "%%", StringComparison.Ordinal), "frame_%08d.png")],
            line => ReportFrameProgress(line, range.Count, progress), cancellationToken);
        var actual = Directory.EnumerateFiles(output, "frame_*.png").Count();
        if (actual != range.Count)
            throw new IOException($"Expected {range.Count} frames, but exported {actual}. Partial output: {output}");
        await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(new { Status = "complete", range.Start, range.EndInclusive, Count = actual }), cancellationToken);
        progress?.Report(new ExportProgress(1, "Complete"));
        return output;
    }

    public async Task<string> TrimVideoAsync(MediaAsset asset, FrameRange range, IReadOnlyList<VideoFrame> frames, MediaInfo info,
        string directory, IProgress<ExportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var times = range.ToTimeRange(frames, info.Duration);
        using var output = OutputReservation.Create(directory, Path.GetFileNameWithoutExtension(asset.Name) + "_trim", ".mp4");
        var arguments = new List<string>
        {
            "-v", "error", "-nostdin", "-y", "-i", asset.FullPath, "-map", "0:v:0",
            // Pad odd dimensions to even so 4:2:0 H.264 encoding never fails on odd-sized sources; even sizes are unchanged.
            "-vf", $"trim=start_frame={range.Start}:end_frame={range.EndInclusive + 1},setpts=PTS-STARTPTS,pad=ceil(iw/2)*2:ceil(ih/2)*2",
            "-fps_mode", "vfr", "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p"
        };
        if (info.AudioTracks.Count > 0)
            arguments.AddRange(["-map", $"0:{info.AudioTracks[0].StreamIndex}", "-af", $"atrim=start={MediaNumber.Format(times.Start)}:end={MediaNumber.Format(times.End)},asetpts=PTS-STARTPTS", "-c:a", "aac"]);
        arguments.AddRange(["-movflags", "+faststart", "-progress", "pipe:1", "-nostats", output.Path]);
        await runner.RunAsync(tools.Ffmpeg, arguments, line => ReportTimeProgress(line, times.Duration, progress), cancellationToken);
        output.Complete();
        return output.Path;
    }

    public async Task<string> ExportAudioAsync(MediaAsset asset, MediaInfo info, AudioTrack track, int? channel, TimeRange range,
        string directory, IProgress<ExportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        range.Validate(info.Duration);
        if (!info.AudioTracks.Contains(track))
            throw new ArgumentException("Select an audio track from this media file.");
        if (channel is < 0 || channel >= track.Channels)
            throw new ArgumentOutOfRangeException(nameof(channel));
        using var output = OutputReservation.Create(directory, Path.GetFileNameWithoutExtension(asset.Name) + "_audio", ".wav");
        var filter = $"atrim=start={MediaNumber.Format(range.Start)}:end={MediaNumber.Format(range.End)},asetpts=PTS-STARTPTS";
        if (channel.HasValue)
            filter += $",pan=mono|c0=c{channel.Value}";
        await runner.RunAsync(tools.Ffmpeg,
            ["-v", "error", "-nostdin", "-y", "-i", asset.FullPath, "-map", $"0:{track.StreamIndex}", "-vn", "-af", filter, "-c:a", "pcm_s16le", "-progress", "pipe:1", "-nostats", output.Path],
            line => ReportTimeProgress(line, range.Duration, progress), cancellationToken);
        output.Complete();
        return output.Path;
    }

    private static string FrameIdentity(MediaAsset asset, int frameIndex) => asset.Identity + $"|frame-v1|{frameIndex}";

    private static string CacheKey(string identity) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));

    private string CachePath(string key) => Path.Combine(cacheDirectory, key + ".png");

    private byte[]? TryReadCached(string key)
    {
        var path = CachePath(key);
        lock (trimLock)
        {
            if (!File.Exists(path))
                return null;
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return File.ReadAllBytes(path);
        }
    }

    private async Task<byte[]> CachedAsync(string identity, Func<string, Task> create, CancellationToken cancellationToken)
    {
        var key = CacheKey(identity);
        Directory.CreateDirectory(cacheDirectory);
        var path = CachePath(key);
        await cacheGate.WaitAsync(cancellationToken);
        var temporary = Path.Combine(cacheDirectory, key + "." + Guid.NewGuid().ToString("N") + ".tmp.png");
        try
        {
            if (TryReadCached(key) is { } cached)
                return cached;
            await create(temporary);
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0)
                throw new IOException("No image was decoded. The file or selected frame may be unsupported.");
            var bytes = await File.ReadAllBytesAsync(temporary, cancellationToken);
            lock (trimLock)
            {
                File.Move(temporary, path, true);
                TrimCache(path);
            }
            return bytes;
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
            cacheGate.Release();
        }
    }

    private void TrimCache(string keep)
    {
        var files = new DirectoryInfo(cacheDirectory).EnumerateFiles()
            .Where(file => !file.Name.Contains(".tmp", StringComparison.Ordinal)
                && (file.Name.EndsWith(".png", StringComparison.Ordinal) || file.Name.EndsWith(".index.json", StringComparison.Ordinal)))
            .OrderBy(file => file.LastWriteTimeUtc).ToArray();
        var size = files.Sum(file => file.Length);
        foreach (var file in files)
        {
            if (size <= cacheMegabytes * 1024L * 1024L)
                break;
            if (file.FullName == keep)
                continue;
            try { var length = file.Length; file.Delete(); size -= length; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static double ReadNumber(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && double.TryParse(property.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : 0;

    private static void ReportFrameProgress(string line, int total, IProgress<ExportProgress>? progress)
    {
        if (line.StartsWith("frame=", StringComparison.Ordinal) && int.TryParse(line[6..].Trim(), out var frames))
            progress?.Report(new ExportProgress(Math.Clamp((double)frames / total, 0, 1), $"{frames:N0} / {total:N0} frames"));
    }

    private static void ReportTimeProgress(string line, double duration, IProgress<ExportProgress>? progress)
    {
        if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && double.TryParse(line[12..], NumberStyles.Float, CultureInfo.InvariantCulture, out var microseconds))
        {
            var fraction = Math.Clamp(microseconds / 1000000 / duration, 0, 1);
            progress?.Report(new ExportProgress(fraction, $"{fraction:P0}"));
        }
    }
}
