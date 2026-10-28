using Poseidon.Core.Modules;

namespace Poseidon.Core.Runtime;

internal sealed record Exclusion(PackageState State, string Reason);

internal sealed record GraphPlan(
    List<ModulePackage> Ordered,
    Dictionary<string, (string Provider, object Value, string Point)> Features,
    Dictionary<ModulePackage, Exclusion> Excluded);

// Plans the largest safe module set: invalid packages are failed, packages whose dependencies,
// extension points or override targets are unavailable are blocked, and everything else is activated
// in dependency order. Conflicts never pick an arbitrary winner; every conflicting claimant is excluded.
internal static class ModuleGraph
{
    public static GraphPlan Plan(IReadOnlyList<ModulePackage> packages, IReadOnlyList<ExtensionPoint> hostPoints,
        IReadOnlyDictionary<ModulePackage, Exclusion>? initialExclusions = null)
    {
        var excluded = new Dictionary<ModulePackage, Exclusion>(ReferenceEqualityComparer.Instance);
        if (initialExclusions is not null)
            foreach (var (package, exclusion) in initialExclusions)
                excluded[package] = exclusion;

        foreach (var group in packages.Where(p => !excluded.ContainsKey(p))
                     .GroupBy(p => p.Source.Manifest.Id, StringComparer.Ordinal).Where(g => g.Count() > 1))
            foreach (var package in group)
                excluded[package] = new(PackageState.Failed,
                    $"Duplicate module ID {group.Key} is deployed in " +
                    string.Join(", ", group.Select(p => p.Source.Directory).Order(StringComparer.Ordinal)) +
                    "; none of them is loaded.");

        foreach (var package in packages.Where(p => !excluded.ContainsKey(p)))
        {
            var problem = ValidateDeclarations(package);
            if (problem is not null)
                excluded[package] = new(PackageState.Failed, problem);
        }

        ExcludeDuplicatePoints(packages, hostPoints, excluded);

        while (true)
        {
            var accepted = packages.Where(p => !excluded.ContainsKey(p)).ToList();
            var points = PointTable(accepted, hostPoints);
            if (BlockUnsatisfied(packages, accepted, points, excluded))
                continue;
            var ordered = Order(accepted, points, out var cyclic);
            if (cyclic.Count > 0)
            {
                var names = string.Join(", ", cyclic.Select(p => p.Source.Manifest.Id).Order(StringComparer.Ordinal));
                foreach (var package in cyclic)
                    excluded[package] = new(PackageState.Blocked, $"Module dependency cycle involving {names}.");
                continue;
            }
            if (TryResolveFeatures(ordered, points, excluded, out var features))
                return new GraphPlan(ordered, features, excluded);
        }
    }

    // Point ID -> declaring module (null for host points) and its declaration.
    private static Dictionary<string, (string? Provider, ExtensionPoint Point)> PointTable(
        List<ModulePackage> accepted, IReadOnlyList<ExtensionPoint> hostPoints)
    {
        var table = new Dictionary<string, (string? Provider, ExtensionPoint Point)>(StringComparer.Ordinal);
        foreach (var point in hostPoints)
            table[point.Id] = (null, point);
        foreach (var package in accepted)
            foreach (var point in package.Source.Manifest.Provides)
                table[point.Id] = (package.Source.Manifest.Id, point);
        return table;
    }

    private static void ExcludeDuplicatePoints(IReadOnlyList<ModulePackage> packages,
        IReadOnlyList<ExtensionPoint> hostPoints, Dictionary<ModulePackage, Exclusion> excluded)
    {
        var hostIds = hostPoints.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var declarations = packages.Where(p => !excluded.ContainsKey(p))
            .SelectMany(p => p.Source.Manifest.Provides.Select(point => (Package: p, Point: point)))
            .GroupBy(d => d.Point.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1 || hostIds.Contains(g.Key))
            .ToList();
        foreach (var group in declarations)
        {
            var owners = hostIds.Contains(group.Key)
                ? "the host"
                : string.Join(", ", group.Select(d => d.Package.Source.Manifest.Id).Distinct().Order(StringComparer.Ordinal));
            foreach (var declaration in group)
                excluded[declaration.Package] = new(PackageState.Failed,
                    $"Extension point {group.Key} is declared more than once ({owners}); none of the claimants is loaded.");
        }
    }

    private static IEnumerable<ModuleDependency> Requirements(ModulePackage package)
        => package.Source.Manifest.Dependencies;

    // Modules that must be active before this one starts: dependencies, the owners of the points it
    // contributes to, and the original providers of features it replaces.
    private static IEnumerable<string> Edges(ModulePackage package, List<ModulePackage> accepted,
        Dictionary<string, (string? Provider, ExtensionPoint Point)> points)
    {
        var self = package.Source.Manifest.Id;
        var ids = Requirements(package).Select(d => d.Id).ToList();
        foreach (var contribution in package.Source.Manifest.Contributions)
        {
            if (points.TryGetValue(contribution.Point, out var point) && point.Provider is not null)
                ids.Add(point.Provider);
            if (contribution.Replaces)
                ids.AddRange(accepted.Where(p => p.Source.Manifest.Contributions.Any(
                    c => !c.Replaces && c.Id == contribution.Id)).Select(p => p.Source.Manifest.Id));
        }
        return ids.Where(id => id != self).Distinct(StringComparer.Ordinal);
    }

