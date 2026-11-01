# M1-I: Module logging

Milestone: M1-I (new; depends on M1-A)
Status: Awaiting code approval

## Problem and scope

Today there is no logging. The host has only two ad hoc mechanisms, both
informal: `ModuleRuntime.Report` appends a short string to an in-memory,
100-entry `diagnostics` list (surfaced in `RuntimeStatus.Diagnostics`) and
also writes it to `Console.Error`; `App.axaml.cs` writes two of its own
startup/shutdown failures straight to `Console.Error`. No module
(`CoreUI`, `CoreDisplay`, `CoreTracks`, `CoreAIS`, `CoreRadar`, `CoreSonar`,
`CoreCharts`, `CoreNavigation`) has any logging facility at all today. This
gap is more pressing now that M1-H (CoreUI extensibility) removes CoreUI's
own diagnostics panel (H12) — the one place a developer could previously
see `RuntimeStatus.Diagnostics` without a debugger attached.

This feature gives every module (and the host itself) a shared logging
facility:

1. A minimal, framework-independent logging abstraction in
   `Poseidon.Contracts`, so every module can log without pulling in a
   logging package of its own.
2. A way for a module to get a logger scoped to its own module ID, usable
   from anywhere — including `StartAsync`/`StopAsync`, where regular
   feature calls (`IModuleHost.InvokeAsync`) are rejected.
3. A host-level config flag that raises the minimum level to `Debug`
   (otherwise `Information` and above only), read once at startup.
4. Output to both the console and a log file, so a deployed console with
   no one watching the terminal still has something to look at after the
   fact.

**Non-goals.** Log file rotation/retention, structured/machine-parseable
output (JSON lines, etc.), shipping logs anywhere external (a log
aggregator, telemetry service), and per-module (rather than one, global)
verbosity control are all out of scope for this pass. CMS modules are out
of scope (CMS backlog, not started).

## Design

### The abstraction

A new, minimal interface in `Poseidon.Contracts`, independent of any
logging framework (the same "stay framework-independent" rule
`IChartViewport.CreateView` already follows for Avalonia, applied here to
logging packages instead):

```csharp
public enum LogLevel { Trace, Debug, Information, Warning, Error }

public interface ILog
{
    void Log(LogLevel level, string message, Exception? exception = null);
}

// Convenience members with default implementations, so a real
// implementation only has to provide Log itself.
public static class LogExtensions
{
    public static void Trace(this ILog log, string message) => log.Log(LogLevel.Trace, message);
    public static void Debug(this ILog log, string message) => log.Log(LogLevel.Debug, message);
    public static void Information(this ILog log, string message) => log.Log(LogLevel.Information, message);
    public static void Warning(this ILog log, string message, Exception? exception = null) => log.Log(LogLevel.Warning, message, exception);
    public static void Error(this ILog log, string message, Exception? exception = null) => log.Log(LogLevel.Error, message, exception);
}
```

### Getting a logger

`IModuleHost` gains a direct method, the same shape as `RequestReload`/
`RequestShutdown`/`RegisterShortcut` (a plain host capability, not routed
through `InvokeAsync`, so it is not subject to "feature calls are rejected
inside lifecycle methods" and works in `StartAsync`/`StopAsync` too):

```csharp
public interface IModuleHost
{
    // ...existing members...

    // category is free text; CoreUI's convention (and every Core module's)
    // is its own module ID ("Core.UI", "Core.Tracks", etc.), so log lines
    // are attributable without the logger needing to know about modules.
    ILog GetLog(string category);
}
```

### Configuration and output

Core reads a host-level `logging.json` (next to the host executable, the
same place `console.json`/`sensors.json` live next to their own module,
but this one belongs to the host since it is not any one module's
setting) at startup:

```json
{ "minimumLevel": "Debug" }
```

Unlike `console.json` (H1: missing is rejected outright, since display
mode has no safe default), a missing or malformed `logging.json` is **not**
fatal: it falls back to `Information`, since logging is a diagnostic aid,
not something that should be able to stop the application from starting.
`minimumLevel` accepts `Trace`, `Debug`, `Information`, `Warning`, or
`Error`; the "config flag for debug-level logging" is this setting with
`Debug` (or `Trace`) selected.

Every log entry below the configured minimum level is dropped; everything
else is written, with a timestamp, the category, and the level, to both
the console and a log file (`logs/poseidon-<date>.log`, next to the host
executable, created on first write; no rotation in this pass — see
non-goals). A write failure (disk full, permission denied) is swallowed,
never thrown back at the caller: a logging problem must not crash or block
the application it is trying to help diagnose.

`ModuleRuntime.Report` and `App.axaml.cs`'s two direct
`Console.Error.WriteLine` calls are routed through this same facility
(category `"Core"`), so there is one real destination for "what got
logged," not two. `RuntimeStatus.Diagnostics` (the capped 100-entry list
UI/tests already read) is unchanged — it keeps recording the same strings
it does today, just no longer by writing to `Console.Error` directly
itself.

