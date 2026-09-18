using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using MediaWorkbench.Core;

namespace MediaWorkbench.App;

// Frame-rate readout and the audio waveform: its picture, playhead and selected section.
public sealed partial class MainViewModel
{
    private CancellationTokenSource? waveformCancellation;
    private FrameRateInfo? frameRate;

    [ObservableProperty] private Waveform? waveform;
    [ObservableProperty] private bool isWaveformLoading;
    [ObservableProperty] private string waveformMessage = "";
    /// <summary>Seconds from the start: the playing position, otherwise the current frame's time (video) or the audio cursor.</summary>
    [ObservableProperty] private double audioPosition;

    public bool HasFrameRate => IsVideo && frameRate is not null;
    public string FrameRateText => frameRate is null ? "" : (frameRate.IsVariable ? "~" : "") + frameRate.Number;
    public string FrameRateUnit => frameRate is { IsVariable: true } ? "fps, variable" : "fps";
    public string FrameRateNote => frameRate switch
    {
        null => "",
        { IsMeasured: false } => "Frames per second from the file header. It is measured from the real frame times once indexing finishes.",
        { IsVariable: true } => $"Average of {frames.Count:N0} indexed frames. This video has a variable frame rate: the time between frames changes, so use frame numbers, not time, to count frames.",
        _ => $"Measured from the {frames.Count:N0} indexed frames of this video."
    };

    /// <summary>The waveform sits under the frame timeline for a video with sound and fills the preview for an audio file.</summary>
    public bool ShowVideoWaveform => IsVideo && HasAudio;
    public bool ShowAudioStage => IsAudio;
    public bool ShowVideoSurface => (ShowPlayback && !concealVideoSurface || holdingVideoSurface) && !IsAudio;
    public bool CanSnipAudio => HasAudio && mediaInfo is not null && AudioEnd > AudioStart;
    public bool CanPlayRange => CanPlay && (HasFrames || IsAudio && AudioEnd > AudioStart);
    public string PlayRangeLabel => IsAudio ? "Play selection" : "Play marked range";
    /// <summary>Seconds across the waveform. For a video it ends at the last frame so it lines up with the frame timeline above it.</summary>
    public double WaveformDuration => HasFrames && frames[^1].Time > 0 ? frames[^1].Time : mediaInfo?.Duration ?? 0;
    public string AudioSelectionText
    {
        get
        {
            if (mediaInfo is null || !HasAudio || AudioEnd <= AudioStart)
                return "";
            var whole = AudioStart <= 0.0005 && AudioEnd >= mediaInfo.Duration - 0.0005;
            var length = WaveformView.FormatTime(AudioEnd - AudioStart);
            return whole ? $"Whole track selected, {length}" : $"{WaveformView.FormatTime(AudioStart)} to {WaveformView.FormatTime(AudioEnd)}, {length} selected";
        }
    }
    public string AudioHint => IsVideo
        ? "Drag on the sound to mark a section; it snaps to whole frames and moves the in and out markers. Click to go to that moment."
        : "Drag to select a section, drag its edges to adjust, click to move the playhead, double-click to select everything. I and O mark while playing.";

    partial void OnAudioStartChanged(double value) => NotifyAudioState();
    partial void OnAudioEndChanged(double value) => NotifyAudioState();
    partial void OnWaveformChanged(Waveform? value) => NotifyAudioState();

    private void NotifyAudioState()
    {
        foreach (var property in new[] { nameof(AudioSelectionText), nameof(CanSnipAudio), nameof(CanPlayRange), nameof(PlayRangeLabel), nameof(ShowVideoWaveform), nameof(ShowAudioStage), nameof(ShowVideoSurface), nameof(WaveformDuration), nameof(AudioHint) })
            OnPropertyChanged(property);
    }

    private void NotifyFrameRate()
    {
        foreach (var property in new[] { nameof(HasFrameRate), nameof(FrameRateText), nameof(FrameRateUnit), nameof(FrameRateNote) })
            OnPropertyChanged(property);
    }

    /// <summary>Header value first so a number shows at once; replaced by the measured rate when the frame index lands.</summary>
    private void UpdateFrameRate()
    {
        var header = mediaInfo is null ? null
            : FrameRateInfo.FromRatio(mediaInfo.Metadata.GetValueOrDefault("avg_frame_rate")) ?? FrameRateInfo.FromRatio(mediaInfo.Metadata.GetValueOrDefault("r_frame_rate"));
        frameRate = !IsVideo || mediaInfo is null ? null : FrameRateInfo.FromFrames(frames, header) ?? header;
        NotifyFrameRate();
    }

    private void ResetAudioView()
    {
        waveformCancellation?.Cancel();
        waveformCancellation = null;
        Waveform = null;
        IsWaveformLoading = false;
        WaveformMessage = "";
        AudioPosition = 0;
        frameRate = null;
        NotifyFrameRate();
    }

