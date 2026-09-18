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

    /// <summary>How many frames before a window a verified seek lands, so the frames that are kept are well clear of the seek point.</summary>
    public int SeekMargin { get; init; } = 48;

    private static readonly System.Text.RegularExpressions.Regex ShowInfoLine = new(@"\bn:\s*(\d+)\s+pts:\s*-?\d+\s+pts_time:(-?[0-9.]+)", System.Text.RegularExpressions.RegexOptions.Compiled);
    private readonly ProcessRunner runner = new();
    // Frames and thumbnails queue separately so a folder full of video thumbnails can never hold up frame stepping.
    private readonly SemaphoreSlim cacheGate = new(2);
    private readonly SemaphoreSlim thumbnailGate = new(2);
    private readonly SemaphoreSlim waveformGate = new(1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> seekUnsafe = new();
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
                foreach (var key in new[] { "width", "height", "codec_name", "pix_fmt", "avg_frame_rate", "r_frame_rate", "display_aspect_ratio", "sample_aspect_ratio", "color_space", "color_transfer", "bits_per_raw_sample" })
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

    /// <summary>Indexes decoded frames, reusing a persisted index for this exact file identity when one exists. <paramref name="progress"/> receives the number of frames read so far while a new index is being built.</summary>
    public async Task<IReadOnlyList<VideoFrame>> IndexFramesAsync(MediaAsset asset, MediaInfo info, CancellationToken cancellationToken = default, IProgress<int>? progress = null)
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
        var frames = await IndexFramesAsync(asset.FullPath, info, cancellationToken, progress);
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

    public async Task<IReadOnlyList<VideoFrame>> IndexFramesAsync(string path, MediaInfo info, CancellationToken cancellationToken = default, IProgress<int>? progress = null)
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
                if (frames.Count % 25 == 0)
                    progress?.Report(frames.Count);
            }, cancellationToken);
        progress?.Report(frames.Count);
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
            cancellationToken: cancellationToken), cancellationToken, thumbnailGate);

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
    public Task<byte[]> GetFrameAsync(MediaAsset asset, int frameIndex, int frameCount, CancellationToken cancellationToken = default) =>
        GetFrameAsync(asset, frameIndex, frameCount, null, null, cancellationToken);

    /// <summary>
    /// As above, but when the frame index is supplied a cache miss deep in the file jumps to a point <see cref="SeekMargin"/> frames
    /// before the window and decodes from there instead of from the start. Every frame decoded after the jump must report exactly
    /// the timestamp the index holds for its ordinal; if any does not, the result is discarded, the file is marked unsafe for
    /// seeking and the window is decoded from the start. Frame identity therefore stays the decoded ordinal.
    /// </summary>
    public Task<byte[]> GetFrameAsync(MediaAsset asset, int frameIndex, IReadOnlyList<VideoFrame> frames, MediaInfo info, CancellationToken cancellationToken = default) =>
        GetFrameAsync(asset, frameIndex, frames.Count, frames, info, cancellationToken);

    /// <summary>True when the frame's PNG is already in the disk cache, so showing it needs no decode.</summary>
    public byte[]? TryGetCachedFrame(MediaAsset asset, int frameIndex) => TryReadCached(CacheKey(FrameIdentity(asset, frameIndex)));

    /// <summary>Whether the last window for this file was decoded with a verified seek (true) or from the start (false). For diagnostics and tests.</summary>
    public bool LastWindowUsedSeek { get; private set; }

    private async Task<byte[]> GetFrameAsync(MediaAsset asset, int frameIndex, int frameCount, IReadOnlyList<VideoFrame>? frames, MediaInfo? info, CancellationToken cancellationToken)
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
            var pattern = Path.Combine(window.Replace("%", "%%", StringComparison.Ordinal), "frame_%08d.png");
            var usedSeek = frames is not null && info is not null && await TrySeekWindowAsync(asset, start, end, frames, info, window, pattern, cancellationToken);
            LastWindowUsedSeek = usedSeek;
            if (!usedSeek)
                await runner.RunAsync(tools.Ffmpeg,
                    ["-v", "error", "-nostdin", "-y", "-i", asset.FullPath, "-map", "0:v:0", "-vf", $"select=between(n\\,{start}\\,{end})", "-frames:v", (end - start + 1).ToString(CultureInfo.InvariantCulture), "-fps_mode", "passthrough", "-start_number", start.ToString(CultureInfo.InvariantCulture), pattern],
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

    /// <summary>
    /// Decodes [start, end] after an input seek and verifies it. The seek lands on the frame <see cref="SeekMargin"/> ordinals before
    /// <paramref name="start"/>; showinfo logs the timestamp of every frame that reaches the filter graph, and each must equal the
    /// indexed timestamp of the ordinal it is assumed to be. Returns false, leaving no files behind, when seeking is not applicable or not proven.
    /// </summary>
    private async Task<bool> TrySeekWindowAsync(MediaAsset asset, int start, int end, IReadOnlyList<VideoFrame> frames, MediaInfo info, string window, string pattern, CancellationToken cancellationToken)
    {
        var seekFrame = start - SeekMargin;
        if (seekFrame <= 0 || end >= frames.Count || seekUnsafe.ContainsKey(asset.Identity))
            return false;
        var smallestGap = double.MaxValue;
        for (var index = seekFrame; index <= end; index++)
            smallestGap = Math.Min(smallestGap, frames[index].Time - frames[index - 1].Time);
        if (!(smallestGap > 0.0001))
            return false;
        var tolerance = Math.Min(0.002, smallestGap / 4);
        var seekTime = frames[seekFrame].Time - Math.Min(0.010, smallestGap / 2);
        var first = start - seekFrame;
        var last = end - seekFrame;
        var logged = new Dictionary<int, double>();
        try
        {
            await runner.RunAsync(tools.Ffmpeg,
                ["-v", "info", "-hide_banner", "-nostats", "-nostdin", "-y", "-copyts", "-ss", MediaNumber.Format(seekTime), "-i", asset.FullPath, "-map", "0:v:0",
                 "-vf", $"showinfo=checksum=0,select=between(n\\,{first}\\,{last})", "-frames:v", (end - start + 1).ToString(CultureInfo.InvariantCulture), "-fps_mode", "passthrough", "-start_number", start.ToString(CultureInfo.InvariantCulture), pattern],
                errorOutput: line =>
                {
                    var match = ShowInfoLine.Match(line);
                    if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal)
                        && double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var time))
                        logged.TryAdd(ordinal, time);
                },
                cancellationToken: cancellationToken);
        }
        catch (InvalidOperationException) { logged.Clear(); }
        var proven = logged.Count > last;
        for (var ordinal = 0; proven && ordinal <= last; ordinal++)
            proven = logged.TryGetValue(ordinal, out var time) && Math.Abs(time - info.StartTime - frames[seekFrame + ordinal].Time) <= tolerance;
        proven = proven && Directory.EnumerateFiles(window, "frame_*.png").Count() == end - start + 1;
        if (proven)
            return true;
        cancellationToken.ThrowIfCancellationRequested();
        seekUnsafe[asset.Identity] = true;
        foreach (var file in Directory.EnumerateFiles(window))
            File.Delete(file);
        return false;
    }

    /// <summary>
    /// Loudness outline of one audio track for drawing. The track is decoded once to 8 kHz mono, reduced to bucket extremes as it streams
    /// (nothing large is held or written), and the small result is cached per file identity and track. Silence is inserted ahead of a track
    /// that starts late, so bucket times line up with the normalized frame timestamps.
    /// </summary>
    public async Task<Waveform> GetWaveformAsync(MediaAsset asset, MediaInfo info, AudioTrack track, CancellationToken cancellationToken = default)
    {
        if (!info.AudioTracks.Contains(track))
            throw new ArgumentException("Select an audio track from this media file.");
        var path = Path.Combine(cacheDirectory, CacheKey(asset.Identity + $"|waveform-v1|{track.StreamIndex}") + ".wave.bin");
        await waveformGate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(path))
            {
                try
                {
                    if (Waveform.FromBytes(await File.ReadAllBytesAsync(path, cancellationToken)) is { } cached)
                    {
                        File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                        return cached;
                    }
                }
                catch (IOException) { }
            }
            Waveform? waveform = null;
            await runner.RunBinaryAsync(tools.Ffmpeg,
                ["-v", "error", "-nostdin", "-i", asset.FullPath, "-map", $"0:{track.StreamIndex}", "-vn", "-af", "aresample=async=1:first_pts=0", "-ac", "1", "-ar", Waveform.SampleRate.ToString(CultureInfo.InvariantCulture), "-f", "s16le", "pipe:1"],
                async stream => waveform = await Waveform.FromPcmAsync(stream, Waveform.SamplesPerBucket(info.Duration), cancellationToken), cancellationToken);
            if (waveform is null || waveform.Count == 0)
                throw new IOException("No audio could be decoded from this track.");
            Directory.CreateDirectory(cacheDirectory);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, waveform.ToBytes(), cancellationToken);
                lock (trimLock) File.Move(temporary, path, true);
            }
            catch (IOException) { }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            return waveform;
        }
        finally { waveformGate.Release(); }
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

    /// <summary>The crop and rotation with the size of the frame they apply to; null size or identity means "leave the picture alone".</summary>
    private static string TransformFilter(VideoTransform? transform, int frameWidth, int frameHeight) =>
        transform is null || frameWidth <= 0 || frameHeight <= 0 || transform.Filter(frameWidth, frameHeight) is not { Length: > 0 } filter ? "" : filter + ",";

    public async Task<string> TrimVideoAsync(MediaAsset asset, FrameRange range, IReadOnlyList<VideoFrame> frames, MediaInfo info,
        string directory, IProgress<ExportProgress>? progress = null, CancellationToken cancellationToken = default,
        VideoTransform? transform = null, int frameWidth = 0, int frameHeight = 0)
    {
        var times = range.ToTimeRange(frames, info.Duration);
        using var output = OutputReservation.Create(directory, Path.GetFileNameWithoutExtension(asset.Name) + "_trim", ".mp4");
        var arguments = new List<string>
        {
            "-v", "error", "-nostdin", "-y", "-i", asset.FullPath, "-map", "0:v:0",
            // Pad odd dimensions to even so 4:2:0 H.264 encoding never fails on odd-sized sources; even sizes are unchanged.
            "-vf", $"trim=start_frame={range.Start}:end_frame={range.EndInclusive + 1},setpts=PTS-STARTPTS,{TransformFilter(transform, frameWidth, frameHeight)}pad=ceil(iw/2)*2:ceil(ih/2)*2",
            "-fps_mode", "vfr", "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p"
        };
        if (info.AudioTracks.Count > 0)
            arguments.AddRange(["-map", $"0:{info.AudioTracks[0].StreamIndex}", "-af", $"atrim=start={MediaNumber.Format(times.Start)}:end={MediaNumber.Format(times.End)},asetpts=PTS-STARTPTS", "-c:a", "aac"]);
        arguments.AddRange(["-movflags", "+faststart", "-progress", "pipe:1", "-nostats", output.Path]);
        await runner.RunAsync(tools.Ffmpeg, arguments, line => ReportTimeProgress(line, times.Duration, progress), cancellationToken);
        output.Complete();
        return output.Path;
    }

    /// <summary>
    /// Re-encodes the whole video with a crop and a quarter-turn rotation. Every frame is kept with its own timing (variable frame
    /// rate passes through) and the first audio track is carried along. The source file is never changed.
    /// </summary>
    public async Task<string> ExportTransformedVideoAsync(MediaAsset asset, MediaInfo info, VideoTransform transform, int frameWidth, int frameHeight,
        string directory, IProgress<ExportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (frameWidth <= 0 || frameHeight <= 0)
            throw new ArgumentException("The frame size is not known yet. Wait for the first frame to appear.");
        if (transform.IsIdentity(frameWidth, frameHeight))
            throw new ArgumentException("Set a crop or a rotation first; there is nothing to change.");
        using var output = OutputReservation.Create(directory, Path.GetFileNameWithoutExtension(asset.Name) + "_edit", ".mp4");
        var arguments = new List<string>
        {
            "-v", "error", "-nostdin", "-y", "-i", asset.FullPath, "-map", "0:v:0",
            "-vf", $"{TransformFilter(transform, frameWidth, frameHeight)}pad=ceil(iw/2)*2:ceil(ih/2)*2",
            "-fps_mode", "vfr", "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p"
        };
        if (info.AudioTracks.Count > 0)
            arguments.AddRange(["-map", $"0:{info.AudioTracks[0].StreamIndex}", "-c:a", "aac"]);
        arguments.AddRange(["-movflags", "+faststart", "-progress", "pipe:1", "-nostats", output.Path]);
        await runner.RunAsync(tools.Ffmpeg, arguments, line => ReportTimeProgress(line, Math.Max(0.001, info.Duration), progress), cancellationToken);
        output.Complete();
        return output.Path;
    }

    private static readonly System.Text.RegularExpressions.Regex CropDetectLine = new(@"\bcrop=(\d+):(\d+):(\d+):(\d+)", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Finds the picture inside black borders (letterbox or pillarbox bars). A few moments spread along the video are examined and the
    /// results are joined, so a dark scene at one of them cannot shrink the answer. Returns null when no usable picture area was found.
    /// </summary>
    public async Task<PixelCrop?> DetectContentBoundsAsync(MediaAsset asset, MediaInfo info, int frameWidth, int frameHeight, CancellationToken cancellationToken = default)
    {
        if (frameWidth <= 0 || frameHeight <= 0)
            return null;
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        foreach (var fraction in info.Duration > 2 ? new[] { 0.1, 0.3, 0.5, 0.7, 0.9 } : new[] { 0.0 })
        {
            PixelCrop? last = null;
            var arguments = new List<string> { "-v", "info", "-hide_banner", "-nostats", "-nostdin" };
            if (fraction > 0)
                arguments.AddRange(["-ss", MediaNumber.Format(info.Duration * fraction)]);
            // round=2 keeps every detected pixel; reset=0 joins the frames of one sample.
            arguments.AddRange(["-i", asset.FullPath, "-map", "0:v:0", "-an", "-vf", "cropdetect=limit=24:round=2:reset=0", "-frames:v", "12", "-f", "null", "-"]);
            try
            {
                await runner.RunAsync(tools.Ffmpeg, arguments, errorOutput: line =>
                {
                    var match = CropDetectLine.Match(line);
                    if (match.Success && int.TryParse(match.Groups[1].Value, out var width) && int.TryParse(match.Groups[2].Value, out var height)
                        && int.TryParse(match.Groups[3].Value, out var x) && int.TryParse(match.Groups[4].Value, out var y)
                        && width > 0 && height > 0 && x >= 0 && y >= 0 && x + width <= frameWidth && y + height <= frameHeight)
                        last = new PixelCrop(x, y, width, height);
                }, cancellationToken: cancellationToken);
            }
            catch (InvalidOperationException) { }
            cancellationToken.ThrowIfCancellationRequested();
            if (last is not { } found)
                continue;
            left = Math.Min(left, found.X);
            top = Math.Min(top, found.Y);
            right = Math.Max(right, found.X + found.Width);
            bottom = Math.Max(bottom, found.Y + found.Height);
        }
        return right - left >= VideoTransform.MinimumSize && bottom - top >= VideoTransform.MinimumSize ? new PixelCrop(left, top, right - left, bottom - top) : null;
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

    private async Task<byte[]> CachedAsync(string identity, Func<string, Task> create, CancellationToken cancellationToken, SemaphoreSlim? gate = null)
    {
        var key = CacheKey(identity);
        Directory.CreateDirectory(cacheDirectory);
        var path = CachePath(key);
        gate ??= cacheGate;
        await gate.WaitAsync(cancellationToken);
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
            gate.Release();
        }
    }

    private void TrimCache(string keep)
    {
        var files = new DirectoryInfo(cacheDirectory).EnumerateFiles()
            .Where(file => !file.Name.Contains(".tmp", StringComparison.Ordinal)
                && (file.Name.EndsWith(".png", StringComparison.Ordinal) || file.Name.EndsWith(".index.json", StringComparison.Ordinal) || file.Name.EndsWith(".wave.bin", StringComparison.Ordinal) || file.Name.EndsWith(".strip.bin", StringComparison.Ordinal))) // .strip.bin: left by an earlier version; still trimmed so they age out
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