### Switching to debug logging from Visual Studio

`logging.json` alone means editing a file by hand to flip levels, which is
too slow for "I just found a bug, let me turn on debug logging and
reproduce it." An environment variable, `POSEIDON_LOG_LEVEL`, takes the
same level names as `logging.json`'s `minimumLevel` and overrides it when
set to a recognized value — read at the same startup point, so neither
source needs to know about the other beyond "the environment variable
wins if present." `System/Core/Core/Properties/launchSettings.json` (new;
none exists today) then gives Visual Studio's run/debug target dropdown
two profiles that differ only in that variable:

```json
{
  "profiles": {
    "Poseidon": { "commandName": "Project" },
    "Poseidon (Debug logging)": {
      "commandName": "Project",
      "environmentVariables": { "POSEIDON_LOG_LEVEL": "Debug" }
    }
  }
}
```

Picking "Poseidon (Debug logging)" from the dropdown and running is then
the whole workflow; `logging.json` is untouched and still governs every
run (including a deployed one, with no Visual Studio and no environment
variable set) that does not override it.

### Alternatives considered

- **`Microsoft.Extensions.Logging.Abstractions`** instead of a custom
  `ILog`. It is a lightweight, widely used package with broader tooling
  compatibility (structured sinks, categories, scopes) if this ever needs
  to feed an external aggregator. Not chosen for this pass, consistent
  with the project's existing zero-logging-dependency style (today's
  `Console.Error.WriteLine` calls use nothing at all) — but flagged as
  open question 1 below, since it is a real fork with lasting API-shape
  consequences for every module.
- **A static/ambient logger** (e.g. a global `Log.For(category)`) instead
  of `IModuleHost.GetLog`. Rejected: it works around the same
  "no feature calls in lifecycle methods" problem a direct `IModuleHost`
  method already solves without introducing global mutable state, and
  keeps the logger's lifetime tied to the host like everything else a
  module is given.

## Requirements

