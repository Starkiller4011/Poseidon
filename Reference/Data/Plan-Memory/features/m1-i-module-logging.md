# M1-I: Module logging

Milestone: M1-I (new; depends on M1-A)
Status: Awaiting plan approval

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

## Status and follow-ups

Planning, ready to present for the Step 1 gate. Known follow-ups,
explicitly out of scope for this pass: log file rotation/retention;
structured/machine-parseable output; shipping logs to an external
aggregator; per-module (rather than global) verbosity; CMS modules
(backlog, not started).
