using System.Collections;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Poseidon.Core.Modules;
using Poseidon.Modules.Charts.S101;
using Poseidon.Modules.Charts.Store;
using Xunit;

namespace CoreCharts.Tests;

// A canonical text form of an object graph using runtime types, so derived records (geometry kinds) are
// compared in full; System.Text.Json would serialize only the declared base type.
internal static class Canonical
{
    public static string Of(object? value)
    {
        var builder = new StringBuilder();
        Write(builder, value, 0);
        return builder.ToString();
    }

    private static void Write(StringBuilder b, object? value, int depth)
    {
        if (depth > 64)
            throw new InvalidOperationException("Object graph too deep for a canonical form.");
        switch (value)
        {
            case null: b.Append("null"); return;
            case string s: b.Append('"').Append(s).Append('"'); return;
            case bool flag: b.Append(flag ? "true" : "false"); return;
            case char c: b.Append('\'').Append(c).Append('\''); return;
            case double d: b.Append(d.ToString("R", CultureInfo.InvariantCulture)); return;
            case float f: b.Append(f.ToString("R", CultureInfo.InvariantCulture)); return;
            case IFormattable f when value.GetType().IsPrimitive || value is decimal or DateTimeOffset or Enum:
                b.Append(f.ToString(null, CultureInfo.InvariantCulture)); return;
            case byte[] bytes: b.Append(Convert.ToHexString(bytes)); return;
            case IDictionary dictionary:
                b.Append('{');
                foreach (var key in dictionary.Keys.Cast<object>().OrderBy(k => Of(k), StringComparer.Ordinal))
                {
                    Write(b, key, depth + 1);
                    b.Append(':');
                    Write(b, dictionary[key], depth + 1);
                    b.Append(',');
                }
                b.Append('}');
                return;
            case IEnumerable sequence:
                b.Append('[');
                foreach (var item in sequence)
                {
                    Write(b, item, depth + 1);
                    b.Append(',');
                }
                b.Append(']');
                return;
        }
        var type = value.GetType();
        b.Append(type.Name).Append('(');
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.GetIndexParameters().Length == 0 && p.Name != "EqualityContract")
                     .OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            b.Append(property.Name).Append('=');
            Write(b, property.GetValue(value), depth + 1);
            b.Append(';');
        }
        b.Append(')');
    }
}

internal static class Decoding
{
    public static readonly DecodeOptions S100TdsOverride = new()
    {
        ProductEditionOverrides = new Dictionary<string, string> { ["2.1.0"] = "2.0.0" }
    };

    public static DecodeResult Result(string path, DecodeOptions? options = null)
        => new S101DatasetDecoder().Decode(File.ReadAllBytes(path), Path.GetFileName(path), options);

    public static DecodeResult Result(byte[] data, string fileName, DecodeOptions? options = null)
        => new S101DatasetDecoder().Decode(data, fileName, options);

    public static S101Dataset Dataset(string path, DecodeOptions? options = null) => Decoded(Result(path, options));

    public static S101Dataset Dataset(byte[] data, string fileName, DecodeOptions? options = null)
        => Decoded(Result(data, fileName, options));

    private static S101Dataset Decoded(DecodeResult result)
    {
        Assert.True(result.Status == DecodeStatus.Decoded,
            $"Expected Decoded but got {result.Status}: {string.Join("; ", result.Diagnostics.Select(d => d.Code + " " + d.Message))}");
        return Assert.IsType<S101Dataset>(result.Dataset);
    }

    public static IEnumerable<S101Attribute> Flatten(IEnumerable<S101Attribute> attributes)
        => attributes.SelectMany(a => new[] { a }.Concat(Flatten(a.Children)));
}

internal static class Libraries
{
    public static ChartLibrary Create(string directory, int cacheCapacity = 64, DecodeOptions? decode = null,
        TimeProvider? time = null)
        => new(new ChartLibraryOptions
        {
            DatabasePath = Path.Combine(directory, "charts.db"),
            CacheCapacity = cacheCapacity,
            Decode = decode ?? new DecodeOptions(),
            Time = time ?? TimeProvider.System
        });

    public static async Task<ChartImportResult> ImportLoaded(ChartLibrary library, string root)
    {
        var result = await library.ImportAsync(root);
        Assert.True(result.Status == ChartImportStatus.Loaded,
            $"Expected Loaded for {root} but got {result.Status}: {Describe(result.Diagnostics)}");
        return result;
    }

    public static ChartCellSummary Cell(ChartSnapshot snapshot, string name)
        => Assert.Single(snapshot.Cells, c => c.DatasetName == name);

    public static string Describe(IEnumerable<ChartDiagnostic> diagnostics)
        => string.Join("; ", diagnostics.Select(d => $"{d.Severity} {d.Code} {d.File} {d.Message}"));
}

// Listens on loopback and counts every connection attempt, to prove a reader made no network request.
internal sealed class ConnectionCounter : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private int connections;

    public ConnectionCounter()
    {
        listener.Start();
        _ = AcceptLoop();
    }

    public string BaseUrl => $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";

    public int Connections => Volatile.Read(ref connections);

    private async Task AcceptLoop()
    {
        try
        {
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync();
                Interlocked.Increment(ref connections);
            }
        }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    public void Dispose() => listener.Stop();
}

internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

// Reports progress synchronously on the importing thread, as ChartLibrary promises.
internal sealed class SyncProgress(Action<ImportProgress> report) : IProgress<ImportProgress>
{
    public void Report(ImportProgress value) => report(value);
}
