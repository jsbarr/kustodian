using Kusto.Language;
using Kusto.Language.Symbols;
using Kusto.Language.Syntax;

record InvokeContext(
    int InvokePos,
    string[] ScalarParamNames,
    int[] ScalarArgPositions,
    SyntaxNode BodySyntax,
    TableSymbol? ResultTable);

// Unified per-symbol metadata collected in a single AST walk.
// NameDeclaration is column-only (tables have no declaration node in the query AST).
// InvokeContext is set only for columns newly introduced by an invoke operator.
record SymbolInfo(
    int FirstPosition,
    NameDeclaration? NameDeclaration = null,
    InvokeContext? InvokeContext = null);

// A lookup `on` key whose two source branches (left input, right enrichment) collapse into one
// output column. Kind drives which branch the consistency check may follow; LeftPos/RightPos are
// the corrected base-table positions of each branch (the SDK alone collapses both to one position).
record LookupFork(string Kind, int LeftPos, int RightPos);

public record QueryFacts(
    string Query,
    IReadOnlyList<ColumnSymbol> Columns,
    Dictionary<ColumnSymbol, IReadOnlyList<ColumnSymbol>> SourceMap,
    IReadOnlyList<ColumnWithProvenance> Output,
    IReadOnlyList<Diagnostic> RawDiagnostics,
    GlobalState Globals,
    Kusto.Language.Syntax.SyntaxNode RawSyntax)
{
    // Parses and semantically analyzes the query, then builds provenance for each output column.
    public static QueryFacts Build(string query, GlobalState globals)
    {
        var code = KustoCode.ParseAndAnalyze(query, globals);

        var resultTable = code.ResultType as TableSymbol;

        var columns = resultTable?.Columns ?? (IReadOnlyList<ColumnSymbol>)[];
        var (symbolInfoMap, columnTableRefPos, lookupForks) = BuildSymbolInfoMap(code.Syntax, globals);

        var built = columns.Select(c =>
        {
            var leafSources = new HashSet<ColumnSymbol>();
            var node = BuildProvenanceNode(c, globals, symbolInfoMap, columnTableRefPos, lookupForks, query, [], leafSources);
            return (c, node, leafSources);
        }).ToList();

        var sourceMap = built.ToDictionary(x => x.c, x => (IReadOnlyList<ColumnSymbol>)x.leafSources.ToList());
        var output = built.Select(x => new ColumnWithProvenance(Name: x.c.Name, Type: x.c.Type.Name, Provenance: x.node)).ToList();

        return new QueryFacts(query, columns, sourceMap, output, code.GetDiagnostics(), globals, code.Syntax);
    }

    // Recursively traces a column back through the query pipeline to its leaf sources (real table columns).
    // Each call resolves one column, then recurses into its upstream sources, building a tree.
    // `path` tracks the current recursion stack to break cycles (e.g. a column that transitively references itself).
    // `leafSources` accumulates all real table columns discovered across the entire traversal.
    static ProvenanceNode BuildProvenanceNode(
        ColumnSymbol col,
        GlobalState globals,
        Dictionary<Symbol, SymbolInfo> symbolInfoMap,
        Dictionary<ColumnSymbol, int> columnTableRefPos,
        Dictionary<(ColumnSymbol, ColumnSymbol), LookupFork> lookupForks,
        string query,
        HashSet<ColumnSymbol> path,
        HashSet<ColumnSymbol> leafSources)
    {
        // Cycle guard: if we've already visited this column in the current path, stop.
        // In practice this appears unreachable through valid KQL — the SDK's sequential extend
        // binding always resolves name references to pre-existing symbols, not ones being defined
        // in the same statement, so no cycle can form in the symbol graph.
        if (path.Contains(col))
            return new ProvenanceNode(Column: col.Name);

        symbolInfoMap.TryGetValue(col, out var colInfo);

        // Invoke case: column was introduced by an invoke operator.
        if (colInfo?.InvokeContext is { } ctx)
            return BuildInvokeProvenance(col, ctx, globals, symbolInfoMap, columnTableRefPos, lookupForks, query, path, leafSources);

        // Base case: column belongs to a real table — it's a leaf, no further recursion needed.
        var table = globals.GetTable(col);
        if (table != null)
        {
            leafSources.Add(col);
            // Use the specific table-occurrence position for this column if available; this distinguishes
            // two references to the same table in separate union branches.
            var pos = columnTableRefPos.TryGetValue(col, out var specificPos)
                ? specificPos
                : (symbolInfoMap.TryGetValue(table, out var tblInfo) ? tblInfo.FirstPosition : 0);
            return new ProvenanceNode(Column: col.Name, Table: table.Name, Position: BuildPosition(query, pos));
        }

        var op = GetEnclosingOperator(colInfo);
        // Use null position when there's no symbol map entry: let-bound columns with no NameDeclaration
        // in the query AST would otherwise default to position 0 (the "let" keyword), causing the UI
        // to anchor highlights at the wrong location.
        var position = colInfo != null ? BuildPosition(query, colInfo.FirstPosition) : null;

        var originalColumns = GetProvenanceSources(col, colInfo);
        // No upstream sources found (e.g. a literal or aggregate with no column references).
        if (originalColumns.Count == 0)
            return new ProvenanceNode(Column: col.Name, Operator: op, Position: position);

        // Recursive case: descend into each upstream column, collecting their provenance nodes.
        path.Add(col);
        var sources = originalColumns
            .Select(orig => BuildProvenanceNode(orig, globals, symbolInfoMap, columnTableRefPos, lookupForks, query, path, leafSources))
            .ToArray();
        path.Remove(col);

        // Lookup key fork: the SDK collapses both branches to a single position (and flattens a rename's
        // OriginalColumns straight to this same left/right key pair), so match on the pair, fix each
        // branch's base position from the extracted occurrences, and mark the node with the join kind.
        // A bare key wrapper has no operator of its own → label it "lookup"; a rename keeps its operator.
        if (originalColumns.Count == 2 && lookupForks.TryGetValue((originalColumns[0], originalColumns[1]), out var fork))
        {
            sources[0] = sources[0] with { Position = BuildPosition(query, fork.LeftPos) };
            sources[1] = sources[1] with { Position = BuildPosition(query, fork.RightPos) };
            return new ProvenanceNode(Column: col.Name, Operator: op ?? "lookup", Kind: fork.Kind, Position: position, Sources: sources);
        }

        return new ProvenanceNode(Column: col.Name, Operator: op, Position: position, Sources: sources);
    }

    // Returns the immediate upstream columns that `col` is derived from.
    // Prefers the Kusto SDK's built-in OriginalColumns, but this is only populated for certain operators like project-rename.
    // For more complex operators like extend, summarize, etc, falls back to walking the AST expression for any column name references
    // (e.g. `extend foo = bar + baz` → [bar, baz]).
    static IReadOnlyList<ColumnSymbol> GetProvenanceSources(ColumnSymbol col, SymbolInfo? info)
    {
        if (col.OriginalColumns.Count > 0) return col.OriginalColumns;

        if (info?.NameDeclaration?.Parent is SimpleNamedExpression sne)
        {
            return sne.Expression
                .GetDescendantsOrSelf<NameReference>()
                .Select(nr => nr.ReferencedSymbol as ColumnSymbol)
                .Where(c => c != null && c != col)
                .Distinct()
                .Cast<ColumnSymbol>()
                .ToList();
        }

        if (info?.NameDeclaration?.GetFirstAncestor<CompoundNamedExpression>() is { } cne)
        {
            return cne.Expression
                .GetDescendantsOrSelf<NameReference>()
                .Select(nr => nr.ReferencedSymbol as ColumnSymbol)
                .Where(c => c != null && c != col)
                .Distinct()
                .Cast<ColumnSymbol>()
                .ToList();
        }

        return [];
    }

    // Returns the KQL operator keyword (e.g. "extend", "project") where this column was declared.
    static string? GetEnclosingOperator(SymbolInfo? info) =>
        info?.NameDeclaration?.GetFirstAncestor<QueryOperator>()?.GetFirstToken()?.Text.ToLowerInvariant();

    // Converts a zero-based character offset in the query string to a 1-based line/column position.
    static Position BuildPosition(string query, int pos)
    {
        int line = 1, col = 1;
        for (int i = 0; i < pos && i < query.Length; i++)
        {
            if (query[i] == '\n') { line++; col = 1; }
            else col++;
        }
        return new Position(Abs: pos, Line: line, Column: col);
    }

    // Single AST walk that builds a unified symbol map used throughout provenance tracing.
    // For each symbol encountered, records: the earliest text position, the declaration node (columns only),
    // and invoke context (for columns introduced via an invoke operator).
    // Also computes columnTableRefPos: for each real table column referenced in the query, the position
    // of the specific table occurrence that immediately precedes that column reference. This lets us
    // distinguish e.g. "DeviceEvents@P1" from "DeviceEvents@P2" when the same table appears in two
    // separate union branches.
    static (Dictionary<Symbol, SymbolInfo> map, Dictionary<ColumnSymbol, int> columnTableRefPos, Dictionary<(ColumnSymbol, ColumnSymbol), LookupFork> lookupForks)
        BuildSymbolInfoMap(SyntaxNode root, GlobalState globals)
    {
        var map = new Dictionary<Symbol, SymbolInfo>();
        var tableRefs = new List<(TableSymbol tbl, int pos)>();
        var colRefs = new List<(ColumnSymbol col, int pos)>();
        // Lookup key forks: deferred until tableRefs is complete so we can resolve each branch's
        // base position from the left-input and right-enrichment spans. (col, leftKey, rightKey, kind, spans).
        var pendingForks = new List<(ColumnSymbol outCol, ColumnSymbol leftKey, ColumnSymbol rightKey, string kind, int leftStart, int leftEnd, int rightStart, int rightEnd)>();

        SyntaxElement.WalkNodes(root, n =>
        {
            if (n is NameDeclaration || n is NameReference)
            {
                switch (n.ReferencedSymbol)
                {
                    case ColumnSymbol col:
                        if (n is NameDeclaration nd)
                            map[col] = new SymbolInfo(n.TextStart, NameDeclaration: nd);
                        else
                            colRefs.Add((col, n.TextStart));
                        break;
                    case TableSymbol tbl:
                        tableRefs.Add((tbl, n.TextStart));
                        if (!map.TryGetValue(tbl, out var tblInfo) || n.TextStart < tblInfo.FirstPosition)
                            map[tbl] = new SymbolInfo(n.TextStart);
                        break;
                }
            }

            if (n is PipeExpression pipe)
            {
                if (pipe.Operator is InvokeOperator invoke)
                {
                    var resultTable = pipe.Expression.ResultType as TableSymbol;
                    var ctx = BuildInvokeContext(invoke, resultTable);
                    var inputNames = new HashSet<string>((resultTable?.Columns ?? []).Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
                    foreach (var col in (pipe.ResultType as TableSymbol)?.Columns ?? [])
                    {
                        if (!inputNames.Contains(col.Name))
                        {
                            map.TryGetValue(col, out var existing);
                            // Invoke-introduced columns typically have no NameDeclaration/NameReference in the
                            // main query AST, so existing is usually null and InvokePos becomes FirstPosition.
                            map[col] = new SymbolInfo(
                                FirstPosition: existing?.FirstPosition ?? ctx.InvokePos,
                                NameDeclaration: existing?.NameDeclaration,
                                InvokeContext: ctx);
                        }
                    }
                }

                // A lookup coalesces each `on` key into one output column whose OriginalColumns are the
                // left-input and right-enrichment branches. Record the branch spans so their distinct base
                // positions can be resolved once tableRefs is complete.
                if (pipe.Operator is LookupOperator lookup && pipe.ResultType is TableSymbol lookupOut)
                {
                    var kind = GetJoinKind(lookup.Parameters, defaultKind: "leftouter");
                    foreach (var keyName in OnClauseKeyNames(lookup.LookupClause))
                    {
                        var outCol = lookupOut.Columns.FirstOrDefault(c => string.Equals(c.Name, keyName, StringComparison.OrdinalIgnoreCase));
                        if (outCol == null || outCol.OriginalColumns.Count < 2) continue;
                        pendingForks.Add((outCol, outCol.OriginalColumns[0], outCol.OriginalColumns[1], kind,
                            pipe.Expression.TextStart, pipe.Expression.End,
                            lookup.Expression.TextStart, lookup.Expression.End));
                    }
                }
            }
        });

        // For each real table column referenced in the query, find the position of the specific
        // table occurrence that immediately precedes it (the last table ref for that table at a
        // position < the column ref). This distinguishes two references to the same table in
        // separate union branches (e.g. Source1 = DeviceEvents | ... and Source2 = DeviceEvents | ...).
        var columnTableRefPos = new Dictionary<ColumnSymbol, int>();
        foreach (var (col, colPos) in colRefs)
        {
            if (globals.GetTable(col) is not { } tbl) continue;
            int best = -1;
            foreach (var (refTbl, refPos) in tableRefs)
                if (refTbl == tbl && refPos < colPos && refPos > best)
                    best = refPos;
            if (best >= 0)
                columnTableRefPos[col] = best;
        }

        // Resolve each fork branch to the occurrence of its base table within that branch's text span:
        // the left key inside the left input, the right key inside the right enrichment. This recovers
        // the distinct positions the SDK collapses for a same-table key.
        var lookupForks = new Dictionary<(ColumnSymbol, ColumnSymbol), LookupFork>();
        foreach (var f in pendingForks)
        {
            int leftPos = TableRefInSpan(tableRefs, globals.GetTable(f.leftKey), f.leftStart, f.leftEnd);
            int rightPos = TableRefInSpan(tableRefs, globals.GetTable(f.rightKey), f.rightStart, f.rightEnd);
            lookupForks[(f.leftKey, f.rightKey)] = new LookupFork(f.kind, leftPos, rightPos);
        }

        return (map, columnTableRefPos, lookupForks);
    }

    // The last occurrence of `table` whose position falls within [start, end) — the table reference
    // feeding one branch of a join/lookup key.
    static int TableRefInSpan(List<(TableSymbol tbl, int pos)> tableRefs, TableSymbol? table, int start, int end)
    {
        int best = -1;
        foreach (var (refTbl, refPos) in tableRefs)
            if (refTbl == table && refPos >= start && refPos < end && refPos > best)
                best = refPos;
        return best < 0 ? start : best;
    }

    // The join/lookup `kind=` parameter value, or the operator's default when unspecified.
    static string GetJoinKind(SyntaxList<NamedParameter> parameters, string defaultKind)
    {
        foreach (var p in parameters)
            if (string.Equals(p.Name.SimpleName, "kind", StringComparison.OrdinalIgnoreCase))
                return (p.Expression.GetFirstToken()?.Text ?? defaultKind).Trim().ToLowerInvariant();
        return defaultKind;
    }

    // The key column names of an `on K1, K2, ...` clause.
    static IEnumerable<string> OnClauseKeyNames(JoinConditionClause? clause)
    {
        if (clause is not JoinOnClause on) yield break;
        foreach (var sep in on.Expressions)
            if (sep.Element is NameReference nr)
                yield return nr.SimpleName;
    }

    static InvokeContext BuildInvokeContext(InvokeOperator invoke, TableSymbol? resultTable)
    {
        var funcCall = (FunctionCallExpression)invoke.Function;
        var sig = ((FunctionSymbol)funcCall.Name.ReferencedSymbol).Signatures.First();

        // Kusto SDK serializes let-defined function bodies with outer braces; unwrap them.
        var bodyText = sig.Body.Trim();
        if (bodyText.StartsWith('{') && bodyText.EndsWith('}'))
            bodyText = bodyText[1..^1].Trim();

        var bodySyntax = KustoCode.Parse(bodyText).Syntax;
        var scalarParams = sig.Parameters.Where(p => !p.IsTabular).ToList();
        var args = funcCall.ArgumentList.Expressions;
        var scalarParamNames = scalarParams.Select(p => p.Name).ToArray();
        var scalarArgPositions = Enumerable.Range(0, Math.Min(scalarParams.Count, args.Count))
            .Select(i => args[i].Element.TextStart)
            .ToArray();

        return new InvokeContext(invoke.InvokeKeyword.TextStart, scalarParamNames, scalarArgPositions, bodySyntax, resultTable);
    }

    // Builds a flattened provenance node for a column introduced by an invoke operator.
    // Scalar param references → invoke-boundary leaves. Table column references → recurse into source.
    static ProvenanceNode BuildInvokeProvenance(
        ColumnSymbol col, InvokeContext ctx, GlobalState globals,
        Dictionary<Symbol, SymbolInfo> symbolInfoMap, Dictionary<ColumnSymbol, int> columnTableRefPos,
        Dictionary<(ColumnSymbol, ColumnSymbol), LookupFork> lookupForks,
        string query, HashSet<ColumnSymbol> path, HashSet<ColumnSymbol> leafSources)
    {
        var invokePos = BuildPosition(query, ctx.InvokePos);
        var defExpr = FindDefiningExpression(ctx.BodySyntax, col.Name);
        if (defExpr == null)
            return new ProvenanceNode(Column: col.Name, Operator: "invoke", Position: invokePos);

        path.Add(col);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sources = new List<ProvenanceNode>();

        foreach (var nr in defExpr.GetDescendantsOrSelf<NameReference>())
        {
            var name = nr.SimpleName;
            if (!seen.Add(name)) continue;

            var scalarIdx = Array.FindIndex(ctx.ScalarParamNames, p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase));
            if (scalarIdx >= 0 && scalarIdx < ctx.ScalarArgPositions.Length)
            {
                sources.Add(new ProvenanceNode(Column: name, Operator: "invoke",
                    Position: BuildPosition(query, ctx.ScalarArgPositions[scalarIdx])));
                continue;
            }

            var sourceCol = ctx.ResultTable?.Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (sourceCol != null)
                sources.Add(BuildProvenanceNode(sourceCol, globals, symbolInfoMap, columnTableRefPos, lookupForks, query, path, leafSources));
        }

        path.Remove(col);
        return new ProvenanceNode(Column: col.Name, Operator: "invoke", Position: invokePos, Sources: (sources.Count > 0 ? sources.ToArray() : null));
    }

    // Finds the RHS expression of the first SimpleNamedExpression with the given column name in a body AST.
    static Expression? FindDefiningExpression(SyntaxNode root, string columnName)
    {
        Expression? result = null;
        SyntaxElement.WalkNodes(root, n =>
        {
            if (result != null) return;
            if (n is SimpleNamedExpression sne &&
                string.Equals(sne.Name.SimpleName, columnName, StringComparison.OrdinalIgnoreCase))
                result = sne.Expression;
        });
        return result;
    }

}
