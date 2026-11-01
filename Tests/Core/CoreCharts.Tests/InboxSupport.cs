using System.Diagnostics;
using Poseidon.Core.Modules;
using Poseidon.Modules.Charts;
using Poseidon.Modules.Charts.Inbox;
using Poseidon.Modules.Charts.Store;
using Xunit;

namespace CoreCharts.Tests;

internal sealed class RecordingLog : ILog
{
    private readonly List<(LogLevel Level, string Message, Exception? Exception)> entries = [];

    public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries
    {
        get
        {
            lock (entries)
                return entries.ToList();
        }
    }

    public void Log(LogLevel level, string message, Exception? exception = null)
    {
        lock (entries)
            entries.Add((level, message, exception));
    }

    public string Describe() => string.Join(Environment.NewLine, Entries.Select(e => $"{e.Level} {e.Message}"));
}

// Supplies the Charts log; nothing else of the runtime is needed by the module's lifecycle.
internal sealed class ChartsHost : IModuleHost
{
    public RecordingLog Log { get; } = new();

    public List<string> LogCategories { get; } = [];

    public RuntimeStatus Status { get; } = new(0, [], [], []);

    public void RequestReload()
    {
    }

    public void RequestShutdown()
    {
    }

    public Task<TResult> InvokeAsync<TFeature, TResult>(
        string featureId, Func<TFeature, TResult> action, CancellationToken cancellationToken = default)
        where TFeature : class
        => throw new InvalidOperationException($"Feature is unavailable: {featureId}");

    public ILog GetLog(string category)
    {
        lock (LogCategories)
            LogCategories.Add(category);
        return Log;
    }
}

internal static class Inboxes
{
    // Copies an exchange set (the S100_ROOT folder at source) to inbox/relativeRoot, which becomes the set's root.
    public static string AddSet(string inbox, string relativeRoot, string source)
    {
        var root = Path.Combine(inbox, relativeRoot);
        CopyDirectory(source, root);
        return root;
    }

    public static void CopyDirectory(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    public static ChartInbox Create(IChartLibrary library, string chartsDirectory, ILog? log = null)
        => new(library, new ChartInboxOptions
        {
            InboxPath = Path.Combine(chartsDirectory, "inbox"),
            RecordPath = Path.Combine(chartsDirectory, "inbox.json"),
            Log = log
        });

    public static ChartLibrary Library(string chartsDirectory) => Libraries.Create(chartsDirectory);

    public static InboxSetRecord Record(ChartInbox inbox, string relativePath)
        => Assert.Single(inbox.Records, r => r.RelativePath == relativePath);

    public static async Task<ChartsModule> Start(string chartsDirectory, ChartsHost host)
    {
        var module = new ChartsModule(chartsDirectory);
        await ((IModule)module).StartAsync(host, null, CancellationToken.None);
        return module;
    }

    public static ChartLibrary LibraryOf(ChartsModule module)
        => Assert.IsType<ChartLibrary>(module.Features[ChartPoints.LibraryFeatureId]);
}

// Runs a target of the CoreCharts project in a child `dotnet msbuild`, as a build would.
internal static class Staging
{
    public static string Project => Path.Combine(Fixtures.RepoRoot, "System", "Core", "CoreCharts", "CoreCharts.csproj");

    // hostDirectory stands in for the host's output folder: modules deploy to hostDirectory/modules and the
    // inbox is hostDirectory/charts/inbox.
    public static (int ExitCode, string Output) Run(string configuration, string hostDirectory,
        params (string Name, string Value)[] properties)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Fixtures.RepoRoot
        };
        foreach (var argument in new[]
                 {
                     "msbuild", Project, "-t:StageDevelopmentCharts", "-nologo", "-nodeReuse:false", "-v:n",
                     $"-p:Configuration={configuration}",
                     $"-p:ModuleDeploymentDirectory={Path.Combine(hostDirectory, "modules")}"
                 }.Concat(properties.Select(p => $"-p:{p.Name}={p.Value}")))
            start.ArgumentList.Add(argument);
        // The test host may carry the outer build's MSBuild settings; the child build resolves its own.
        foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("MSBUILD", StringComparison.OrdinalIgnoreCase)).ToList())
            start.Environment.Remove(name);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(TimeSpan.FromMinutes(5)), "dotnet msbuild did not finish within 5 minutes");
        return (process.ExitCode, output.Result + error.Result);
    }

    public static string Inbox(string hostDirectory) => Path.Combine(hostDirectory, "charts", "inbox");

    // The SHOM sets below this folder are staged in the ordinal order of their paths, which is edition order.
    public static string ShomSource => Path.Combine(Fixtures.LocalUkhoS101, "S-101_FR_June26", "S-101_FR_June26",
        "Shom Data 22 June 2026", "S-101");
}