    private static bool BlockUnsatisfied(IReadOnlyList<ModulePackage> all, List<ModulePackage> accepted,
        Dictionary<string, (string? Provider, ExtensionPoint Point)> points,
        Dictionary<ModulePackage, Exclusion> excluded)
    {
        var byId = accepted.ToDictionary(p => p.Source.Manifest.Id, StringComparer.Ordinal);
        var changed = false;
        foreach (var package in accepted)
        {
            foreach (var dependency in Requirements(package))
            {
                string? reason = null;
                if (!byId.TryGetValue(dependency.Id, out var target))
                {
                    reason = all.Any(p => p.Source.Manifest.Id == dependency.Id)
                        ? $"Requires {dependency.Id} {dependency.Version}, which is unavailable."
                        : $"Requires {dependency.Id} {dependency.Version}, which is not installed.";
                }
                else if (!dependency.IsSatisfiedBy(target.Source.Version))
                    reason = $"Requires {dependency.Id} {dependency.Version} (same major version or later), " +
                             $"but {target.Source.Manifest.Version} is installed.";
                if (reason is null)
                    continue;
                excluded[package] = new(PackageState.Blocked, reason);
                changed = true;
                break;
            }
            if (excluded.ContainsKey(package))
                continue;
            foreach (var contribution in package.Source.Manifest.Contributions)
            {
                var exclusion = CheckPoint(package, contribution, points);
                if (exclusion is null)
                    continue;
                excluded[package] = exclusion;
                changed = true;
                break;
            }
        }
        return changed;
    }

    private static Exclusion? CheckPoint(ModulePackage package, Contribution contribution,
        Dictionary<string, (string? Provider, ExtensionPoint Point)> points)
    {
        if (!points.TryGetValue(contribution.Point, out var entry))
            return new(PackageState.Blocked,
                $"Contributes {contribution.Id} to extension point {contribution.Point}, which no loaded module provides.");
        var point = entry.Point;
        var hostOwnedPoint = entry.Provider is null;
        if (!hostOwnedPoint && !new ModuleDependency { Id = point.Id, Version = contribution.Version }.IsSatisfiedBy(Version.Parse(point.Version)))
            return new(PackageState.Blocked,
                $"Contribution {contribution.Id} targets {point.Id} {contribution.Version} " +
                $"(same major version or later), but {point.Version} is provided.");
        if (point.Contract is not null &&
            !SharedInterfaces(package.Features[contribution.Id]).Any(i => i.FullName == point.Contract))
            return new(PackageState.Failed,
                $"Contribution {contribution.Id} does not implement {point.Contract}, required by {point.Id}.");
        return null;
    }

    // Kahn's algorithm with ordinal tie-breaking keeps activation order deterministic.
    private static List<ModulePackage> Order(List<ModulePackage> accepted,
        Dictionary<string, (string? Provider, ExtensionPoint Point)> points, out List<ModulePackage> cyclic)
    {
        var byId = accepted.ToDictionary(p => p.Source.Manifest.Id, StringComparer.Ordinal);
        var edges = accepted.ToDictionary<ModulePackage, ModulePackage, string[]>(p => p,
            p => Edges(p, accepted, points).Where(byId.ContainsKey).ToArray(), ReferenceEqualityComparer.Instance);
        var pending = accepted.ToDictionary<ModulePackage, ModulePackage, int>(p => p,
            p => edges[p].Length, ReferenceEqualityComparer.Instance);
        var dependents = accepted.ToDictionary<ModulePackage, ModulePackage, List<ModulePackage>>(
            p => p, _ => [], ReferenceEqualityComparer.Instance);
        foreach (var package in accepted)
            foreach (var id in edges[package])
                dependents[byId[id]].Add(package);
        var ready = new SortedSet<string>(
            accepted.Where(p => pending[p] == 0).Select(p => p.Source.Manifest.Id), StringComparer.Ordinal);
        var ordered = new List<ModulePackage>();
        while (ready.Count > 0)
        {
            var next = byId[ready.Min!];
            ready.Remove(ready.Min!);
            ordered.Add(next);
            foreach (var dependent in dependents[next])
                if (--pending[dependent] == 0)
                    ready.Add(dependent.Source.Manifest.Id);
        }
        cyclic = accepted.Where(p => pending[p] > 0).ToList();
        return ordered;
    }

