using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MediaWorkbench.Core;
using SkiaSharp;

namespace MediaWorkbench.Avalonia.Tests;

/// <summary>Suggestions while typing a tag, choosing from the tags in use, and picking several files to tag them together.</summary>
public sealed class TaggingTests : IDisposable
{
    private readonly string workspace = Path.Combine(Path.GetTempPath(), "MediaWorkbench.Avalonia.Tests", "tagging-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(workspace, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void OnlyTheTagBeingTypedIsCompletedAndTheOnesBeforeItAreKept()
    {
        Assert.Equal("cli", TagCompletion.Current("sunset,  cli"));
        Assert.True(TagCompletion.Matches("sunset, cli", "client A"));
        Assert.True(TagCompletion.Matches("ENT", "client A"));
        Assert.False(TagCompletion.Matches("sunset, ", "client A"), "Nothing is offered before a letter is typed.");
        Assert.False(TagCompletion.Matches("client A, cli", "client A"), "A tag already in the box is not offered again.");
        Assert.False(TagCompletion.Matches("client a", "client A"), "A tag typed out in full needs no suggestion.");
        Assert.Equal("sunset, client A, ", TagCompletion.Complete("sunset, cli", "client A"));
        Assert.Equal("client A, ", TagCompletion.Complete("cli", "client A"));
    }

    private async Task<(MainViewModel Model, MainWindow Window, AssetViewModel[] Files)> OpenAsync(bool withVideo = false)
    {
        var root = Path.Combine(workspace, "shoot");
        Directory.CreateDirectory(root);
        if (withVideo)
            await new ProcessRunner().RunAsync(ToolPaths.Resolve().Ffmpeg, ["-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=10:duration=4",
                "-c:v", "libx264", "-g", "10", "-pix_fmt", "yuv420p", Path.Combine(root, "clip.mp4")]);
        for (var index = 1; index <= 6; index++)
        {
            using var bitmap = new SKBitmap(32, 18);
            using (var canvas = new SKCanvas(bitmap)) canvas.Clear(new SKColor((byte)(index * 40), 90, 160));
            using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(root, $"a{index}.png"), png.ToArray());
        }
        // Settings first, so nothing this test does can reach the user's own export folder.
        var data = Path.Combine(workspace, "data");
        Directory.CreateDirectory(data);
        new SettingsStore(Path.Combine(data, "settings.json")).Save(new AppSettings { ExportDirectory = Path.Combine(workspace, "exports") });
        var model = new MainViewModel(data);
        var window = new MainWindow(model) { Width = 1500, Height = 940 };
        window.Show();
        // Awaited, never blocked on: the test runs on the window's own thread.
        await model.OpenLibraryAsync(root);
        model.InspectorTab = 1;
        Settle(window);
        var files = Enumerable.Range(1, 6).Select(index => model.Assets.Single(item => item.Name == $"a{index}.png")).ToArray();
        return (model, window, files);
    }

