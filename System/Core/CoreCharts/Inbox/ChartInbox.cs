using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Poseidon.Core.Modules;

namespace Poseidon.Modules.Charts.Inbox;

public enum InboxSetState
{
    // Imported; not imported again until its listing changes (CI2).
    Completed,
    // A dataset was refused only for sequence; retried after a later import makes progress (CI3).
    Pending
}

// The latest attempt at one exchange set in the inbox (CI2, CI7). RelativePath is the set's root
// (the folder holding CATALOG.XML) relative to the inbox, with forward slashes.
public sealed record InboxSetRecord(
    string RelativePath,
    string Fingerprint,
    InboxSetState State,
    DateTimeOffset ProcessedAt,
    ChartImportStatus Status,
    int CellsChanged,
    int Errors,
    int Warnings,
    int Information,
    IReadOnlyList<ChartDiagnostic> ErrorDiagnostics);

// Processed holds the final attempt at each set imported by this scan, in the order first attempted;
// Skipped holds the completed sets whose listing was unchanged.
public sealed record InboxScanResult(IReadOnlyList<InboxSetRecord> Processed, IReadOnlyList<string> Skipped);

public sealed record ChartInboxOptions
{
    public required string InboxPath { get; init; }

    // The durable record of processed sets (CI2), replaced atomically.
    public required string RecordPath { get; init; }

    // Deepest folder level below the inbox at which a set root is found (CI1).
    public int MaxDepth { get; init; } = 8;

    public TimeProvider Time { get; init; } = TimeProvider.System;

    public ILog? Log { get; init; }
}

// Imports the exchange sets placed in the inbox folder, once per scan; the module scans at startup (CI5).
// A set is remembered by a fingerprint of its file listing, so it is imported again only when it changes.
public sealed class ChartInbox
{
    private const int RecordFormat = 1;
    private const string CatalogueName = "CATALOG.XML";

    // Refusals that a later import can resolve: the base or the edition the update needs arrives after it.
    private static readonly HashSet<string> SequenceCodes =
    [
        ChartDiagnosticCodes.BaseNotInstalled, ChartDiagnosticCodes.UpdateEditionNewer, ChartDiagnosticCodes.UpdateOutOfSequence
    ];

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly IChartLibrary library;
    private readonly ChartInboxOptions options;
    private readonly object gate = new();
    private Dictionary<string, InboxSetRecord>? records;

    public ChartInbox(IChartLibrary library, ChartInboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(options);
        this.library = library;
        this.options = options;
    }

    // The records as currently held, loaded from RecordPath on first use.
    public IReadOnlyList<InboxSetRecord> Records
    {
        get
        {
            lock (gate)
                return Load().Values.OrderBy(r => r.RelativePath, StringComparer.Ordinal).ToList();
        }
    }

    // Throws OperationCanceledException when cancelled; the set being imported is then not recorded (CI6).
    public async Task<InboxScanResult> ScanAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<ExchangeSetFolder> sets;
        try
        {
            Directory.CreateDirectory(options.InboxPath);
            sets = Discover();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            options.Log?.Error($"Chart inbox {options.InboxPath} cannot be read; no charts were imported from it.", e);
            return new InboxScanResult([], []);
        }

        Dictionary<string, InboxSetRecord> known;
        lock (gate)
            known = new Dictionary<string, InboxSetRecord>(Load(), StringComparer.Ordinal);
        var skipped = new List<string>();
        var queue = new List<ExchangeSetFolder>();
        foreach (var set in sets)
        {
            if (known.TryGetValue(set.RelativePath, out var record) && record.State == InboxSetState.Completed
                && record.Fingerprint == set.Fingerprint)
                skipped.Add(set.RelativePath);
            else
                queue.Add(set);
        }

        var processed = new List<string>();
        var latest = new Dictionary<string, InboxSetRecord>(StringComparer.Ordinal);
        var pending = new List<ExchangeSetFolder>();

        // Returns whether the attempt installed or changed a cell, which may let a pending set through.
        async Task<bool> Attempt(ExchangeSetFolder set)
        {
            if (await Import(set, cancellationToken) is not { } outcome)
                return false;
            if (!latest.ContainsKey(set.RelativePath))
                processed.Add(set.RelativePath);
            latest[set.RelativePath] = outcome.Record;
            pending.Remove(set);
            if (outcome.Record.State == InboxSetState.Pending)
                pending.Add(set);
            return outcome.Progress;
        }

        foreach (var set in queue)
        {
            if (!await Attempt(set))
                continue;
            // Retry the pending sets in rounds until a round changes nothing.
            for (var progress = true; progress && pending.Count > 0;)
            {
                progress = false;
                foreach (var waiting in pending.ToList())
                    progress |= await Attempt(waiting);
            }
        }