    internal static string? ValidateDeclarations(ModulePackage package)
    {
        var manifest = package.Source.Manifest;
        if (manifest.Contributions.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != manifest.Contributions.Length ||
            !package.Features.Keys.Order(StringComparer.Ordinal)
                .SequenceEqual(manifest.Contributions.Select(c => c.Id).Order(StringComparer.Ordinal)))
            return "Feature declarations do not match registrations: manifest contributions " +
                   $"[{string.Join(", ", manifest.Contributions.Select(c => c.Id))}], module registers " +
                   $"[{string.Join(", ", package.Features.Keys)}].";
        foreach (var contribution in manifest.Contributions)
        {
            var value = package.Features[contribution.Id];
            if (value is null)
                return $"Null feature: {manifest.Id}/{contribution.Id}";
            if (SharedInterfaces(value).Length == 0)
                return $"Feature requires a host-shared interface: {contribution.Id}";
        }
        return null;
    }

    private static Type[] SharedInterfaces(object value)
        => value.GetType().GetInterfaces().Where(i => i.Assembly == typeof(IModule).Assembly).ToArray();

    private static bool TryResolveFeatures(List<ModulePackage> ordered,
        Dictionary<string, (string? Provider, ExtensionPoint Point)> points,
        Dictionary<ModulePackage, Exclusion> excluded,
        out Dictionary<string, (string Provider, object Value, string Point)> registry)
    {
        registry = new Dictionary<string, (string Provider, object Value, string Point)>(StringComparer.Ordinal);
        var changed = false;
        var claims = ordered
            .SelectMany(p => p.Source.Manifest.Contributions.Select(c => (Package: p, Contribution: c)))
            .ToList();

        foreach (var point in claims.GroupBy(c => c.Contribution.Point, StringComparer.Ordinal)
                     .Where(g => points[g.Key].Point.Cardinality == PointCardinality.Single &&
                                 g.Select(c => c.Contribution.Id).Distinct(StringComparer.Ordinal).Count() > 1))
        {
            var names = string.Join(", ", point.Select(c => c.Package.Source.Manifest.Id).Distinct().Order(StringComparer.Ordinal));
            foreach (var claim in point)
                excluded[claim.Package] = new(PackageState.Failed,
                    $"Extension point {point.Key} accepts a single contribution, but {names} contribute different features to it.");
            changed = true;
        }
        if (changed)
            return false;

        foreach (var feature in claims.GroupBy(c => c.Contribution.Id, StringComparer.Ordinal)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var originals = feature.Where(c => !c.Contribution.Replaces).ToList();
            var replacements = feature.Where(c => c.Contribution.Replaces).ToList();
            if (feature.Select(c => c.Contribution.Point).Distinct(StringComparer.Ordinal).Count() > 1)
            {
                foreach (var claim in feature)
                    excluded[claim.Package] = new(PackageState.Failed,
                        $"Feature {feature.Key} is contributed to different extension points.");
                changed = true;
                continue;
            }
            if (originals.Count > 1)
            {
                var names = string.Join(", ", originals.Select(c => c.Package.Source.Manifest.Id));
                foreach (var claim in originals)
                    excluded[claim.Package] = new(PackageState.Failed,
                        $"Ambiguous override of {feature.Key}: {names} all contribute it without an explicit replacement.");
                changed = true;
                continue;
            }
            if (originals.Count == 0)
            {
                foreach (var claim in replacements)
                    excluded[claim.Package] = new(PackageState.Blocked,
                        $"Replacement target does not exist: no loaded module contributes {feature.Key}.");
                changed = true;
                continue;
            }

            var target = originals[0];
            foreach (var replacement in replacements)
            {
                if (Version.Parse(target.Contribution.Version) != Version.Parse(replacement.Contribution.Version) ||
                    SharedInterfaces(target.Package.Features[feature.Key])
                        .Any(i => !i.IsInstanceOfType(replacement.Package.Features[feature.Key])))
                {
                    excluded[replacement.Package] = new(PackageState.Blocked,
                        $"Incompatible replacement contract: {feature.Key} {replacement.Contribution.Version} " +
                        $"cannot replace {target.Package.Source.Manifest.Id}'s {target.Contribution.Version}.");
                    changed = true;
                }
            }
            if (changed || replacements.Count == 0)
            {
                if (!changed)
                    registry[feature.Key] = (target.Package.Source.Manifest.Id,
                        target.Package.Features[feature.Key], target.Contribution.Point);
                continue;
            }
            var top = replacements.Max(c => c.Contribution.Priority);
            var leaders = replacements.Where(c => c.Contribution.Priority == top).ToList();
            if (leaders.Count > 1)
            {
                var names = string.Join(", ", leaders.Select(c => c.Package.Source.Manifest.Id));
                foreach (var claim in leaders)
                    excluded[claim.Package] = new(PackageState.Blocked,
                        $"Ambiguous override of {feature.Key}: {names} all replace it with priority {top}; " +
                        "give them distinct priorities.");
                changed = true;
                continue;
            }
            registry[feature.Key] = (leaders[0].Package.Source.Manifest.Id,
                leaders[0].Package.Features[feature.Key], leaders[0].Contribution.Point);
        }
        return !changed;
    }
}
