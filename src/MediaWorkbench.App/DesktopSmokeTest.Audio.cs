using System.IO;
using System.Windows;
using MediaWorkbench.Core;

namespace MediaWorkbench.App;

internal static partial class DesktopSmokeTest
{
    /// <summary>A selected video shows its frame rate, draws its sound under the timeline, and a section dragged on the sound snaps to whole frames.</summary>
    private static async Task CheckVideoSoundAsync(MainViewModel model, MainWindow window, string dataDirectory, CancellationToken token)
    {
        Require(model.HasFrameRate && model.FrameRateText == "6" && model.FrameRateUnit == "fps", $"A 6 fps video should read 6 fps beside the frame counter, not '{model.FrameRateText} {model.FrameRateUnit}'.");
        Require(model.MediaSummary.Contains("6 fps", StringComparison.Ordinal), "The line under the file name should include the frame rate: " + model.MediaSummary);
        await WaitUntilAsync(() => !model.IsWaveformLoading, token);
        Require(model.Waveform is { Count: > 50 } && model.ShowVideoWaveform && !model.ShowAudioStage, "A video with sound should get a waveform under its timeline: " + model.WaveformMessage);
        Require(Directory.GetFiles(Path.Combine(dataDirectory, "cache"), "*.wave.bin").Length == 1, "The waveform should be cached for the file identity.");
        model.SelectAudioRange(0.2, 0.6);
        Require(model.InFrame == 1 && model.OutFrame == 3, $"A section dragged on the sound should snap to whole frames (expected 1 to 3, got {model.InFrame} to {model.OutFrame}).");
        Require(Math.Abs(model.AudioStart - 1 / 6.0) < 0.002 && Math.Abs(model.AudioEnd - 4 / 6.0) < 0.002, "The audio selection should cover exactly the marked frames.");
        model.SeekAudio(0.51);
        await WaitUntilAsync(() => !model.IsPreviewBusy, token);
        Require(model.CurrentFrame == 3 && model.DisplayedFrame == 3 && Math.Abs(model.AudioPosition - 0.5) < 0.002, "Clicking the sound should go to the frame showing at that moment.");
        Layout(window);
        var root = (UIElement)window.Content;
        Require(Shown(window.FrameRateReadout) && Shown(window.VideoWaveform) && window.VideoWaveform.ActualHeight >= 26 && model.CanSnipAudio, "The frame rate and the waveform should be visible, and the sound can be snipped, for a video with sound.");
        var timelineLeft = window.Timeline.TranslatePoint(new Point(0, 0), root).X;
        var waveformLeft = window.VideoWaveform.TranslatePoint(new Point(0, 0), root).X;
        Require(Math.Abs(timelineLeft - waveformLeft) < 0.5 && Math.Abs(window.Timeline.ActualWidth - window.VideoWaveform.ActualWidth) < 0.5, "The waveform must line up with the frame timeline above it.");
        var tabs = window.MainTabs.TranslatePoint(new Point(0, 0), root);
        var header = window.SelectionHeader.TranslatePoint(new Point(0, 0), root);
        Require(Shown(window.SelectionHeader) && header.X > tabs.X + window.MainTabs.ActualWidth && header.Y < tabs.Y + window.MainTabs.ActualHeight && header.Y + window.SelectionHeader.ActualHeight > tabs.Y,
            $"The selected-file header should sit on the same row as the Library and Preview tabs (tabs {tabs} {window.MainTabs.ActualWidth:0}x{window.MainTabs.ActualHeight:0}, header {header} {window.SelectionHeader.ActualWidth:0}x{window.SelectionHeader.ActualHeight:0}).");
        Render(window, Path.Combine(dataDirectory, "workspace-sound.png"));
        CheckIndexingBar(model, window, dataDirectory);
        model.InFrame = 0;
        model.OutFrame = model.MaximumFrame;
    }

    /// <summary>The dotted loading bar beside the frame counter while a video is being indexed.</summary>
    private static void CheckIndexingBar(MainViewModel model, MainWindow window, string dataDirectory)
    {
        var half = MainViewModel.DotBar(0.5, 0);
        Require(half.Lead.Length == 0 && half.Lit.Length == 14 && half.Tail.Length == 14, "Half way through, half of the fourteen dots should be lit.");
        Require(MainViewModel.DotBar(0.001, 0).Lit.Length == 2 && MainViewModel.DotBar(0.99, 0).Tail.Length == 0, "The bar should always light at least one dot and fill up at the end.");
        var first = MainViewModel.DotBar(null, 0);
        var later = MainViewModel.DotBar(null, 5);
        Require(first.Lit.Length == 6 && later.Lit.Length == 6 && first.Lead.Length == 0 && later.Lead.Length == 10 && (later.Lead + later.Lit + later.Tail).Length == 28, "Without an estimate three lit dots should travel along the row.");
        Layout(window);
        Require(!model.ShowIndexing && !Shown(window.IndexingIndicator), "The indexing bar must be hidden once the index is ready.");
        model.IsIndexing = true;
        model.SetIndexProgress(450, 1000);
        Layout(window);
        Require(model.ShowIndexing && model.IndexingText == "Indexing frames, 45%" && Shown(window.IndexingIndicator), "While indexing, the dotted bar and its percentage should show beside the frame counter.");
        Render(window, Path.Combine(dataDirectory, "workspace-indexing.png"));
        model.SetIndexProgress(0, 0);
        model.IsIndexing = false;
    }

