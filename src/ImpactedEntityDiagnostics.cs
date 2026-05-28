public static class ImpactedEntityDiagnostics
{
    public static IEnumerable<DiagnosticMessage> Check(QueryFacts facts, AnalyseRequest request)
    {
        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Timestamp", "ReportId", request.ImpactedEntityField };

        var outputNames = facts.Columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = required.Where(r => !outputNames.Contains(r)).OrderBy(r => r).ToArray();
        if (missing.Length > 0)
        {
            yield return new DiagnosticMessage(
                Level: "ERROR",
                Type: "ImpactedEntityExistence",
                Message: $"Missing required output columns: {string.Join(", ", missing)}",
                AffectedColumns: missing);
            yield break;
        }

        var present = facts.Columns.Where(c => required.Contains(c.Name)).ToList();
        if (present.Count < 2) yield break;

        // All required columns must trace back to exactly the same set of source table instances.
        // "Instance" = (table name, position in query) — this catches both cross-table mixing
        // (e.g. Timestamp from DeviceEvents, AccountUpn from IdentityInfo) and same-table split
        // branches (e.g. two union arms that each reference DeviceEvents independently).
        var outputByName = facts.Output.ToDictionary(o => o.Name, StringComparer.OrdinalIgnoreCase);
        var sourceSets = present.ToDictionary(
            c => c.Name,
            c => LeafSources(outputByName[c.Name].Provenance));

        var first = sourceSets.First().Value;
        var inconsistent = sourceSets
            .Where(kv => !kv.Value.SetEquals(first))
            .Select(kv => kv.Key)
            .OrderBy(n => n)
            .ToArray();

        if (inconsistent.Length > 0)
            yield return new DiagnosticMessage(
                Level: "ERROR",
                Type: "ImpactedEntityConsistency",
                Message: $"Impacted entity columns have inconsistent provenance: {string.Join(", ", inconsistent)}",
                AffectedColumns: inconsistent);
    }

    static HashSet<(string table, int pos)> LeafSources(ProvenanceNode? node)
    {
        var result = new HashSet<(string, int)>();
        Collect(node, result);
        return result;

        static void Collect(ProvenanceNode? n, HashSet<(string, int)> acc)
        {
            if (n == null) return;
            if (n.Table != null) { acc.Add((n.Table, n.Position?.Abs ?? 0)); return; }
            foreach (var src in n.Sources ?? []) Collect(src, acc);
        }
    }
}
