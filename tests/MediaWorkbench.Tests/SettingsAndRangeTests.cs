using MediaWorkbench.Core;

namespace MediaWorkbench.Tests;

public sealed class SettingsAndRangeTests
{
    [Fact]
    public void SettingsRoundTripAndRejectRelativeExportFolders()
    {
        using var temporary = new TemporaryDirectory();
        var store = new SettingsStore(temporary.FilePath("settings.json"));
        var settings = new AppSettings { ExportDirectory = temporary.FilePath("exports"), CacheMegabytes = 128, LastLibrary = temporary.Path };
        store.Save(settings);
        Assert.Equal(settings, store.Load());
        Assert.Throws<InvalidDataException>(() => store.Save(settings with { ExportDirectory = "relative" }));
        Assert.Throws<InvalidDataException>(() => store.Save(settings with { CacheMegabytes = 1 }));
        Assert.Equal(settings, store.Load());
    }

    [Fact]
    public void RecentLibrariesAreBoundedDeduplicatedAndPersisted()
    {
        using var temporary = new TemporaryDirectory();
        var store = new SettingsStore(temporary.FilePath("settings.json"));
        var settings = new AppSettings { ExportDirectory = temporary.FilePath("exports") };
        for (var index = 0; index < AppSettings.RecentLibraryLimit + 3; index++)
            settings = settings.WithRecentLibrary(temporary.FilePath($"library{index}"));
        settings = settings.WithRecentLibrary(temporary.FilePath("library5") + Path.DirectorySeparatorChar);
        Assert.Equal(AppSettings.RecentLibraryLimit, settings.RecentLibraries.Length);
        Assert.Equal(temporary.FilePath("library5"), settings.RecentLibraries[0]);
        Assert.Equal(temporary.FilePath("library5"), settings.LastLibrary);
        Assert.Single(settings.RecentLibraries, path => path.EndsWith("library5", StringComparison.Ordinal));
        store.Save(settings);
        Assert.Equal(settings, store.Load());
        Assert.Throws<InvalidDataException>(() => store.Save(settings with { RecentLibraries = ["relative"] }));
    }

    [Fact]
    public void ExportHistoryRoundTripsNewestFirstAndSurvivesCorruption()
    {
        using var temporary = new TemporaryDirectory();
        var store = new JobHistoryStore(temporary.FilePath("history.json"));
        Assert.Empty(store.Load());
        var records = Enumerable.Range(0, JobHistoryStore.Capacity + 5)
            .Select(index => new ExportJobRecord($"Job {index}", index % 2 == 0 ? "Complete" : "Failed: x", temporary.FilePath($"out{index}.png"), DateTimeOffset.UnixEpoch.AddMinutes(index)))
            .ToArray();
        store.Save(records);
        var loaded = store.Load();
        Assert.Equal(JobHistoryStore.Capacity, loaded.Count);
        Assert.Equal("Job 204", loaded[0].Title);
        Assert.True(loaded[0].Succeeded);
        Assert.False(loaded[1].Succeeded);
        File.WriteAllText(temporary.FilePath("history.json"), "{ not json");
        Assert.Empty(store.Load());
    }

    [Fact]
    public void CorruptSettingsAreNotSilentlyOverwritten()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.FilePath("settings.json");
        File.WriteAllText(path, "bad json");
        Assert.Throws<System.Text.Json.JsonException>(() => new SettingsStore(path).Load());
        Assert.Equal("bad json", File.ReadAllText(path));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(2, 1)]
    [InlineData(0, 5)]
    public void InvalidFrameRangesFail(int start, int end) => Assert.Throws<ArgumentException>(() => new FrameRange(start, end).Validate(5));

    [Theory]
    [InlineData(-0.1, 1)]
    [InlineData(1, 1)]
    [InlineData(0, 20)]
    [InlineData(double.NaN, 1)]
    [InlineData(0, double.PositiveInfinity)]
    public void InvalidAudioRangesFail(double start, double end) => Assert.Throws<ArgumentException>(() => new TimeRange(start, end).Validate(10));

    [Fact]
    public void InclusiveVideoRangeUsesNextActualTimestampNotAverageFps()
    {
        VideoFrame[] frames = [new(0, 0, 0.1), new(1, 0.1, 0.2), new(2, 0.3, 0.4), new(3, 0.7, 0.3)];
        Assert.Equal(new TimeRange(0.1, 0.7), new FrameRange(1, 2).ToTimeRange(frames, 1));
        Assert.Equal(new TimeRange(0.7, 1), new FrameRange(3, 3).ToTimeRange(frames, 1));
    }

    [Fact]
    public void OutputNamesNeverOverwriteAndIncompleteFilesAreRemoved()
    {
        using var temporary = new TemporaryDirectory();
        string original;
        using (var first = OutputReservation.Create(temporary.Path, "sample", ".png"))
        {
            original = first.Path;
            File.WriteAllText(original, "keep this");
            first.Complete();
        }
        string incomplete;
        using (var second = OutputReservation.Create(temporary.Path, "sample", ".png"))
        {
            incomplete = second.Path;
            Assert.NotEqual(original, incomplete);
        }
        Assert.False(File.Exists(incomplete));
        Assert.Equal("keep this", File.ReadAllText(original));
    }

    [Fact]
    public void ConcurrentOutputReservationsAreUnique()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, 16, index =>
        {
            using var output = OutputReservation.Create(temporary.Path, "same-name", ".png");
            File.WriteAllText(output.Path, index.ToString());
            output.Complete();
            paths.Add(output.Path);
        });
        Assert.Equal(16, paths.Distinct().Count());
    }
}
