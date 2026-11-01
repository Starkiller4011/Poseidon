using Poseidon.Core.Modules;
using Poseidon.Modules.Charts.Inbox;
using Xunit;

namespace CoreCharts.Tests;

// One Debug staging run into a stand-in host folder, shared by the tests that read what it staged.
public sealed class StagedHost : IDisposable
{
    private readonly TempCopy host = new();

    public StagedHost() => (ExitCode, Output) = Staging.Run("Debug", host.Root);

    public string Root => host.Root;

    public string Inbox => Staging.Inbox(host.Root);

    public int ExitCode { get; }

    public string Output { get; }

    public void Dispose() => host.Dispose();
}

// CI10-CI12: the Debug-only target that stages development chart data into the host's inbox.
public sealed class ChartStagingTests(StagedHost staged) : IClassFixture<StagedHost>
{
    // CI10
    [Fact]
    public void DebugStaging_ExtractsThePowerUpSetIntoTheInbox()
    {
        Assert.True(staged.ExitCode == 0, staged.Output);
        var root = Assert.Single(Directory.EnumerateDirectories(Path.Combine(staged.Inbox, "S-164-PowerUp"), "S100_ROOT",
            SearchOption.AllDirectories));
        AssertSameFiles(Fixtures.ReleasedPowerUp, root);
    }

    // CI10: each SHOM set is staged flat as NN-<set folder>/S100_ROOT, numbered in source-path order, so the
    // sets keep their edition order and every path stays well inside Windows' 260-character limit.
    [LocalDataFact]
    public void DebugStaging_CopiesTheLocalUkhoAndShomSets()
    {
        Assert.True(staged.ExitCode == 0, staged.Output);
        AssertSameFiles(Fixtures.LocalUkhoGb, Path.Combine(staged.Inbox, "UKHO-GB-Apr26", "S100_ROOT"));
        var shom = Path.Combine(staged.Inbox, "SHOM-FR-June26");
        var sources = Directory.EnumerateDirectories(Staging.ShomSource, "S100_ROOT", SearchOption.AllDirectories)
            .OrderBy(r => Path.GetRelativePath(Staging.ShomSource, r).Replace('\\', '/'), StringComparer.Ordinal).ToList();
        Assert.Equal(4, sources.Count);
        var names = sources.Select((root, i) => $"{i + 1:00}-{Path.GetFileName(Path.GetDirectoryName(root))}").ToList();
        Assert.Equal(names, Directory.EnumerateDirectories(shom).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        for (var i = 0; i < sources.Count; i++)
            AssertSameFiles(sources[i], Path.Combine(shom, names[i], "S100_ROOT"));
        Assert.All(Directory.EnumerateFiles(staged.Inbox, "*", SearchOption.AllDirectories),
            f => Assert.True(Path.GetRelativePath(staged.Inbox, f).Length <= 130, $"Staged path too long: {f}"));
    }

    // CI10
    [Fact]
    public void DebugStaging_WithoutTheLocalData_SkipsItWithAMessage()
    {
        using var host = new TempCopy();
        var missing = Path.Combine(host.Root, "no-such-data");

        var (exitCode, output) = Staging.Run("Debug", host.Root,
            ("DevelopmentChartsUkhoSource", missing), ("DevelopmentChartsShomSource", missing));

        Assert.True(exitCode == 0, output);
        Assert.Contains(missing, output, StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.Combine(Staging.Inbox(host.Root), "S-164-PowerUp")));
        Assert.False(Directory.Exists(Path.Combine(Staging.Inbox(host.Root), "UKHO-GB-Apr26")));
        Assert.False(Directory.Exists(Path.Combine(Staging.Inbox(host.Root), "SHOM-FR-June26")));
    }

    // CI10: a staged file held open (no write sharing) would fail the run if it were copied again.
    [Fact]
    public void DebugStaging_RunAgain_DoesNotCopyUnchangedFiles()
    {
        Assert.True(staged.ExitCode == 0, staged.Output);
        var handles = Directory.EnumerateFiles(staged.Inbox, "*", SearchOption.AllDirectories)
            .Select(f => new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read)).ToList();
        try
        {
            var (exitCode, output) = Staging.Run("Debug", staged.Root);

            Assert.True(exitCode == 0, output);
        }
        finally
        {
            handles.ForEach(h => h.Dispose());
        }
    }

    // CI11
    [Fact]
    public void ReleaseStaging_StagesNothing()
    {
        using var host = new TempCopy();

        var (exitCode, output) = Staging.Run("Release", host.Root);

        Assert.True(exitCode == 0, output);
        Assert.False(Directory.Exists(Path.Combine(host.Root, "charts")));
    }

    // CI10 and CI11 as the real build leaves them: the host output of this test run's own configuration.
    [Fact]
    public void HostBuild_StagesTheInboxOnlyInDebug()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var inbox = Path.Combine(Fixtures.RepoRoot, "System", "Core", "Core", "bin", configuration, "net10.0", "charts", "inbox");

        if (configuration == "Debug")
            Assert.True(File.Exists(Path.Combine(inbox, "S-164-PowerUp", "S100_ROOT", "CATALOG.XML")), $"Nothing staged in {inbox}");
        else
            Assert.False(Directory.Exists(inbox) && Directory.EnumerateFiles(inbox, "*", SearchOption.AllDirectories).Any());
    }

    // CI12
    [Fact]
    public async Task StagedInbox_ImportsEverythingAtStartupAndNothingAtTheNextStart()
    {
        Assert.True(staged.ExitCode == 0, staged.Output);
        using var charts = new TempCopy();
        Inboxes.CopyDirectory(staged.Inbox, Path.Combine(charts.Root, "inbox"));
        var expectedCells = 6 + (Fixtures.HasLocalUkhoData ? 27 + 2 : 0);

        var module = await Inboxes.Start(charts.Root, new ChartsHost());
        await module.StartupScan!.WaitAsync(TimeSpan.FromMinutes(5));
        var library = Inboxes.LibraryOf(module);
        Assert.Equal(expectedCells, library.Current.Cells.Count);
        if (Fixtures.HasLocalUkhoData)
        {
            foreach (var name in new[] { "101FR00369400", "101FR00571300" })
                Assert.Equal((2, 1), (Libraries.Cell(library.Current, name).Edition, Libraries.Cell(library.Current, name).Update));
        }
        var records = Inboxes.Create(library, charts.Root).Records;
        Assert.All(records, r => Assert.Equal((InboxSetState.Completed, 0), (r.State, r.Errors)));
        var generation = library.Current.Generation;
        await ((IModule)module).StopAsync(CancellationToken.None);

        var restarted = await Inboxes.Start(charts.Root, new ChartsHost());
        await restarted.StartupScan!.WaitAsync(TimeSpan.FromMinutes(5));

        var restartedLibrary = Inboxes.LibraryOf(restarted);
        Assert.Equal(generation, restartedLibrary.Current.Generation);
        Assert.Equal(expectedCells, restartedLibrary.Current.Cells.Count);
        Assert.Equal(Canonical.Of(records), Canonical.Of(Inboxes.Create(restartedLibrary, charts.Root).Records));
        await ((IModule)restarted).StopAsync(CancellationToken.None);
    }

    private static void AssertSameFiles(string expectedRoot, string actualRoot)
    {
        static List<string> Files(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToList();
        var expected = Files(expectedRoot);
        Assert.Equal(expected, Files(actualRoot));
        Assert.All(expected, f => Assert.Equal(File.ReadAllBytes(Path.Combine(expectedRoot, f)), File.ReadAllBytes(Path.Combine(actualRoot, f))));
    }
}