    private static void Layout(Window window)
    {
        var content = (FrameworkElement)window.Content;
        for (var pass = 0; pass < 2; pass++)
        {
            content.InvalidateMeasure();
            content.Measure(new Size(1440, 900));
            content.Arrange(new Rect(0, 0, 1440, 900));
            content.UpdateLayout();
        }
    }

    /// <summary>IsVisible is always false in a window that was never shown, so walk the Visibility of the element and its ancestors instead.</summary>
    private static bool Shown(FrameworkElement element)
    {
        for (DependencyObject? current = element; current is not null; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
            if (current is UIElement { Visibility: not Visibility.Visible })
                return false;
        return element.ActualWidth > 0 && element.ActualHeight > 0;
    }

    /// <summary>An audio file fills the preview with its waveform; a section can be selected, played and snipped to a WAV of that length.</summary>
    private static async Task CheckAudioFileAsync(MainViewModel model, MainWindow window, string dataDirectory, ToolPaths tools, CancellationToken token)
    {
        var directory = Path.Combine(dataDirectory, "synthetic-audio");
        Directory.CreateDirectory(directory);
        await new ProcessRunner().RunAsync(tools.Ffmpeg,
            ["-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i", "aevalsrc=if(lt(mod(t\\,1)\\,0.5)\\,0.6*sin(2*PI*330*t)\\,0.05*sin(2*PI*90*t)):s=44100:d=4", "-c:a", "pcm_s16le", Path.Combine(directory, "Sample voice.wav")],
            cancellationToken: token);
        await model.OpenLibraryAsync(directory);
        model.SelectedAsset = model.Assets.Single();
        await WaitUntilAsync(() => !model.IsPreviewBusy && !model.IsWaveformLoading && model.Waveform is not null, token);
        Require(model.IsAudio && model.ShowAudioStage && !model.ShowVideoWaveform && !model.ShowEmptyState && !model.HasFrameRate, "An audio file should show its waveform in the preview area and no frame rate.");
        Require(model.Waveform is { Count: > 350 } && Math.Abs(model.WaveformDuration - 4) < 0.05, "The waveform should span the whole audio file.");
        Require(model.AudioSelectionText.StartsWith("Whole track", StringComparison.Ordinal) && model.PlayRangeLabel == "Play selection", "A new audio file starts with everything selected.");
        model.SelectAudioRange(2.5, 1.0);
        Require(Math.Abs(model.AudioStart - 1.0) < 1e-9 && Math.Abs(model.AudioEnd - 2.5) < 1e-9 && model.CanSnipAudio && model.CanPlayRange, "A section dragged right to left should still select 1.0 to 2.5 s.");
        model.SeekAudio(1.75);
        Require(Math.Abs(model.AudioPosition - 1.75) < 1e-9, "Clicking the waveform of an audio file should move its playhead.");
        model.MarkOutCommand.Execute(null);
        Require(Math.Abs(model.AudioEnd - 1.75) < 1e-9, "O should end the selection at the audio playhead.");
        Layout(window);
        Require(Shown(window.AudioWaveform) && window.AudioWaveform.ActualHeight > 150 && !Shown(window.VideoTimeline) && Shown(window.PlayRangeButton) && Shown(window.RestartButton) && model.CanSnipAudio, "The audio stage, Play selection and Snip selection should be available for an audio file.");
        Render(window, Path.Combine(dataDirectory, "workspace-audio.png"));
        var before = model.JobHistory.Count;
        model.ExportAudioCommand.Execute(null);
        await WaitUntilAsync(() => model.JobHistory.Count > before && model.ActiveJobCount == 0, token);
        var record = model.JobHistory[0];
        Require(record.Succeeded && File.Exists(record.OutputPath), "Snipping the selection should write a WAV file: " + record.Status);
        var snipped = await new MediaEngine(tools, Path.Combine(dataDirectory, "cache")).ProbeAsync(record.OutputPath!, token);
        Require(Math.Abs(snipped.Duration - 0.75) < 0.01, $"The snipped WAV should be as long as the selection (0.75 s), not {snipped.Duration:0.###} s.");
    }
}
