using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MediaWorkbench.Core;
using Path = System.IO.Path;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace MediaWorkbench.App;

/// <summary>
/// A visible, scripted run of the everyday actions (opening folders, clicking files, stepping, playing, pausing) recorded at
/// 60 frames a second, for finding things that jump or flash too quickly to see in two screenshots. Three records line up:
/// <list type="bullet">
/// <item>the screen recording (<c>probe.mkv</c>), which shows what a person sees, including the video player's own surface;</item>
/// <item>a code of cells drawn in the window's top-left corner: the action number and a counter that goes up on every
/// frame WPF draws, so each recorded frame can be matched to the log;</item>
/// <item><c>layout.jsonl</c>: every named element whose place or size changed on each drawn frame, and every change of the
/// view model's properties, stamped with that same counter.</item>
/// </list>
/// The test video carries its own frame number as a barcode along its top edge, so the recording also shows which frame
/// the preview is on after Play and Pause. Run it with <c>tools/motion-probe/run.ps1</c>, which also runs <c>analyze.py</c> on the result.
/// </summary>
internal static class DesktopMotionProbe
{
    public const int CodeCells = 16;
    public const double CellSize = 8;
    public const int BarcodeBits = 12;

    private sealed record Step(string Label, Func<Task> Run);

    public static async Task RunAsync(MainViewModel model, MainWindow window, string dataDirectory)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var tools = ToolPaths.Resolve();
        await tools.CheckAsync(timeout.Token);
        var media = Path.Combine(dataDirectory, "probe-media");
        await CreateMediaAsync(tools, media, timeout.Token);
        model.ExportDirectory = Path.Combine(dataDirectory, "exports");
        await model.SaveSettingsCommand.ExecuteAsync(null);
        model.Player.Mute = true;

        var work = SystemParameters.WorkArea;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = work.Left;
        window.Top = work.Top;
        window.Width = 1500;
        window.Height = 940;
        window.Topmost = true;
        window.Show();
        window.Activate();
        await Task.Delay(600, timeout.Token);

        var cells = AddCodeCells(window);
        var recorder = new LayoutRecorder(window, model, Path.Combine(dataDirectory, "layout.jsonl"), cells);
        var source = PresentationSource.FromVisual(window)!;
        var toDevice = source.CompositionTarget.TransformToDevice;
        var topLeft = window.PointToScreen(new Point(0, 0));
        var bottomRight = window.PointToScreen(new Point(window.ActualWidth, window.ActualHeight));
        var capture = new Int32Rect((int)topLeft.X & ~1, (int)topLeft.Y & ~1, ((int)(bottomRight.X - topLeft.X)) & ~1, ((int)(bottomRight.Y - topLeft.Y)) & ~1);
        File.WriteAllText(Path.Combine(dataDirectory, "probe.json"), JsonSerializer.Serialize(new
        {
            scale = toDevice.M11,
            capture = new { x = capture.X, y = capture.Y, width = capture.Width, height = capture.Height },
            cellSize = CellSize,
            cells = CodeCells,
            barcodeBits = BarcodeBits,
            originX = topLeft.X - capture.X,
            originY = topLeft.Y - capture.Y
        }));

        var video = Path.Combine(dataDirectory, "probe.mkv");
        using var ffmpeg = StartRecording(tools, capture, video, Path.Combine(dataDirectory, "ffmpeg.log"));
        await Task.Delay(1500, timeout.Token);
        recorder.Start();

        AssetViewModel Asset(string name) => model.Assets.Single(item => item.Name == name);
        void Click(string name) => window.Filmstrip.SelectedItem = Asset(name);
        async Task Idle(int milliseconds) => await Task.Delay(milliseconds, timeout.Token);
        async Task Settle() { await WaitAsync(() => !model.IsPreviewBusy, timeout.Token); await Idle(900); }
        async Task Steps(int count, int delta)
        {
            for (var index = 0; index < count; index++) { model.StepFrames(delta); await Idle(160); }
            await Settle();
        }

