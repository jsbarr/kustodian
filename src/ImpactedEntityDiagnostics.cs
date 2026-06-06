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
        var joinPartners = BuildJoinPartners(facts.JoinKeyEquivalences);
        var families = present.ToDictionary(
            c => c.Name,
            c => Achievable(outputByName[c.Name].Provenance, joinPartners));

        // Consistent iff some leaf-set achievable by the first required column is also achievable by every
        // other one — i.e. the three families share a common member (a single source record they all fit).
        var anchor = families[present[0].Name];
        var others = present.Skip(1).ToList();
        var consistent = anchor.Any(cand => others.All(c => Agree([cand], families[c.Name])));
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
    static List<HashSet<(string table, int pos)>> Achievable(
        ProvenanceNode? n, Dictionary<(string, int, string), List<(string, int)>> joinPartners)
    {
        if (n == null)
            return [[]];
        if (n.Table != null)
        {
            var self = (n.Table, n.Position?.Abs ?? 0);
            // A join key leaf may also describe its matched partner's record (the equality the join proves).
            // The faithful tree never carries this edge, so apply it here as extra single-leaf alternatives.
            if (joinPartners.TryGetValue((n.Table, self.Item2, n.Column), out var partners))
                return [[self], .. partners.Select(p => new HashSet<(string, int)> { p })];
            return [[self]];
        }

        var sources = n.Sources ?? [];
        if (sources.Length == 0)
            return [[]]; // calculated leaf — contributes nothing

        // At a key fork, follow only the branch(es) the join kind allows. Kinds with no case
        // (fullouter / semi / anti) are not collapsible and fall through to normal handling.
        if (n.Kind != null && sources.Length >= 2)
            switch (n.Kind)
            {
                case "leftouter": return Achievable(sources[0], joinPartners);
                case "rightouter": return Achievable(sources[1], joinPartners);
                case "inner" or "innerunique": return [.. Achievable(sources[0], joinPartners), .. Achievable(sources[1], joinPartners)];
            }

        // Normal node: every child contributes to the record, so the result is the Cartesian product
        // of the children's leaf-sets — each child may independently offer several, and one from each
        // combines into a full record. Fold the sources in: cross the running sets with the next child's,
        // replacing the accumulator each round (start from one empty set so the first child seeds it).
        var achievable = new List<HashSet<(string, int)>> { new() };
        foreach (var src in sources)
        {
            var childSets = Achievable(src, joinPartners);
            var next = new List<HashSet<(string, int)>>();
            foreach (var acc in achievable)
                foreach (var cs in childSets)
                    next.Add([.. acc, .. cs]);
            achievable = next;
        }
        return achievable;
    }

    // Per join key leaf, the partner leaves it may also be attributed to. inner/innerunique prove the keys
    // equal on every output row (either side); a leftouter/rightouter guarantees only the surviving side,
    // so the non-surviving key may borrow the surviving leaf but not vice versa; fullouter/semi/anti prove
    // no cross-row equality and add nothing.
    static Dictionary<(string, int, string), List<(string, int)>> BuildJoinPartners(
        IReadOnlyList<JoinKeyEquivalence> equivalences)
    {
        var partners = new Dictionary<(string, int, string), List<(string, int)>>();
        void Add(LeafRef from, LeafRef to)
        {
            var key = (from.Table, from.Pos, from.Column);
            if (!partners.TryGetValue(key, out var list)) partners[key] = list = [];
            list.Add((to.Table, to.Pos));
        }
        foreach (var e in equivalences)
            switch (e.Kind)
            {
                case "inner" or "innerunique": Add(e.Left, e.Right); Add(e.Right, e.Left); break;
                case "leftouter": Add(e.Right, e.Left); break;
                case "rightouter": Add(e.Left, e.Right); break;
            }
        return partners;
    }
}
