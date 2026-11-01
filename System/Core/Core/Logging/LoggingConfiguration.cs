using Poseidon.Core.Modules;

namespace Poseidon.Core.Runtime.Logging;

// M1-I (I3, I11): resolves the minimum log level once at startup, next to the host executable (the
// same "found next to its own assembly" precedent as CoreRadar's and CoreUI's own hardware
// configuration files) — except this one belongs to the host, not any one module, since logging is
// not a module-specific concern. Unlike console.json (H1), a missing or malformed logging.json (or an
// unrecognized environment override) is never fatal: logging is a diagnostic aid, not something that
// should be able to stop the application starting.
internal static class LoggingConfiguration
{
    public const string FileName = "logging.json";
    public const string EnvironmentVariable = "POSEIDON_LOG_LEVEL";
    public const LogLevel DefaultLevel = LogLevel.Information;

    // environmentOverride is the raw value read from EnvironmentVariable by the caller (ModuleRuntime),
    // passed in rather than read here, so this stays a pure function tests can drive without touching
    // real process environment variables.
    public static LogLevel ResolveMinimumLevel(string hostDirectory, string? environmentOverride) =>
        throw new NotImplementedException();
}
