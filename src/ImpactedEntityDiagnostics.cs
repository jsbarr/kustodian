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

        // Each required column must be able to describe a single source record. We compute, per column,
        // the set of leaf-sets it could trace to — at a matching join/lookup key fork the check may follow
        // only the kind-allowed branch(es); everywhere else children's leaves combine outright. The columns
        // are consistent iff they share a common achievable leaf-set (calculated leaves contribute nothing).
        var outputByName = facts.Output.ToDictionary(o => o.Name, StringComparer.OrdinalIgnoreCase);
        var families = present.ToDictionary(
            c => c.Name,
            c => Achievable(outputByName[c.Name].Provenance));

        // Consistent iff some leaf-set achievable by the first required column is also achievable by every
        // other one — i.e. the three families share a common member (a single source record they all fit).
        var anchor = families[present[0].Name];
        var others = present.Skip(1).ToList();
        var consistent = anchor.Any(cand => others.All(c => families[c.Name].Any(s => s.SetEquals(cand))));
        if (consistent) yield break;

        // Report the columns that share no achievable leaf-set with the first required column (its odd ones
        // out). If the first column is itself the outlier, every other column disagrees with it.
        var inconsistent = others
            .Where(c => !Agree(anchor, families[c.Name]))
            .Select(c => c.Name)
            .OrderBy(n => n)
            .ToArray();
        if (inconsistent.Length == 0)
            inconsistent = others.Select(c => c.Name).OrderBy(n => n).ToArray();

        yield return new DiagnosticMessage(
            Level: "ERROR",
            Type: "ImpactedEntityConsistency",
            Message: $"Impacted entity columns have inconsistent provenance: {string.Join(", ", inconsistent)}",
            AffectedColumns: inconsistent);
    }

    // True when the two families share an achievable leaf-set (i.e. both columns can describe one record).
    static bool Agree(List<HashSet<(string, int)>> a, List<HashSet<(string, int)>> b) =>
        a.Any(x => b.Any(y => y.SetEquals(x)));

    // The set of leaf-sets a column could trace to. At a collapsible key fork, follow only the
    // kind-allowed branch(es); at any other node, every child contributes (leaves combine).
    static List<HashSet<(string table, int pos)>> Achievable(ProvenanceNode? n)
    {
        if (n == null)
            return [[]];
        if (n.Table != null)
            return [[(n.Table, n.Position?.Abs ?? 0)]];

        var sources = n.Sources ?? [];
        if (sources.Length == 0)
            return [[]]; // calculated leaf — contributes nothing

        if (n.Kind != null && sources.Length >= 2)
            switch (CollapseKind(n.Kind))
            {
                case "left": return Achievable(sources[0]);
                case "right": return Achievable(sources[1]);
                case "either": return [.. Achievable(sources[0]), .. Achievable(sources[1])];
            }

        // Normal node: a child may independently offer several leaf-sets, but all children combine.
        var combos = new List<HashSet<(string, int)>> { new() };
        foreach (var src in sources)
        {
            var childSets = Achievable(src);
            var next = new List<HashSet<(string, int)>>();
            foreach (var acc in combos)
                foreach (var cs in childSets)
                    next.Add([.. acc, .. cs]);
            combos = next;
        }
        return combos;
    }

    // Which branch(es) of a key fork the check may follow, by join kind (null = not collapsible).
    static string? CollapseKind(string kind) => kind switch
    {
        "inner" or "innerunique" => "either",
        "leftouter" => "left",
        "rightouter" => "right",
        _ => null, // fullouter / semi / anti: not collapsible
    };
}
