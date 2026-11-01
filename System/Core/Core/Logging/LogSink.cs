using Poseidon.Core.Modules;

namespace Poseidon.Core.Runtime.Logging;

// M1-I (I4 to I7, I10): the one real destination every category-scoped ILog (CategoryLog) writes
// through — a single shared log file (I6: one file, not one per module), plus the console. A log
// call below minimumLevel produces no output at all (I4). A write failure (disk full, permission
// denied) is swallowed; logging must never throw back at the caller it is trying to help (I7).
internal sealed class LogSink(LogLevel minimumLevel, string logFilePath)
{
    public void Write(string category, LogLevel level, string message, Exception? exception) =>
        throw new NotImplementedException($"LogSink(minimumLevel={minimumLevel}, logFilePath={logFilePath})");
}

// M1-I (I1, I2): returned by IModuleHost.GetLog. category is free text; every Core module's
// convention is its own module ID.
internal sealed class CategoryLog(string category, LogSink sink) : ILog
{
    public void Log(LogLevel level, string message, Exception? exception = null) =>
        sink.Write(category, level, message, exception);
}
