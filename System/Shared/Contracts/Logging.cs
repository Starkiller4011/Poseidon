namespace Poseidon.Core.Modules;

// M1-I: a minimal, framework-independent logging abstraction (no third-party logging package, for the
// same cross-platform-compatibility reason IChartViewport.CreateView avoids a UI framework in its own
// signature). A module gets one through IModuleHost.GetLog, never constructs one itself.
public enum LogLevel { Trace, Debug, Information, Warning, Error }

public interface ILog
{
    void Log(LogLevel level, string message, Exception? exception = null);
}

// Convenience members with default implementations; a real ILog only has to provide Log itself.
public static class LogExtensions
{
    public static void Trace(this ILog log, string message) => log.Log(LogLevel.Trace, message);
    public static void Debug(this ILog log, string message) => log.Log(LogLevel.Debug, message);
    public static void Information(this ILog log, string message) => log.Log(LogLevel.Information, message);
    public static void Warning(this ILog log, string message, Exception? exception = null) =>
        log.Log(LogLevel.Warning, message, exception);
    public static void Error(this ILog log, string message, Exception? exception = null) =>
        log.Log(LogLevel.Error, message, exception);
}