        Step[] steps =
        [
            new("open library", async () => { await model.OpenLibraryAsync(media); await Idle(2500); }),
            new("folder Trip C", async () => { model.SelectedFolderRow = model.FolderRows.First(row => row.Name == "Trip C"); await Idle(1500); }),
            new("folder Trip A", async () => { model.SelectedFolderRow = model.FolderRows.First(row => row.Name == "Trip A"); await Idle(1200); }),
            new("folder Trip C again", async () => { model.SelectedFolderRow = model.FolderRows.First(row => row.Name == "Trip C"); await Idle(1200); }),
            new("folder Trip B", async () => { model.SelectedFolderRow = model.FolderRows.First(row => row.Name == "Trip B"); await Idle(1200); }),
            new("all folders", async () => { model.SelectedFolderRow = model.FolderRows[0]; await Idle(1000); }),
            new("click landscape photo", async () => { Click("landscape photo.jpg"); await Settle(); }),
            new("click portrait photo", async () => { Click("portrait.jpg"); await Settle(); }),
            new("click square photo with a much longer file name", async () => { Click("square photo with a much longer file name.png"); await Settle(); }),
            new("click video", async () => { Click("counter video.mp4"); await Settle(); await Idle(800); }),
            new("step forward 12", () => Steps(12, 1)),
            new("play", async () => { model.TogglePlaybackCommand.Execute(null); await Idle(1500); }),
            new("pause", async () => { model.TogglePlaybackCommand.Execute(null); await Idle(2800); }),
            new("play again", async () => { model.TogglePlaybackCommand.Execute(null); await Idle(1200); }),
            new("pause again", async () => { model.TogglePlaybackCommand.Execute(null); await Idle(2800); }),
            new("step back 5", () => Steps(5, -1)),
            new("play after stepping", async () => { model.TogglePlaybackCommand.Execute(null); await Idle(1000); }),
            new("pause after stepping", async () => { model.TogglePlaybackCommand.Execute(null); await Idle(2800); }),
            new("click portrait video", async () => { Click("tall clip.mp4"); await Settle(); await Idle(600); }),
            new("click audio", async () => { Click("song.wav"); await Settle(); }),
            new("play audio", async () => { model.TogglePlaybackCommand.Execute(null); await Idle(1000); }),
            new("pause audio", async () => { model.TogglePlaybackCommand.Execute(null); await Idle(1200); }),
            new("click landscape photo again", async () => { Click("landscape photo.jpg"); await Settle(); }),
            new("click video again", async () => { Click("counter video.mp4"); await Settle(); }),
            new("favorite", async () => { model.ToggleFavoriteCommand.Execute(null); await Idle(700); }),
            new("new tab and add a second folder", async () => { model.NewTabCommand.Execute(null); await model.AddFolderAsync(Path.Combine(Path.GetDirectoryName(media)!, "probe-archive", "Scans")); await Idle(2500); }),
            new("click first file", async () => { window.Filmstrip.SelectedIndex = 0; await Settle(); }),
            new("back to the first tab", async () => { model.SelectedTab = model.Tabs[0]; await Idle(1200); }),
            new("to the second tab", async () => { model.SelectedTab = model.Tabs[1]; await Idle(1200); }),
            new("back to the first tab again", async () => { model.SelectedTab = model.Tabs[0]; await Idle(1200); }),
            new("folder Trip C from the tree", async () => { model.SelectedFolderRow = model.FolderRows.First(row => row.Name == "Trip C"); await Idle(1200); }),
            new("end", () => Idle(500))
        ];
        try
        {
            for (var index = 0; index < steps.Length; index++)
            {
                recorder.Begin(index, steps[index].Label);
                await steps[index].Run();
            }
        }
        finally
        {
            recorder.Stop();
            await StopRecordingAsync(ffmpeg);
            File.WriteAllLines(Path.Combine(dataDirectory, "steps.txt"), steps.Select((step, index) => $"{index}\t{step.Label}"));
        }
    }

    private static async Task WaitAsync(Func<bool> ready, CancellationToken token)
    {
        while (!ready())
            await Task.Delay(20, token);
    }

    /// <summary>Two folders of small files whose shapes differ: landscape, portrait and square photos, a landscape and a portrait video, and a sound file.</summary>
    private static async Task CreateMediaAsync(ToolPaths tools, string root, CancellationToken token)
    {
        var tripA = Path.Combine(root, "Trip A");
        var tripB = Path.Combine(root, "Trip B");
        Directory.CreateDirectory(tripA);
        Directory.CreateDirectory(tripB);
        var runner = new ProcessRunner();
        Task Make(params string[] arguments) => runner.RunAsync(tools.Ffmpeg, ["-v", "error", "-nostdin", "-y", .. arguments], cancellationToken: token);
        await Make("-f", "lavfi", "-i", "testsrc2=size=1920x1080", "-frames:v", "1", Path.Combine(tripA, "landscape photo.jpg"));
        await Make("-f", "lavfi", "-i", "testsrc2=size=1080x1920", "-frames:v", "1", Path.Combine(tripA, "portrait.jpg"));
        await Make("-f", "lavfi", "-i", "testsrc2=size=1000x1000", "-frames:v", "1", Path.Combine(tripB, "square photo with a much longer file name.png"));
        // The frame number as twelve cells across the top: white is a one, lowest bit on the left. Below it a picture that moves.
        var cell = $"(W/{BarcodeBits})";
        var barcode = $"lum='if(lt(Y,H/8),if(mod(floor(N/pow(2,floor(X/{cell}))),2),235,16),128+100*sin((X+N*8)/40)*cos(Y/30))':cb=128:cr=128";
        await Make("-f", "lavfi", "-i", $"nullsrc=size=1280x720:rate=30:duration=10,geq={barcode}", "-f", "lavfi", "-i", "sine=frequency=440:duration=10:sample_rate=48000",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "16", "-g", "30", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", Path.Combine(tripA, "counter video.mp4"));
        await Make("-f", "lavfi", "-i", "testsrc2=size=720x1280:rate=30:duration=4", "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", Path.Combine(tripA, "tall clip.mp4"));
        await Make("-f", "lavfi", "-i", "sine=frequency=330:duration=6:sample_rate=44100", Path.Combine(tripB, "song.wav"));
        // A folder with a full filmstrip of pictures, and a second folder for the workspace, to show what switching looks like.
        var tripC = Path.Combine(root, "Trip C");
        Directory.CreateDirectory(tripC);
        await Make("-f", "lavfi", "-i", "testsrc2=size=480x270:rate=30", "-frames:v", "120", Path.Combine(tripC, "shot_%03d.jpg"));
        var archive = Path.Combine(Path.GetDirectoryName(root)!, "probe-archive", "Scans");
        Directory.CreateDirectory(archive);
        await Make("-f", "lavfi", "-i", "mandelbrot=size=320x240:rate=10", "-frames:v", "60", Path.Combine(archive, "scan_%03d.jpg"));
    }

    /// <summary>A row of cells in the top-left corner: eight for the action number, eight for the drawn-frame counter.</summary>
    private static Rectangle[] AddCodeCells(MainWindow window)
    {
        var canvas = new Canvas { IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        Grid.SetRowSpan(canvas, 5);
        var cells = new Rectangle[CodeCells];
        for (var index = 0; index < CodeCells; index++)
        {
            cells[index] = new Rectangle { Width = CellSize, Height = CellSize, Fill = Brushes.Black };
            Canvas.SetLeft(cells[index], index * CellSize);
            canvas.Children.Add(cells[index]);
        }
        window.RootGrid.Children.Add(canvas);
        return cells;
    }

    private static Process StartRecording(ToolPaths tools, Int32Rect area, string output, string log)
    {
        var start = new ProcessStartInfo(tools.Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true };
        foreach (var argument in new[]
        {
            "-y", "-hide_banner", "-f", "lavfi", "-i",
            $"ddagrab=output_idx=0:framerate=60:draw_mouse=0:offset_x={area.X}:offset_y={area.Y}:video_size={area.Width}x{area.Height}",
            "-vf", "hwdownload,format=bgra", "-c:v", "libx264rgb", "-preset", "ultrafast", "-qp", "0", output
        })
            start.ArgumentList.Add(argument);
        var process = Process.Start(start)!;
        var writer = new StreamWriter(log) { AutoFlush = true };
        process.ErrorDataReceived += (_, args) => { if (args.Data is { } line) lock (writer) writer.WriteLine(line); };
        process.Exited += (_, _) => { lock (writer) writer.Dispose(); };
        process.EnableRaisingEvents = true;
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task StopRecordingAsync(Process ffmpeg)
    {
        try
        {
            await ffmpeg.StandardInput.WriteAsync('q');
            await ffmpeg.StandardInput.FlushAsync();
        }
        catch (IOException) { }
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await ffmpeg.WaitForExitAsync(wait.Token); }
        catch (OperationCanceledException) { ffmpeg.Kill(); }
    }

    /// <summary>On every frame WPF draws: bump the counter shown in the corner cells and write down every named element that moved, appeared or went away.</summary>
    private sealed class LayoutRecorder
    {
        private readonly MainWindow window;
        private readonly MainViewModel model;
        private readonly StreamWriter log;
        private readonly Rectangle[] cells;
        private readonly Stopwatch clock = new();
        private readonly Dictionary<string, Rect> previous = new();
        private int tick;
        private int action;
        private string label = "";

        public LayoutRecorder(MainWindow window, MainViewModel model, string path, Rectangle[] cells)
        {
            this.window = window;
            this.model = model;
            this.cells = cells;
            log = new StreamWriter(path, false, new UTF8Encoding(false));
        }

        public void Start()
        {
            clock.Start();
            model.PropertyChanged += OnPropertyChanged;
            CompositionTarget.Rendering += OnRendering;
        }

        public void Begin(int index, string name)
        {
            action = index;
            label = name;
            Write(new { kind = "action", t = clock.Elapsed.TotalMilliseconds, tick, action, label });
        }

        public void Stop()
        {
            CompositionTarget.Rendering -= OnRendering;
            model.PropertyChanged -= OnPropertyChanged;
            log.Dispose();
        }

        private void OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName is null or nameof(MainViewModel.Status)) return;
            object? value;
            try { value = typeof(MainViewModel).GetProperty(args.PropertyName)?.GetValue(model); }
            catch (Exception) { value = "?"; }
            var text = value switch
            {
                null => "null",
                string s => s,
                bool or int or double => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
                _ => value.GetType().Name
            };
            Write(new { kind = "prop", t = clock.Elapsed.TotalMilliseconds, tick, action, name = args.PropertyName, value = text.Length > 90 ? text[..90] : text });
        }

        private void OnRendering(object? sender, EventArgs args)
        {
            tick++;
            for (var bit = 0; bit < 8; bit++)
            {
                cells[bit].Fill = (action >> bit & 1) == 1 ? Brushes.White : Brushes.Black;
                cells[8 + bit].Fill = (tick >> bit & 1) == 1 ? Brushes.White : Brushes.Black;
            }
            // Template parts share names (every tab has a "TabBorder"), so repeats are numbered in tree order.
            var seen = new HashSet<string>();
            var counts = new Dictionary<string, int>();
            foreach (var element in Named(window))
            {
                if (!element.IsVisible || element.ActualWidth <= 0) continue;
                var occurrence = counts[element.Name] = counts.GetValueOrDefault(element.Name) + 1;
                var name = occurrence == 1 ? element.Name : $"{element.Name}#{occurrence}";
                seen.Add(name);
                Rect bounds;
                try { bounds = element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize)); }
                catch (InvalidOperationException) { continue; }
                bounds = new Rect(Math.Round(bounds.X, 1), Math.Round(bounds.Y, 1), Math.Round(bounds.Width, 1), Math.Round(bounds.Height, 1));
                if (previous.TryGetValue(name, out var before) && before == bounds) continue;
                Write(new { kind = previous.ContainsKey(name) ? "move" : "show", t = clock.Elapsed.TotalMilliseconds, tick, action, name, x = bounds.X, y = bounds.Y, w = bounds.Width, h = bounds.Height, dx = bounds.X - before.X, dy = bounds.Y - before.Y, dw = bounds.Width - before.Width, dh = bounds.Height - before.Height });
                previous[name] = bounds;
            }
            foreach (var gone in previous.Keys.Where(name => !seen.Contains(name)).ToArray())
            {
                Write(new { kind = "hide", t = clock.Elapsed.TotalMilliseconds, tick, action, name = gone });
                previous.Remove(gone);
            }
        }

        private void Write(object entry) => log.WriteLine(JsonSerializer.Serialize(entry));

        private static IEnumerable<FrameworkElement> Named(DependencyObject parent)
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                if (child is FrameworkElement { Name.Length: > 0 } element && child is not Rectangle)
                    yield return element;
                if (child is UIElement { Visibility: not Visibility.Visible })
                    continue;
                foreach (var nested in Named(child))
                    yield return nested;
            }
        }
    }
}