| ID | Requirement |
|---|---|
| I1 | `Poseidon.Contracts` declares `ILog`/`LogLevel` and `IModuleHost.GetLog(string category)`; `Poseidon.Contracts` pulls in no logging-framework package (B12's "stay framework-independent" rule, applied here) |
| I2 | `GetLog` and the returned `ILog`'s `Log` method both work when called from `StartAsync`/`StopAsync`, unlike `InvokeAsync` |
| I3 | Core reads `logging.json` at startup for `minimumLevel`; a missing or malformed file, or an unrecognized level, falls back to `Information` rather than failing startup |
| I4 | A log call below the configured minimum level produces no output at all |
| I5 | Each emitted entry includes a timestamp, its category, and its level |
| I6 | Each emitted entry is written to both the console and a log file (`logs/poseidon-<date>.log`, next to the host executable) |
| I7 | A failure writing the log file (e.g. permission denied) is swallowed; logging never throws back at the caller |
| I8 | `ModuleRuntime.Report` and `App.axaml.cs`'s direct `Console.Error.WriteLine` calls are routed through the same facility instead, under category `"Core"` |
| I9 | `RuntimeStatus.Diagnostics`'s existing content and 100-entry cap are unchanged by this feature |
| I10 | Logging an exception (`Warning`/`Error` with an `Exception` argument) includes the exception's full detail (message and stack trace) in the emitted entry |
| I11 | The `POSEIDON_LOG_LEVEL` environment variable, when set to a recognized level, overrides `logging.json`'s `minimumLevel` for that run; when unset (or unrecognized), `logging.json` (or its own `Information` fallback) applies unchanged |
| I12 | `System/Core/Core/Properties/launchSettings.json` offers at least two Visual Studio run profiles, "Poseidon" and "Poseidon (Debug logging)", differing only in whether `POSEIDON_LOG_LEVEL=Debug` is set — verified manually (IDE launch configuration, not something an xUnit test exercises), not by an automated test |

## Decisions

Confirmed by the user, 2026-11-01:

- A custom `ILog` abstraction (not `Microsoft.Extensions.Logging.Abstractions`),
  to keep every module's dependency footprint free of a third-party
  logging package — stated reason: cross-platform compatibility.
- Both console and file output; log files are wanted for development as
  much as for the final product, not just as a deployed-console fallback.
- `logging.json` is the config-file mechanism.
- New: two Visual Studio run targets (launch profiles) — Normal and
  Debug — so switching to debug-level logging while chasing a bug is a
  one-click dropdown choice, not a file edit.

## Open questions

None remaining. Ready for the Step 1 gate.

## Ledger

| Requirement | Test | Failing run (Step 2) | Passing run (Step 3) |
|---|---|---|---|
| I1 | `ModuleRuntimeTests.GetLog_WorksFromStartAsyncAndStopAsync_WhereFeatureCallsAreRejected`, [Core.Tests](../../../../Tests/Core/Core.Tests/ModuleLoggingTests.cs) (compiles against the real `ILog`/`LogLevel`/`GetLog` signatures; no logging package referenced anywhere) | `Assert.True` failure — `ReloadAsync()` returns `false` (`GetLog`'s `NotImplementedException` fails the fixture's `StartAsync`), 2026-11-01 | Passed, 2026-11-01 |
| I2 | Same test as I1 | Same failure, 2026-11-01 | Passed, 2026-11-01 |
| I3 | `LoggingConfigurationTests.ResolveMinimumLevel_WithNoFileAndNoOverride_DefaultsToInformation`, `_WithAValidFile_UsesItsMinimumLevel`, `_WithAMalformedOrUnrecognizedFile_FallsBackToInformation_NeverThrows`, [Core.Tests](../../../../Tests/Core/Core.Tests/LoggingConfigurationTests.cs) | `NotImplementedException` from the stub, 2026-11-01 | Passed, 2026-11-01 |
| I4 | `LogSinkTests.Write_BelowTheMinimumLevel_ProducesNoOutputAtAll`, [Core.Tests](../../../../Tests/Core/Core.Tests/LogSinkTests.cs) | `NotImplementedException` from the stub, 2026-11-01 | Passed, 2026-11-01 |
| I5 | `LogSinkTests.Write_AtOrAboveTheMinimumLevel_AppearsOnTheConsoleAndInTheFile`, `Write_IncludesATimestamp` | `NotImplementedException`, 2026-11-01 | Passed, 2026-11-01 |
| I6 | `LogSinkTests.Write_AtOrAboveTheMinimumLevel_AppearsOnTheConsoleAndInTheFile` | `NotImplementedException`, 2026-11-01 | Passed, 2026-11-01 |
| I7 | `LogSinkTests.Write_WhenTheFileCannotBeWritten_StillWritesTheConsoleAndDoesNotThrow` | `NotImplementedException`, 2026-11-01 | Passed, 2026-11-01 |
| I8 | `ModuleRuntimeTests.Report_RoutesThroughTheSameLoggingFacility_NotJustConsoleError`, [Core.Tests](../../../../Tests/Core/Core.Tests/ModuleLoggingTests.cs). The `App.axaml.cs` half of this requirement has no existing automated test harness for the `App` class (none exists today) and is verified by code review in Step 3, not an xUnit test | `Assert.Single` failure — no log file is ever created (`Report` does not call `GetLog` yet), 2026-11-01 | Passed, 2026-11-01 |
| I9 | No new test; guarded by every existing test that already asserts on `RuntimeStatus.Diagnostics` (e.g. `ModuleLifecycleTests.InvalidFeatureValue_IsReportedWithoutActivation`), all still passing, since nothing in this feature touches that code path | N/A (regression guard, not new behavior) | Still passes unchanged, 2026-11-01 |
| I10 | `LogSinkTests.Write_WithAnException_IncludesItsMessageAndStackTrace` | `NotImplementedException`, 2026-11-01 | Passed, 2026-11-01 |
| I11 | `LoggingConfigurationTests.ResolveMinimumLevel_WithAnEnvironmentOverride_TakesPrecedenceOverTheFile`, `_WithAnUnrecognizedEnvironmentOverride_FallsBackToTheFile` | `NotImplementedException`, 2026-11-01 | Passed, 2026-11-01 |
| I12 | None (IDE launch configuration; verified manually in Step 3, not by an automated test — see Requirements) | N/A | Verified by reading `launchSettings.json`, 2026-11-01 |

All new [Core.Tests](../../../../Tests/Core/Core.Tests) cases (18: 11
`LoggingConfigurationTests` theory cases, 5 `LogSinkTests`, 2
`ModuleLoggingTests`) were run on Windows, 2026-11-01, and confirmed
failing for the stated reason, not from a test-setup bug. `dotnet build
Poseidon.slnx`: no warnings or errors. `dotnet test Poseidon.slnx
-maxCpuCount:1`: `Core.Tests` is the only project reporting "Test Run
Failed" (141 cases: 123 pre-existing, unaffected; 18 new, all failing as
above); every other project reports "Test Run Successful" with zero
failures, including every other Core module's own test project.