    private async Task LoadWaveformAsync()
    {
        waveformCancellation?.Cancel();
        waveformCancellation = null;
        Waveform = null;
        var item = loadedAsset;
        var info = mediaInfo;
        var track = SelectedAudioTrack;
        if (item is null || info is null || track is null)
        {
            IsWaveformLoading = false;
            WaveformMessage = item is not null && info is not null && info.AudioTracks.Count == 0 ? "This file has no audio track." : "";
            return;
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        waveformCancellation = cancellation;
        IsWaveformLoading = true;
        WaveformMessage = "Reading the audio to draw it…";
        var selectedEngine = engine;
        try
        {
            var result = await Task.Run(() => selectedEngine.GetWaveformAsync(item.Asset, info, track, cancellation.Token), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (loadedAsset != item || SelectedAudioTrack != track)
                return;
            Waveform = result;
            WaveformMessage = "";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or ArgumentException)
        {
            if (loadedAsset == item)
                WaveformMessage = "The audio could not be drawn. Playback and export still work.";
        }
        finally
        {
            if (waveformCancellation == cancellation)
            {
                waveformCancellation = null;
                IsWaveformLoading = false;
            }
        }
    }

    /// <summary>Selects a section of sound. For a video the section snaps outward to whole frames and becomes the in/out markers.</summary>
    public void SelectAudioRange(double start, double end) => Guard(() =>
    {
        if (mediaInfo is null)
            return;
        start = Math.Clamp(start, 0, mediaInfo.Duration);
        end = Math.Clamp(end, 0, mediaInfo.Duration);
        if (end < start) (start, end) = (end, start);
        if (HasFrames)
        {
            var first = FrameAtTime(start);
            var last = Math.Max(first, FrameAtTime(Math.Max(start, end - 0.000001)));
            // Order the two writes so in never passes out on the way.
            if (first > OutFrame) { OutFrame = last; InFrame = first; }
            else { InFrame = first; OutFrame = last; }
            Status = $"Marked frames {InFrame:N0} to {OutFrame:N0} from the sound.";
            return;
        }
        if (end - start < 0.01)
            return;
        AudioStart = start;
        AudioEnd = end;
        Status = AudioSelectionText + ". Play selection to check it, then Snip selection to WAV.";
    });

    /// <summary>Moves the playhead to a moment in the sound: the playing position, the nearest earlier frame, or the audio cursor.</summary>
    public void SeekAudio(double seconds) => Guard(() =>
    {
        if (mediaInfo is null)
            return;
        seconds = Math.Clamp(seconds, 0, mediaInfo.Duration);
        if (ShowPlayback && Player.IsPlaying)
        {
            stopPlaybackAt = null;
            Player.Time = (long)(seconds * 1000);
            AudioPosition = seconds;
        }
        else if (HasFrames)
            CurrentFrame = FrameAtTime(seconds);
        else
            AudioPosition = seconds;
    });
}

// The dotted loading bar shown while a video's frame index is being built.
public sealed partial class MainViewModel
{
    public const int IndexDotCount = 14;
    private const string Dot = "\u25CF ";
    private int indexedFrames;
    private int indexEstimate;
    private int indexPulses;

    /// <summary>Only once FFprobe has reported frames: an index that comes straight from the cache never flashes the bar.</summary>
    public bool ShowIndexing => IsVideo && IsIndexing && indexedFrames > 0;
    /// <summary>Share of the expected frame count read so far, or null when the file header gives no frame rate to estimate from.</summary>
    public double? IndexFraction => indexEstimate <= 0 ? null : Math.Clamp(indexedFrames / (double)indexEstimate, 0, 0.99);
    public string IndexDotsLead => DotBar(IndexFraction, indexPulses).Lead;
    public string IndexDotsLit => DotBar(IndexFraction, indexPulses).Lit;
    public string IndexDotsTail => DotBar(IndexFraction, indexPulses).Tail;
    public string IndexingText => IndexFraction is { } fraction
        ? $"Indexing frames, {fraction:P0}"
        : $"Indexing frames, {indexedFrames:N0} read";
    public string IndexingNote => "Reading the exact time of every frame so stepping and exports are frame-accurate. This happens once per video; you can already play it and look at the first frame.";

    /// <summary>
    /// Splits a row of dots into dim, lit and dim parts. With a known fraction the lit dots fill from the left;
    /// without one, three lit dots travel along the row, one place per progress report.
    /// </summary>
    internal static (string Lead, string Lit, string Tail) DotBar(double? fraction, int pulses, int dots = IndexDotCount)
    {
        int lead, lit;
        if (fraction is { } known)
        {
            lead = 0;
            lit = Math.Clamp((int)Math.Round(known * dots), 1, dots);
        }
        else
        {
            lit = Math.Min(3, dots);
            lead = Math.Abs(pulses) % (dots - lit + 1);
        }
        return (Repeat(lead), Repeat(lit), Repeat(dots - lead - lit));

        static string Repeat(int count) => string.Concat(Enumerable.Repeat(Dot, count));
    }

    internal void SetIndexProgress(int framesRead, int estimatedFrames)
    {
        indexedFrames = framesRead;
        indexEstimate = estimatedFrames;
        indexPulses++;
        NotifyIndexing();
    }

    private void NotifyIndexing()
    {
        foreach (var property in new[] { nameof(ShowIndexing), nameof(IndexFraction), nameof(IndexDotsLead), nameof(IndexDotsLit), nameof(IndexDotsTail), nameof(IndexingText), nameof(FrameTotalText) })
            OnPropertyChanged(property);
    }
}
