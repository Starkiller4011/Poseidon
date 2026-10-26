namespace Poseidon.Core.Modules;

// A top-level window or console surface supplied by a module. The host only needs to know whether
// any module contributes to the shell point; it never names a specific module.
public interface IShell
{
}

// Extension points owned by the host itself. All other points are declared by the modules that own
// the concept (in their manifest's "provides"), so the host assumes nothing about loaded modules.
public static class ExtensionPoints
{
    public const string Shell = "shell.host";

    public static IReadOnlyList<ExtensionPoint> Host { get; } =
    [
        new ExtensionPoint
        {
            Id = Shell, Version = "1.0.0", Cardinality = PointCardinality.Multiple,
            Contract = typeof(IShell).FullName
        }
    ];
}