    private static void Settle(Window window)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private static async Task Until(Window window, Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail(message);
            Settle(window);
            await Task.Delay(20);
        }
    }

    /// <summary>Clicks a thumbnail where a person would, with Ctrl or Shift held if asked.</summary>
    private static void ClickThumbnail(MainWindow window, AssetViewModel item, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        Settle(window);
        window.Filmstrip.ScrollIntoView(item);
        Settle(window);
        var container = window.Filmstrip.ContainerFromItem(item) ?? throw new InvalidOperationException("Not on screen: " + item.Name);
        var card = container.GetVisualDescendants().OfType<Border>().First(border => border.Classes.Contains("thumbCard"));
        var point = card.TranslatePoint(new Point(card.Bounds.Width / 2, card.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left, modifiers);
        window.MouseUp(point, MouseButton.Left, modifiers);
        Settle(window);
    }

    /// <summary>With MEDIAWORKBENCH_KEEP_CHECKS set, keeps a picture of the window for looking at by eye.</summary>
    private static void Picture(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("MEDIAWORKBENCH_KEEP_CHECKS") is null) return;
        Settle(window);
        var folder = Path.Combine(Path.GetTempPath(), "MediaWorkbench.Avalonia.Tests", "tagging-pictures");
        Directory.CreateDirectory(folder);
        if (window.CaptureRenderedFrame() is { } frame)
            File.WriteAllBytes(Path.Combine(folder, name + ".png"), Pixels.EncodePng(frame));
    }

    private static string[] TagsOf(MainViewModel model, AssetViewModel item) =>
        model.Assets.Single(each => ReferenceEquals(each, item)).Tags;

    [AvaloniaFact]
    public async Task SeveralPickedFilesAreTaggedTogetherAndPartlySharedTagsCanBeFinished()
    {
        var (model, window, files) = await OpenAsync();
        ClickThumbnail(window, files[0]);
        await Until(window, () => model.SelectedAsset == files[0] && !model.IsPreviewBusy, "A plain click should open the file.");
        model.TagText = "client A";
        model.AddTagsCommand.Execute(null);
        Assert.Contains("client A", TagsOf(model, files[0]));
        Assert.Equal("THIS FILE", model.TagTargetTitle);

        // Ctrl+click picks without opening; Shift+click picks the run from the last pick.
        ClickThumbnail(window, files[1], RawInputModifiers.Control);
        ClickThumbnail(window, files[2], RawInputModifiers.Control);
        Assert.Same(files[0], model.SelectedAsset);
        Assert.Equal(2, model.PickedCount);
        ClickThumbnail(window, files[4], RawInputModifiers.Shift);
        Assert.Equal(new[] { "a2.png", "a3.png", "a4.png", "a5.png" }, model.PickedAssets.Select(item => item.Name).Order());
        Assert.Equal("4 PICKED FILES", model.TagTargetTitle);
        Assert.True(window.PickBar.IsVisible && !model.ShowFileTagSections);

        // Typing in the tag box and pressing Enter tags every picked file, and only those.
        window.TagBox.Focus();
        model.TagText = "job 7";
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Settle(window);
        Assert.All(files[1..5], item => Assert.Contains("job 7", TagsOf(model, item)));
        Assert.DoesNotContain("job 7", TagsOf(model, files[0]));
        Assert.Equal("", model.TagText);

        // With the open file picked too, tags on only some of them are listed with how many, and + finishes them.
        ClickThumbnail(window, files[0], RawInputModifiers.Control);
        Assert.Equal(5, model.PickedCount);
        var client = model.PartialTags.Single(share => share.Tag == "client A");
        Assert.Equal("1 of 5", client.CountText);
        Assert.Equal("4 of 5", model.PartialTags.Single(share => share.Tag == "job 7").CountText);
        model.AddSharedTagToAllCommand.Execute(client);
        Assert.Contains("client A", model.SelectedTags);
        Assert.All(files[..5], item => Assert.Contains("client A", TagsOf(model, item)));

        // Choosing from the tags in use: a half-lit tag goes on all of them, a lit one comes off all of them.
        model.ShowTagChoices = true;
        Settle(window);
        Picture(window, "picked-with-choices");
        var job = model.TagChoices.Single(choice => choice.Tag == "job 7");
        Assert.True(job.IsOnSome);
        model.ToggleTagChoiceCommand.Execute(job);
        Assert.True(model.TagChoices.Single(choice => choice.Tag == "job 7").IsOnAll);
        model.ToggleTagChoiceCommand.Execute(model.TagChoices.Single(choice => choice.Tag == "job 7"));
        Assert.All(files, item => Assert.DoesNotContain("job 7", TagsOf(model, item)));
        Assert.DoesNotContain("job 7", model.KnownTags);

        // Esc lets go of the picks; the Tags tab is back on the open file.
        // The last Ctrl+click left the keyboard on the filmstrip, as a click would.
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Settle(window);
        Assert.False(model.HasPicks);
        Assert.Equal("THIS FILE", model.TagTargetTitle);
        Assert.True(files.All(item => !item.IsPicked));
        window.Close();
    }

    [AvaloniaFact]
    public async Task PicksHiddenByASearchAreLetGoSoNothingUnseenIsTagged()
    {
        var (model, window, files) = await OpenAsync();
        ClickThumbnail(window, files[1], RawInputModifiers.Control);
        ClickThumbnail(window, files[5], RawInputModifiers.Control);
        model.SearchText = "a6";
        await Until(window, () => model.LibraryView.Count == 1, "The search should leave one file.");
        Assert.Equal(new[] { "a6.png" }, model.PickedAssets.Select(item => item.Name));
        Assert.False(files[1].IsPicked);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheTagBoxSuggestsTagsInUseAsYouTypeAndKeepsTheTagsBefore()
    {
        var (model, window, files) = await OpenAsync();
        ClickThumbnail(window, files[0]);
        await Until(window, () => model.SelectedAsset == files[0], "A plain click should open the file.");
        model.TagText = "client A, sunset";
        model.AddTagsCommand.Execute(null);
        ClickThumbnail(window, files[1]);
        await Until(window, () => model.SelectedAsset == files[1], "Another file should open.");

        window.TagBox.Focus();
        Settle(window);
        window.KeyTextInput("review, cli");
        await Until(window, () => window.TagBox.IsDropDownOpen, "Typing part of a tag in use should open the suggestions.");
        Assert.Equal(new[] { "client A" }, window.TagBox.ItemsSource!.Cast<string>().Where(tag => TagCompletion.Matches(window.TagBox.Text, tag)));
        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Settle(window);
        Assert.Equal("review, client A, ", model.TagText);
        Assert.Empty(TagsOf(model, files[1]));

        // With no list open, Enter adds what is in the box.
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Settle(window);
        Assert.Equal(new[] { "client A", "review" }, TagsOf(model, files[1]).Order());
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheBarOverTheFilmstripTagsFileAfterFileWithoutLeavingIt()
    {
        var (model, window, files) = await OpenAsync();
        Assert.Equal("PNG", files[0].KindBadge);
        ClickThumbnail(window, files[0]);
        await Until(window, () => model.SelectedAsset == files[0] && !model.IsPreviewBusy, "A plain click should open the file.");

        // T, type, Enter: the open file is tagged and the keyboard is back on the filmstrip.
        window.KeyPress(Key.T, RawInputModifiers.None, PhysicalKey.T, "t");
        Settle(window);
        window.KeyTextInput("client A");
        Settle(window);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Settle(window);
        Assert.Equal(new[] { "client A" }, TagsOf(model, files[0]));
        Assert.Equal("", model.QuickTagText);
        Assert.True(window.Filmstrip.IsKeyboardFocusWithin, "Enter in the bar should hand the keyboard back to the filmstrip.");

        // Right goes on to the next file; the bar offers the folder's tag, and one click puts it on.
        window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        await Until(window, () => model.SelectedAsset == files[1], "Right should open the next file.");
        var chip = model.FolderBarTags.Single();
        Assert.Equal(("client A", 1, false), (chip.Tag, chip.Uses, chip.IsOnAll));
        var button = window.FolderTagBar.GetVisualDescendants().OfType<Button>().Single(each => each.DataContext is TagChoice choice && choice.Tag == "client A");
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Settle(window);
        Assert.Equal(new[] { "client A" }, TagsOf(model, files[1]));
        Picture(window, "folder-bar");
        Assert.Equal((2, true), (model.FolderBarTags.Single().Uses, model.FolderBarTags.Single().IsOnAll));
        // On a filled bubble every word is in the dark ink, the count included.
        Settle(window);
        var filled = window.FolderTagBar.GetVisualDescendants().OfType<Button>().Single(each => each.DataContext is TagChoice { Tag: "client A" });
        Assert.All(filled.GetVisualDescendants().OfType<TextBlock>(), text => Assert.Equal(((ISolidColorBrush)TagColors.Ink).Color, ((ISolidColorBrush)text.Foreground!).Color));
        window.Close();
    }

    [Fact]
    public void EveryTagHasItsOwnSteadyMattePastel()
    {
        var tags = Enumerable.Range(1, 30).Select(index => $"tag {index}").Append("client A").Append("job 7").ToList();
        var colors = tags.Select(TagColors.Of).ToList();
        Assert.Equal(colors.Count, colors.Distinct().Count());
        Assert.Equal(TagColors.Of("client A"), TagColors.Of("CLIENT a"));
        foreach (var color in colors)
        {
            var hsl = color.ToHsl();
            Assert.InRange(hsl.L, 0.75, 0.9);
            Assert.InRange(hsl.S, 0.35, 0.6);
        }
    }

    [AvaloniaFact]
    public async Task TheThumbnailSizeSliderFitsItsRow()
    {
        var (_, window, _) = await OpenAsync();
        var slider = window.ThumbnailSizeControl.GetVisualDescendants().OfType<Slider>().Single();
        var row = (Control)window.ThumbnailSizeControl.GetVisualParent()!.GetVisualParent()!;
        var top = slider.TranslatePoint(new Point(0, 0), row)!.Value.Y;
        Assert.True(top >= -0.5 && top + slider.Bounds.Height <= row.Bounds.Height + 0.5, $"The slider ({top:0.#} to {top + slider.Bounds.Height:0.#}) should sit inside its {row.Bounds.Height:0.#}-high row.");
        window.Close();
    }

    [AvaloniaFact]
    public async Task RestingOnAVideoPlaysFivePicturesFromItsStartToItsEnd()
    {
        var (model, window, _) = await OpenAsync(withVideo: true);
        var video = model.Assets.Single(item => item.Name == "clip.mp4");
        Assert.Equal("MP4", video.KindBadge);
        window.Filmstrip.ScrollIntoView(video);
        Settle(window);
        var card = window.Filmstrip.ContainerFromItem(video)!.GetVisualDescendants().OfType<Border>().First(border => border.Classes.Contains("thumbCard"));
        var point = card.TranslatePoint(new Point(card.Bounds.Width / 2, card.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point);
        await Until(window, () => window.HoverState.Count == MainViewModel.HoverFrameCount && video.HoverFrame is not null, "Resting on a video should bring up its five pictures.");
        var first = video.HoverFrame!;
        await Until(window, () => !ReferenceEquals(video.HoverFrame, first), "The pictures should play in turn.");
        Assert.Equal(1, window.HoverState.Step);
        Assert.NotEqual(Pixels.Bytes(first, out _), Pixels.Bytes(video.HoverFrame!, out _));

        // Moving off it stops the preview and the thumbnail comes back.
        window.MouseMove(new Point(5, 5));
        Settle(window);
        Assert.Null(video.HoverFrame);
        window.Close();
    }

    private static void Click(Window window, Control control)
    {
        Settle(window);
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Settle(window);
    }

    [AvaloniaFact]
    public async Task TheTagFilterTicksSeveralTagsMatchesAnyOrAllAndClears()
    {
        var (model, window, files) = await OpenAsync();
        // a1 and a2 carry client A; a2 and a3 carry job 7.
        foreach (var (item, tags) in new[] { (files[0], "client A"), (files[1], "client A, job 7"), (files[2], "job 7") })
        {
            model.SelectedAsset = item;
            await Until(window, () => !model.IsPreviewBusy, "The file should open.");
            model.TagText = tags;
            model.AddTagsCommand.Execute(null);
        }
        window.TaggedFilesSection.IsExpanded = true;
        Assert.Equal("Choose tags…", model.TagFilterLabel);

        // The drop-down lists every tag with its count; ticking boxes narrows the filmstrip.
        Click(window, window.TagFilterButton);
        var list = (StackPanel)((Flyout)window.TagFilterButton.Flyout!).Content!;
        await Until(window, () => list.GetVisualDescendants().OfType<CheckBox>().Count() == 2, "The drop-down should list both tags.");
        CheckBox Box(string tag) => list.GetVisualDescendants().OfType<CheckBox>().Single(box => ((TagFilterOption)box.DataContext!).Tag == tag);
        Assert.Equal(2, ((TagFilterOption)Box("job 7").DataContext!).Files);
        Box("client A").IsChecked = true;
        Settle(window);
        Assert.Equal(new[] { "a1.png", "a2.png" }, model.LibraryView.Select(item => item.Name));
        Box("job 7").IsChecked = true;
        Settle(window);
        Assert.Equal(3, model.LibraryView.Count);
        Assert.Equal("2 tags (any)", model.TagFilterLabel);
        Picture(window, "tag-filter-open");
        ((Flyout)window.TagFilterButton.Flyout!).Hide();
        Picture(window, "tag-filter-closed");

        // All of them: only the file carrying both.
        Assert.True(window.TagMatchChoice.IsVisible);
        Click(window, window.MatchAllTagsButton);
        Assert.True(model.MatchAllTags);
        Assert.Equal(new[] { "a2.png" }, model.LibraryView.Select(item => item.Name));
        Click(window, window.MatchAllTagsButton);
        Assert.True(model.MatchAllTags, "Clicking the chosen one again keeps it chosen.");

        // A ticked tag's bubble × unticks just that one, in the drop-down too.
        var bubble = window.FilterTagBubbles.GetVisualDescendants().OfType<Button>().Single(button => (string?)button.CommandParameter == "job 7");
        Click(window, bubble);
        Assert.Equal(new[] { "client A" }, model.FilterTags);
        Assert.False(model.HasSeveralFilterTags);
        Assert.Equal(new[] { "a1.png", "a2.png" }, model.LibraryView.Select(item => item.Name));

        // An Overview tag ticks the same filter; Clear lets everything go.
        model.ToggleOverviewTagCommand.Execute(model.OverviewTags.Single(tag => tag.Tag == "job 7"));
        Assert.Equal(new[] { "client A", "job 7" }, model.FilterTags);
        Click(window, window.ClearTagFilterButton);
        Assert.False(model.HasFilterTags);
        Assert.Equal(6, model.LibraryView.Count);
        Click(window, window.TagFilterButton);
        await Until(window, () => list.GetVisualDescendants().OfType<CheckBox>().Count() == 2, "The drop-down should open again.");
        Assert.All(list.GetVisualDescendants().OfType<CheckBox>(), box => Assert.False(box.IsChecked));
        window.Close();
    }
}