**Production additions this step are compile-only, with no behavior**,
per the development-workflow rule: `ILog`/`LogLevel`/`LogExtensions` are
real (no behavior to stub); `IModuleHost.GetLog` defaults to
`throw new NotImplementedException()` (so every existing `IModuleHost`
test double keeps compiling unchanged); `LoggingConfiguration.ResolveMinimumLevel`
and `LogSink.Write` are the same kind of stub; `ModuleRuntime` gained an
unused `logDirectory` constructor parameter (`LogDirectory` internal
property) for Step 3 to wire up, and `GetLog`/`Report` are themselves
untouched. `FixtureModule` (a test fixture, not production code) gained a
real `UseLogDuringStartAndStop` flag so I1/I2 can be exercised through a
real deployed module's lifecycle methods.

### Step 3 (code), 2026-11-01

Implemented for real: `LoggingConfiguration.ResolveMinimumLevel` (checks
`POSEIDON_LOG_LEVEL` first, then `logging.json`, then `Information`,
matching the ledger's I3/I11 test expectations exactly); `LogSink.Write`
(level filtering, timestamp/category/level formatting, console then file,
exception detail appended, file-write failures swallowed). `ModuleRuntime`
now builds one `LogSink` at construction (minimum level resolved from
`AppContext.BaseDirectory`'s `logging.json`/the environment variable; log
file at `logDirectory/poseidon-<date>.log`, `logDirectory` defaulting to
a `logs` folder next to the host executable, created eagerly) and
implements `GetLog` for real; `Report` now calls
`GetLog("Core").Warning(message)` instead of `Console.Error.WriteLine`
directly (I8; `RuntimeStatus.Diagnostics` itself, I9, is untouched).
`App.axaml.cs`'s two failure paths now call `GetLog("Core").Error(...)`
the same way, verified by reading the diff rather than an automated test
(no test harness exists for the `App` class, as already noted for I8/I9 in
Step 2). New `System/Core/Core/Properties/launchSettings.json` with the
"Poseidon" and "Poseidon (Debug logging)" profiles (I12), verified by
reading its JSON rather than an automated test, consistent with how the
plan always scoped I12.

**One deliberate behavior note**: `Report`'s diagnostics used to go to
`Console.Error`; they now go through `LogSink`, which always writes to
`Console.Out` (uniformly for every level, by design — nothing in this
feature distinguishes streams by severity). This is an intentional,
in-scope consequence of "routed through the same facility instead" (I8's
own wording), not an accidental regression.

**Final passing run (Windows, 2026-11-01):** `dotnet build Poseidon.slnx`
and `dotnet build Core.Application.proj`: no warnings or errors. All 18
new `Core.Tests` cases pass. The full `Core.Tests` project: 141/141.
`dotnet test Poseidon.slnx -maxCpuCount:1`: every project "Test Run
Successful", zero failures solution-wide.

## Status and follow-ups

Step 1 (plan) and Step 2 (tests) approved. Step 3 (code) done: every
requirement I1 to I12 is implemented (I9 unchanged by design, I12 verified
manually) and its test passes; the full solution
(`dotnet test Poseidon.slnx -maxCpuCount:1`) is green with zero failures,
2026-11-01. Awaiting the user's acceptance to close out M1-I. Known
follow-ups, explicitly out of scope for this pass: log file
rotation/retention; structured/machine-parseable output; shipping logs to
an external aggregator; per-module (rather than global) verbosity; CMS
modules (backlog, not started).
