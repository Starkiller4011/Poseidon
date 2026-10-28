using Poseidon.Core.Modules;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Core.Tests")]

namespace Poseidon.Core.Runtime;

public sealed class ModuleRuntime : IModuleHost, IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly AsyncLocal<bool> invocationBlocked = new();
    private readonly string modulesDirectory;
    private readonly string cacheDirectory;
    private readonly Action shutdown;
    private readonly TimeSpan lifecycleTimeout;
    private readonly IReadOnlyList<ExtensionPoint> hostPoints;
    private readonly List<string> diagnostics = [];
    private readonly List<(WeakReference Context, string Directory)> retired = [];
    private readonly List<ModulePackage> quarantined = [];
    private readonly List<Task> pendingLifecycle = [];
    // Packages that failed in their current deployed form, keyed by package directory.
    private readonly Dictionary<string, (string Fingerprint, string Reason)> rejected = new(StringComparer.Ordinal);
    private List<ModulePackage> active = [];
    private IReadOnlyList<PackageStatus> packageReports = [];
    private string? lastInventory;
    private bool lastResult = true;
    private int forceRequested;
    private Dictionary<string, (string Provider, object Value, string Point)> features = new(StringComparer.Ordinal);
    private FileSystemWatcher? watcher;
    private Timer? timer;
    private bool disposed;
    private bool faulted;
    private long generation;
    private int reloadRequested;
    private int started;
    private int disposalRequested;
    private RuntimeStatus status = new(0, [], [], []);
    public RuntimeStatus Status => Volatile.Read(ref status);
    internal WeakReference[] RetiredContexts => retired.Select(r => r.Context).ToArray();
    internal string CacheDirectory => cacheDirectory;

    public ModuleRuntime(string? modulesDirectory = null, Action? shutdown = null, TimeSpan? lifecycleTimeout = null,
        IEnumerable<ExtensionPoint>? additionalHostPoints = null)
    {
        hostPoints = [.. ExtensionPoints.Host, .. additionalHostPoints ?? []];
        this.modulesDirectory = Path.GetFullPath(
            modulesDirectory ?? Path.Combine(AppContext.BaseDirectory, "modules"));
        cacheDirectory = Path.Combine(Path.GetTempPath(), "Poseidon", Guid.NewGuid().ToString("N"));
        this.shutdown = shutdown ?? (() => { });
        this.lifecycleTimeout = lifecycleTimeout ?? TimeSpan.FromSeconds(10);
        if (this.lifecycleTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lifecycleTimeout));
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref started, 1) != 0)
            throw new InvalidOperationException("The module watcher has already started.");
        ObjectDisposedException.ThrowIf(disposed, this);
        Directory.CreateDirectory(modulesDirectory);
        timer = new Timer(_ => Schedule(force: false), null, Timeout.Infinite, Timeout.Infinite);
        watcher = new FileSystemWatcher(modulesDirectory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                           NotifyFilters.LastWrite | NotifyFilters.Size
        };
        watcher.Changed += OnPackageChanged;
        watcher.Created += OnPackageChanged;
        watcher.Deleted += OnPackageChanged;
        watcher.Renamed += OnPackageChanged;
        watcher.Error += (_, e) =>
        {
            Report($"Module watcher error; scheduling rescan: {e.GetException().Message}");
            Schedule(force: false);
        };
        watcher.EnableRaisingEvents = true;
        // Periodic reconciliation also recovers from missed/overflowed watcher events.
        timer.Change(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        await ReloadAsync(cancellationToken);
    }

    private void OnPackageChanged(object sender, FileSystemEventArgs e)
    {
        if (Volatile.Read(ref disposalRequested) != 0)
            return;
        try
        {
            timer?.Change(TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(5));
        }
        catch (ObjectDisposedException)
        {
            // A watcher callback already in progress can race with timer disposal.
        }
    }

    // Explicit user/module request: also retries packages that previously failed.
    public void RequestReload() => Schedule(force: true);

    private void Schedule(bool force)
    {
        if (Volatile.Read(ref disposalRequested) != 0)
            return;
        if (force)
            Interlocked.Exchange(ref forceRequested, 1);
        if (Interlocked.Exchange(ref reloadRequested, 1) != 0)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                do { await ReconcileAsync(Interlocked.Exchange(ref forceRequested, 0) == 1, CancellationToken.None); }
                while (Volatile.Read(ref forceRequested) == 1 && Volatile.Read(ref disposalRequested) == 0);
            }
            finally { Interlocked.Exchange(ref reloadRequested, 0); }
        });
    }
    public void RequestShutdown() => shutdown();

    public async Task<TResult> InvokeAsync<TFeature, TResult>(
        string featureId, Func<TFeature, TResult> action, CancellationToken cancellationToken = default)
        where TFeature : class
    {
        if (invocationBlocked.Value)
            throw new InvalidOperationException("Feature calls cannot be nested or made from lifecycle methods.");
        await gate.WaitAsync(cancellationToken);
        try
        {
            invocationBlocked.Value = true;
            ObjectDisposedException.ThrowIf(disposed, this);
            if (faulted || !features.TryGetValue(featureId, out var entry))
                throw new InvalidOperationException($"Feature is unavailable: {featureId}");
            if (entry.Value is not TFeature feature)
                throw new InvalidOperationException($"Feature contract mismatch: {featureId}");
            return action(feature);
        }
        finally
        {
            invocationBlocked.Value = false;
            gate.Release();
        }
    }

    // Explicit reloads also retry packages that previously failed; automatic reconciliation only
    // reacts to deployment changes so a broken package is not restarted every few seconds.
    public Task<bool> ReloadAsync(CancellationToken cancellationToken = default)
        => ReconcileAsync(force: true, cancellationToken);

    // Returns true only when every complete package that was discovered is running.
    private async Task<bool> ReconcileAsync(bool force, CancellationToken cancellationToken)
    {
        if (!await gate.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken))
        {
            Report("Reload deferred: a feature operation or lifecycle transition is still in progress.");
            return false;
        }
        var loaded = new List<ModulePackage>();
        var committed = new HashSet<ModulePackage>(ReferenceEqualityComparer.Instance);
        var attempted = new List<ModulePackage>();
        var quarantine = false;
        var oldStopped = false;
        try
        {
            if (disposed)
                return false;
            Reap();
            if (faulted)
            {
                Publish();
                return false;
            }
            var scan = Scan();
            var inventory = string.Join("\n", scan.Select(e =>
                $"{e.Directory}|{(e.Ready ? e.Fingerprint ?? "error:" + e.Error : "pending")}"));
            if (inventory == lastInventory && !(force && !lastResult))
            {
                Publish();
                return lastResult;
            }
            if (force)
                rejected.Clear();
            foreach (var stale in rejected.Keys.Where(k => scan.All(e => e.Directory != k)).ToArray())
                rejected.Remove(stale);

            var problems = new Dictionary<string, string>(StringComparer.Ordinal);
            var pending = new HashSet<string>(StringComparer.Ordinal);
            var success = true;
            foreach (var entry in scan)
            {
                var current = active.FirstOrDefault(p => p.Source.Directory == entry.Directory);
                string problem;
                if (!entry.Ready)
                {
                    if (current is null)
                    {
                        pending.Add(entry.Directory);
                        continue;
                    }
                    problem = "Deployment in progress: the .ready marker was removed.";
                }
                else if (entry.Fingerprint is null)
                    problem = entry.Error ?? "Package cannot be read.";
                else if (rejected.TryGetValue(entry.Directory, out var prior) && prior.Fingerprint == entry.Fingerprint)
                    problem = prior.Reason;
                else
                {
                    try
                    {
                        var candidate = ModulePackage.Load(ModulePackage.Inspect(entry.Directory), cacheDirectory, OnLoadFailure);
                        // Package-local declaration failures are handled like load failures so the running
                        // version can be retained instead of being stopped and replaced by nothing.
                        var declarationProblem = ModuleGraph.ValidateDeclarations(candidate);
                        if (declarationProblem is not null)
                        {
                            Retire([candidate]);
                            throw new InvalidDataException(declarationProblem);
                        }
                        loaded.Add(candidate);
                        continue;
                    }
                    catch (Exception ex)
                    {
                        problem = Describe(ex);
                        rejected[entry.Directory] = (entry.Fingerprint, problem);
                    }
                }
                success = false;
                problems[entry.Directory] = problem;
                if (current is not null)
                    Retain(current, loaded, problems);
            }

            var startFailures = new Dictionary<ModulePackage, Exclusion>(ReferenceEqualityComparer.Instance);
            Dictionary<string, ModuleState?>? states = null;
            Exception? lastStartFailure = null;
            while (true)
            {
                var plan = ModuleGraph.Plan(loaded, hostPoints, startFailures);
                if (SameSet(plan.Ordered, active))
                {
                    if (oldStopped)
                        await RollbackAsync(states!, lastStartFailure!);
                    return Complete(plan, scan, problems, pending, inventory, success);
                }

                if (states is null)
                {
                    states = new Dictionary<string, ModuleState?>(StringComparer.Ordinal);
                    foreach (var package in active)
                    {
                        var state = await LifecycleAsync($"capture {package.Source.Manifest.Id}",
                            token => package.Instance.CaptureStateAsync(token), cancellationToken);
                        states.Add(package.Source.Manifest.Id,
                            state is null ? null : new ModuleState(state.SchemaVersion, state.Data.ToArray()));
                    }
                }
                if (!oldStopped)
                {
                    // A global generation barrier deliberately trades concurrency for a safe first implementation.
                    foreach (var package in active.AsEnumerable().Reverse())
                    {
                        try
                        {
                            await LifecycleAsync($"stop {package.Source.Manifest.Id}",
                                package.Instance.StopAsync, CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            faulted = true;
                            features.Clear();
                            throw new InvalidOperationException(
                                $"Cannot safely quiesce {package.Source.Manifest.Id}; host recovery is required.", ex);
                        }
                    }
                    oldStopped = true;
                }

                attempted.Clear();
                ModulePackage? failed = null;
                foreach (var package in plan.Ordered)
                {
                    attempted.Add(package);
                    states.TryGetValue(package.Source.Manifest.Id, out var state);
                    try
                    {
                        await LifecycleAsync($"start {package.Source.Manifest.Id}",
                            token => package.Instance.StartAsync(this, state, token), cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        failed = package;
                        lastStartFailure = ex;
                        break;
                    }
                }
                if (failed is null)
                {
                    Retire(active);
                    active = plan.Ordered;
                    committed.UnionWith(active);
                    features = plan.Features;
                    generation++;
                    var result = Complete(plan, scan, problems, pending, inventory, success);
                    Reap();
                    return result;
                }

                Report($"Module {failed.Source.Manifest.Id} failed to start from {failed.Source.Directory}: {lastStartFailure}");
                foreach (var package in attempted.AsEnumerable().Reverse())
                    await StopForRecoveryAsync(package);
                if (faulted)
                {
                    quarantine = true;
                    features.Clear();
                    throw new InvalidOperationException("Candidate activation failed; see recovery diagnostics.",
                        lastStartFailure);
                }
                // Exclude only the failing module (its dependents become blocked) and plan again.
                success = false;
                var reason = $"Start failed: {Describe(lastStartFailure!)}";
                startFailures[failed] = new Exclusion(PackageState.Failed, reason);
                if (failed.Source.ContentDirectory == failed.Source.Directory)
                {
                    rejected[failed.Source.Directory] = (failed.Source.Fingerprint, reason);
                    problems[failed.Source.Directory] = reason;
                    var current = active.FirstOrDefault(p => p.Source.Directory == failed.Source.Directory);
                    if (current is not null)
                        Retain(current, loaded, problems);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || oldStopped || faulted)
        {
            // This is the plugin fault boundary: never publish success after a rejected generation.
            if (faulted)
                features.Clear();
            Report($"Module reconciliation rejected: {ex}");
            Publish();
            return false;
        }
        finally
        {
            var unused = loaded.Where(p => !committed.Contains(p)).ToList();
            if (quarantine)
            {
                quarantined.AddRange(unused);
                Report("Candidate modules quarantined after unsafe lifecycle failure; restart is required.");
            }
            else
                Retire(unused);
            gate.Release();
        }
    }

    private void OnLoadFailure(WeakReference context, string directory) => retired.Add((context, directory));

    // Keeps the running version of a package available when its redeployment is incomplete or broken.
    private void Retain(ModulePackage current, List<ModulePackage> loaded, Dictionary<string, string> problems)
    {
        var directory = current.Source.Directory;
        if (loaded.Any(p => p.Source.Directory == directory && p.Source.Fingerprint == current.Source.Fingerprint))
            return;
        try
        {
            loaded.Add(ModulePackage.Load(current.Source with { ContentDirectory = current.ShadowDirectory },
                cacheDirectory, OnLoadFailure));
        }
        catch (Exception ex)
        {
            problems[directory] += $" The last-known-good version could not be retained: {Describe(ex)}";
        }
    }

    private async Task RollbackAsync(Dictionary<string, ModuleState?> states, Exception activationError)
    {
        foreach (var package in active)
        {
            try
            {
                await LifecycleAsync($"rollback {package.Source.Manifest.Id}",
                    token => package.Instance.StartAsync(this, states[package.Source.Manifest.Id], token),
                    CancellationToken.None);
            }
            catch (Exception rollbackError)
            {
                faulted = true;
                Report($"Rollback failed for {package.Source.Manifest.Id}: {rollbackError}");
                break;
            }
        }
        if (faulted)
        {
            features.Clear();
            throw new InvalidOperationException("Candidate activation failed; see recovery diagnostics.", activationError);
        }
    }

    private bool Complete(GraphPlan plan, List<ScanEntry> scan, Dictionary<string, string> problems,
        HashSet<string> pending, string inventory, bool success)
    {
        var reports = new List<PackageStatus>();
        foreach (var entry in scan)
        {
            var directory = entry.Directory;
            var running = plan.Ordered.FirstOrDefault(p => p.Source.Directory == directory);
            var excluded = plan.Excluded.Where(e => e.Key.Source.Directory == directory)
                .OrderBy(e => e.Key.Source.ContentDirectory == directory ? 0 : 1).Select(e => (KeyValuePair<ModulePackage, Exclusion>?)e)
                .FirstOrDefault();
            var manifest = running?.Source.Manifest ?? excluded?.Key.Source.Manifest;
            PackageStatus report;
            if (running is not null && running.Source.ContentDirectory != directory)
                report = new(directory, manifest!.Id, manifest.Version, PackageState.Retained,
                    $"{problems.GetValueOrDefault(directory)} Running the last-known-good version.".Trim());
            else if (running is not null)
                report = new(directory, manifest!.Id, manifest.Version, PackageState.Active);
            else if (pending.Contains(directory))
                report = new(directory, null, null, PackageState.Pending, "Waiting for the .ready completion marker.");
            else if (problems.TryGetValue(directory, out var problem))
                report = new(directory, manifest?.Id, manifest?.Version, PackageState.Failed,
                    excluded is { } retainedExclusion && retainedExclusion.Key.Source.ContentDirectory != directory
                        ? $"{problem} The last-known-good version is also unavailable: {retainedExclusion.Value.Reason}"
                        : problem);
            else if (excluded is { } exclusion)
                report = new(directory, manifest!.Id, manifest.Version, exclusion.Value.State, exclusion.Value.Reason);
            else
                continue;
            reports.Add(report);
            if (report.State is PackageState.Failed or PackageState.Blocked or PackageState.Retained)
                Report($"{report.State}: {report.ModuleId ?? Path.GetFileName(directory)} ({directory}): {report.Detail}");
        }
        packageReports = reports;
        lastInventory = inventory;
        lastResult = success && plan.Excluded.Count == 0;
        Publish();
        return lastResult;
    }

    private static bool SameSet(List<ModulePackage> candidate, List<ModulePackage> current)
        => candidate.Count == current.Count && candidate.All(c => current.Any(p =>
            p.Source.Directory == c.Source.Directory && p.Source.Fingerprint == c.Source.Fingerprint));

    private sealed record ScanEntry(string Directory, bool Ready, string? Fingerprint, string? Error);

    private List<ScanEntry> Scan()
    {
        // A missing directory simply means that no modules are installed.
        if (!Directory.Exists(modulesDirectory))
            return [];
        var entries = new List<ScanEntry>();
        foreach (var directory in Directory.EnumerateDirectories(modulesDirectory).Order(StringComparer.Ordinal))
        {
            if (!File.Exists(Path.Combine(directory, ".ready")))
            {
                entries.Add(new(directory, false, null, null));
                continue;
            }
            try { entries.Add(new(directory, true, ModulePackage.Fingerprint(directory), null)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                entries.Add(new(directory, true, null, ex.Message));
            }
        }
        return entries;
    }

    private static string Describe(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
            if (!messages.Contains(current.Message))
                messages.Add(current.Message);
        return string.Join(" ", messages);
    }

    private async ValueTask StopForRecoveryAsync(ModulePackage package)
    {
        if (pendingLifecycle.Any(t => !t.IsCompleted))
        {
            faulted = true;
            Report($"Cannot stop {package.Source.Manifest.Id} while timed-out lifecycle work is still running.");
            return;
        }
        try
        {
            await LifecycleAsync($"cleanup {package.Source.Manifest.Id}",
                package.Instance.StopAsync, CancellationToken.None);
        }
        catch (Exception ex)
        {
            faulted = true;
            Report($"Cleanup failed for {package.Source.Manifest.Id}: {ex}");
        }
    }

    private async Task LifecycleAsync(string operation, Func<CancellationToken, ValueTask> action,
        CancellationToken cancellationToken)
        => await LifecycleAsync(operation, async token =>
        {
            await action(token);
            return true;
        }, cancellationToken);

    private async Task<T> LifecycleAsync<T>(string operation, Func<CancellationToken, ValueTask<T>> action,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(lifecycleTimeout);
        var token = deadline.Token;
        var task = Task.Run(async () =>
        {
            invocationBlocked.Value = true;
            try { return await action(token); }
            finally { invocationBlocked.Value = false; }
        }, CancellationToken.None);
        try { return await task.WaitAsync(lifecycleTimeout, cancellationToken); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            if (!task.IsCompleted)
            {
                faulted = true;
                pendingLifecycle.Add(task);
                _ = task.ContinueWith(t => Report($"Late lifecycle failure ({operation}): {t.Exception}"),
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
            throw new InvalidOperationException($"Lifecycle interrupted during {operation}.", ex);
        }
    }

    private void Retire(IEnumerable<ModulePackage> packages)
    {
        foreach (var package in packages)
        {
            retired.Add((package.ContextReference, package.ShadowDirectory));
            package.Dispose();
        }
    }

    private void Reap()
    {
        foreach (var item in retired.ToArray())
        {
            if (item.Context.IsAlive)
                continue;
            try
            {
                Directory.Delete(item.Directory, recursive: true);
                retired.Remove(item);
            }
            catch (IOException ex) { Report($"Shadow cleanup pending: {ex.Message}"); }
            catch (UnauthorizedAccessException ex) { Report($"Shadow cleanup denied: {ex.Message}"); }
        }
    }

    private void Report(string message)
    {
        lock (diagnostics)
        {
            diagnostics.Add(message);
            if (diagnostics.Count > 100)
                diagnostics.RemoveAt(0);
        }
        Console.Error.WriteLine(message);
    }

    private void Publish()
    {
        string[] messages;
        lock (diagnostics) messages = diagnostics.ToArray();
        var next = new RuntimeStatus(generation,
            faulted ? [] : active.Select(p => new ModuleStatus(p.Source.Manifest.Id,
                p.Source.Manifest.Version, p.Source.Directory)).ToArray(),
            features.Select(f => new FeatureStatus(f.Key, f.Value.Provider, f.Value.Point)).ToArray(), messages)
        {
            RetainedLoadContexts = retired.Count(r => r.Context.IsAlive),
            Packages = disposed ? [] : faulted
                ? packageReports.Select(p => p.State is PackageState.Active or PackageState.Retained
                    ? p with { State = PackageState.Failed, Detail = "Unavailable after an unsafe lifecycle failure; restart required." }
                    : p).ToArray()
                : packageReports
        };
        Volatile.Write(ref status, next);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposalRequested, 1) != 0)
            return;
        watcher?.Dispose();
        if (timer is not null)
            await timer.DisposeAsync();
        if (!await gate.WaitAsync(lifecycleTimeout))
        {
            Interlocked.Exchange(ref disposalRequested, 0);
            throw new TimeoutException("Cannot shut down modules while a feature operation is still running.");
        }
        try
        {
            if (disposed)
                return;
            disposed = true;
            foreach (var package in active.AsEnumerable().Reverse())
                await StopForRecoveryAsync(package);
            foreach (var package in quarantined.AsEnumerable().Reverse())
                await StopForRecoveryAsync(package);
            features.Clear();
            if (pendingLifecycle.Any(t => !t.IsCompleted))
                Report("Lifecycle code is still running; its contexts cannot be safely unloaded. Process restart required.");
            else
            {
                Retire(active);
                Retire(quarantined);
            }
            active.Clear();
            quarantined.Clear();
            Reap();
            Publish();
        }
        finally { gate.Release(); }
    }
}
