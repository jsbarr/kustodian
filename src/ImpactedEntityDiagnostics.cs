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

        // All required columns must trace back to exactly the same set of source tables.
        // Mixed provenance (e.g. Timestamp from DeviceEvents, AccountUpn from IdentityInfo)
        // means the alert evidence spans multiple independent rows, which is misleading.
        var sourceSets = present.ToDictionary(
            c => c.Name,
            c => new HashSet<string>(facts.SourceMap[c].Select(s => facts.Globals.GetTable(s)!.Name)));

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
}