        if (skipped.Count > 0)
            options.Log?.Debug($"Chart inbox: {skipped.Count} unchanged exchange set(s) skipped.");
        return new InboxScanResult(processed.Select(p => latest[p]).ToList(), skipped);
    }

    private async Task<(InboxSetRecord Record, bool Progress)?> Import(ExchangeSetFolder set, CancellationToken cancellationToken)
    {
        ChartImportResult result;
        try
        {
            result = await library.ImportAsync(set.Root, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            // Not recorded, so the set is tried again at the next start (CI8).
            options.Log?.Error($"Chart inbox: importing {set.RelativePath} failed unexpectedly; it will be tried again at the next start.", e);
            return null;
        }
        if (result.Status == ChartImportStatus.Cancelled)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new OperationCanceledException($"The import of {set.RelativePath} was cancelled.");
        }

        var errors = result.Diagnostics.Where(d => d.Severity == ChartDiagnosticSeverity.Error).ToList();
        var state = errors.Any(d => SequenceCodes.Contains(d.Code)) ? InboxSetState.Pending : InboxSetState.Completed;
        var record = new InboxSetRecord(set.RelativePath, set.Fingerprint, state, options.Time.GetUtcNow(), result.Status,
            result.Cells.Count, errors.Count,
            result.Diagnostics.Count(d => d.Severity == ChartDiagnosticSeverity.Warning),
            result.Diagnostics.Count(d => d.Severity == ChartDiagnosticSeverity.Information),
            errors);
        Remember(record);
        Report(record);
        return (record, result.Cells.Count > 0);
    }

    private void Report(InboxSetRecord record)
    {
        var level = record.Errors > 0 ? LogLevel.Warning : LogLevel.Information;
        var pending = record.State == InboxSetState.Pending ? "; waiting for an earlier set, retried at the next start" : "";
        options.Log?.Log(level, $"Chart inbox: {record.RelativePath} {record.Status}: {record.CellsChanged} cell(s) installed or changed, " +
            $"{record.Errors} error(s), {record.Warnings} warning(s){pending}.");
        foreach (var error in record.ErrorDiagnostics)
            options.Log?.Error($"Chart inbox: {record.RelativePath}: {error.Code} {error.File}: {error.Message}");
    }

    // Every folder below the inbox, at most MaxDepth levels deep, that holds a catalogue (CI1). The search
    // stops at a set root and does not follow links, which could otherwise loop.
    private List<ExchangeSetFolder> Discover()
    {
        var inbox = Path.GetFullPath(options.InboxPath);
        var roots = new List<string>();
        void Visit(string folder, int depth)
        {
            if (depth > 0 && Directory.EnumerateFiles(folder).Any(f => Path.GetFileName(f).Equals(CatalogueName, StringComparison.OrdinalIgnoreCase)))
            {
                roots.Add(folder);
                return;
            }
            if (depth == options.MaxDepth)
                return;
            foreach (var child in Directory.EnumerateDirectories(folder).Order(StringComparer.Ordinal))
                if (!new DirectoryInfo(child).Attributes.HasFlag(FileAttributes.ReparsePoint))
                    Visit(child, depth + 1);
        }
        Visit(inbox, 0);
        return roots
            .Select(root => new ExchangeSetFolder(Path.GetRelativePath(inbox, root).Replace('\\', '/'), root, Fingerprint(root)))
            .OrderBy(s => s.RelativePath, StringComparer.Ordinal)
            .ToList();
    }

    // Relative path, size and last-write time of every file in the set (CI2).
    private static string Fingerprint(string root)
    {
        var listing = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Select(f => new FileInfo(f))
                     .OrderBy(f => f.FullName, StringComparer.Ordinal))
            listing.Append(Path.GetRelativePath(root, file.FullName).Replace('\\', '/')).Append('|')
                .Append(file.Length).Append('|').Append(file.LastWriteTimeUtc.Ticks).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(listing.ToString())));
    }

    private Dictionary<string, InboxSetRecord> Load()
    {
        if (records is not null)
            return records;
        records = new Dictionary<string, InboxSetRecord>(StringComparer.Ordinal);
        if (!File.Exists(options.RecordPath))
            return records;
        try
        {
            var file = JsonSerializer.Deserialize<RecordFile>(File.ReadAllBytes(options.RecordPath), Json)
                ?? throw new InvalidDataException("The record is empty.");
            if (file.Format != RecordFormat)
                throw new InvalidDataException($"The record has format {file.Format}; this version reads format {RecordFormat}.");
            foreach (var record in file.Sets)
                records[record.RelativePath] = record;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException
                                      or NotSupportedException or ArgumentException)
        {
            // Its sets are imported again, which only skips what is already installed (CI4, CI8).
            options.Log?.Warning($"The chart inbox record {options.RecordPath} cannot be read; it is set aside and every set is imported again.", e);
            records.Clear();
            SetAside();
        }
        return records;
    }

    private void SetAside()
    {
        try
        {
            File.Move(options.RecordPath, $"{options.RecordPath}.unreadable-{options.Time.GetUtcNow():yyyyMMddHHmmss}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            options.Log?.Error("The unreadable chart inbox record could not be set aside; the next save replaces it.", e);
        }
    }

    // Saved after every attempt, so a scan that stops part-way keeps what it finished.
    private void Remember(InboxSetRecord record)
    {
        lock (gate)
        {
            var all = Load();
            all[record.RelativePath] = record;
            try
            {
                var path = Path.GetFullPath(options.RecordPath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temp = path + ".tmp";
                File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(
                    new RecordFile(RecordFormat, all.Values.OrderBy(r => r.RelativePath, StringComparer.Ordinal).ToList()), Json));
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                options.Log?.Error($"The chart inbox record {options.RecordPath} could not be saved; {record.RelativePath} will be imported again at the next start.", e);
            }
        }
    }

    private sealed record ExchangeSetFolder(string RelativePath, string Root, string Fingerprint);

    private sealed record RecordFile(int Format, IReadOnlyList<InboxSetRecord> Sets);
}
