namespace Poseidon.Core.Modules;

// The point and feature Charts declares in its module.json, shared so consumers can find the effective
// contribution in RuntimeStatus without referencing Charts.
public static class ChartPoints
{
    public const string Library = "charts.library";
    public const string LibraryFeatureId = "core.charts.library";
}

public enum ChartImportStatus
{
    Loaded,
    Rejected,
    UnsupportedVersion,
    Cancelled
}

public enum ChartDiagnosticSeverity
{
    Information,
    Warning,
    Error
}

// Code is a stable machine-readable identifier; File and Position locate the problem when known.
public sealed record ChartDiagnostic(
    ChartDiagnosticSeverity Severity,
    string Code,
    string Message,
    string? File = null,
    long? Position = null);

public enum ChartLineageState
{
    Current,
    // A required update was refused; the cell stays at its last valid state until a complete sequence,
    // re-issue or new edition clears it.
    NotUpToDate,
    Cancelled
}

public sealed record ChartCellSummary(
    string DatasetName,
    int Edition,
    int Update,
    ChartLineageState State,
    string ProductEdition,
    string EncodingEdition,
    string SourceFile,
    string SourceSha256,
    DateTimeOffset ImportedAt,
    IReadOnlyList<ChartDiagnostic> Diagnostics);

// Generation identifies the published snapshot the import produced (or left in place).
public sealed record ChartImportResult(
    ChartImportStatus Status,
    long Generation,
    IReadOnlyList<ChartCellSummary> Cells,
    IReadOnlyList<ChartDiagnostic> Diagnostics);

public sealed record ChartNeighbourhood(long Generation, IReadOnlyList<ChartCellSummary> Cells);

public interface IChartLibrary
{
    Task<ChartImportResult> ImportAsync(string exchangeSetRoot, CancellationToken cancellationToken = default);

    // The reference cells plus every installed cell whose catalogue coverage touches or overlaps them,
    // all from one snapshot generation.
    ChartNeighbourhood QueryNeighbourhood(IReadOnlyList<string> referenceCells);
}
