using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Poseidon.Core.Modules;

namespace Poseidon.Tests.Fixtures;

public sealed record FixtureSettings(string Id, string Text, bool FailStart, bool FailStop, int StartDelayMilliseconds,
    string FeatureId, string FeatureMode, bool FailCapture, bool FailRollback, bool InvokeOnStart, string? EventLog,
    bool UseLogDuringStartAndStop);

public sealed class FixtureModule : IModule, ITextFeature
{
    private readonly FixtureSettings settings = ReadSettings();
    private int calls;
    private int starts;
    private static FixtureSettings ReadSettings()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "fixture.json")));
        var root = document.RootElement;
        return new FixtureSettings(root.GetProperty("Id").GetString()!, root.GetProperty("Text").GetString()!,
            root.GetProperty("FailStart").GetBoolean(), root.GetProperty("FailStop").GetBoolean(),
            root.GetProperty("StartDelayMilliseconds").GetInt32(),
            root.TryGetProperty("FeatureId", out var featureId) ? featureId.GetString()! : "test.text",
            root.TryGetProperty("FeatureMode", out var mode) ? mode.GetString()! : "shared",
            root.TryGetProperty("FailCapture", out var capture) && capture.GetBoolean(),
            root.TryGetProperty("FailRollback", out var rollback) && rollback.GetBoolean(),
            root.TryGetProperty("InvokeOnStart", out var invoke) && invoke.GetBoolean(),
            root.TryGetProperty("EventLog", out var log) ? log.GetString() : null,
            root.TryGetProperty("UseLogDuringStartAndStop", out var useLog) && useLog.GetBoolean());
    }
    public IReadOnlyList<ModuleCapability> Capabilities => [];
    public IReadOnlyDictionary<string, object> Features => new Dictionary<string, object>
    {
        [settings.FeatureId] = settings.FeatureMode switch
        {
            "null" => null!,
            "private" => new object(),
            _ => this
        }
    };
    public string Text => $"{settings.Text}:{++calls}";

    public async ValueTask StartAsync(IModuleHost host, ModuleState? state, CancellationToken cancellationToken)
    {
        starts++;
        Log("start");
        Host = host;
        // M1-I (I1, I2): proves GetLog works from a lifecycle method, where InvokeAsync is rejected.
        if (settings.UseLogDuringStartAndStop)
            host.GetLog(settings.Id).Information("starting");
        if (settings.StartDelayMilliseconds > 0)
            await Task.Delay(settings.StartDelayMilliseconds, CancellationToken.None);
        if (settings.FailStart)
            throw new InvalidOperationException("Fixture activation failure");
        if (settings.Text == "native-dependency")
            NativeProbe();
        if (settings.FailRollback && starts > 1)
            throw new InvalidOperationException("Fixture rollback failure");
        if (settings.InvokeOnStart)
            await host.InvokeAsync<ITextFeature, string>(settings.FeatureId, f => f.Text, cancellationToken);
        if (state is not null)
        {
            if (state.SchemaVersion != 1)
                throw new InvalidDataException("Unsupported fixture state");
            calls = BitConverter.ToInt32(state.Data);
        }
    }

    public ValueTask<ModuleState?> CaptureStateAsync(CancellationToken cancellationToken)
    {
        Log("capture");
        if (settings.FailCapture)
            throw new InvalidOperationException("Fixture capture failure");
        return ValueTask.FromResult<ModuleState?>(new ModuleState(1, BitConverter.GetBytes(calls)));
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        Log("stop");
        if (settings.UseLogDuringStartAndStop)
            Host?.GetLog(settings.Id).Information("stopping");
        if (settings.FailStop)
            throw new InvalidOperationException("Fixture stop failure");
        return ValueTask.CompletedTask;
    }

    // StopAsync does not receive the host; StartAsync stashes it so StopAsync can also prove GetLog
    // works there (I2), the same as it does in StartAsync.
    internal IModuleHost? Host { get; private set; }

    private void Log(string operation)
    {
        if (settings.EventLog is not null)
            File.AppendAllText(settings.EventLog, $"{settings.Id}:{operation}\n");
    }

    [DllImport("Poseidon.Unsupported.Native")]
    private static extern void NativeProbe();
}
