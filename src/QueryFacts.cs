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
        var (symbolInfoMap, lookupRhsKeys) = BuildSymbolInfoMap(code.Syntax);

        var built = columns.Select(c =>
        {
            var leafSources = new HashSet<ColumnSymbol>();
            var node = BuildProvenanceNode(c, globals, symbolInfoMap, lookupRhsKeys, query, [], leafSources);
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
        HashSet<ColumnSymbol> lookupRhsKeys,
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
            return BuildInvokeProvenance(col, ctx, globals, symbolInfoMap, lookupRhsKeys, query, path, leafSources);

        // Base case: column belongs to a real table — it's a leaf, no further recursion needed.
        var table = globals.GetTable(col);
        if (table != null)
        {
            leafSources.Add(col);
            var pos = symbolInfoMap.TryGetValue(table, out var tblInfo) ? tblInfo.FirstPosition : 0;
            return new ProvenanceNode(Column: col.Name, Table: table.Name, Position: BuildPosition(query, pos));
        }

        var op = GetEnclosingOperator(colInfo);
        // Use null position when there's no symbol map entry: let-bound columns with no NameDeclaration
        // in the query AST would otherwise default to position 0 (the "let" keyword), causing the UI
        // to anchor highlights at the wrong location.
        var position = colInfo != null ? BuildPosition(query, colInfo.FirstPosition) : null;

        var originalColumns = GetProvenanceSources(col, colInfo, lookupRhsKeys);
        // No upstream sources found (e.g. a literal or aggregate with no column references).
        if (originalColumns.Count == 0)
            return new ProvenanceNode(Column: col.Name, Operator: op, Position: position);

        // Recursive case: descend into each upstream column, collecting their provenance nodes.
        path.Add(col);
        var sources = originalColumns
            .Select(orig => BuildProvenanceNode(orig, globals, symbolInfoMap, lookupRhsKeys, query, path, leafSources))
            .ToArray();
        path.Remove(col);

        return new ProvenanceNode(Column: col.Name, Operator: op, Position: position, Sources: sources);
    }

    // Returns the immediate upstream columns that `col` is derived from.
    // Prefers the Kusto SDK's built-in OriginalColumns, but this is only populated for certain operators like project-rename.
    // For more complex operators like extend, summarize, etc, falls back to walking the AST expression for any column name references
    // (e.g. `extend foo = bar + baz` → [bar, baz]).
    static IReadOnlyList<ColumnSymbol> GetProvenanceSources(ColumnSymbol col, SymbolInfo? info, HashSet<ColumnSymbol> lookupRhsKeys)
    {
        var originals = col.OriginalColumns;
        if (originals.Count > 0)
        {
            // Filter out lookup RHS key columns: the lookup binder merges [LHS, RHS] into one column
            // (SDK VisitLookupOperator). Provenance follows only the LHS — the key value always comes
            // from the driver table. lookupRhsKeys tracks which symbols are RHS keys across all lookup
            // operators in this query, so the filter is exact rather than heuristic.
            if (lookupRhsKeys.Count > 0)
            {
                var filtered = originals.Where(c => !lookupRhsKeys.Contains(c)).ToList();
                if (filtered.Count != originals.Count)
                    return filtered;
            }
            return originals;
        }

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
    // Also returns the set of lookup RHS key symbols — the right-hand side column from each lookup key
    // merge — so GetProvenanceSources can filter them from any column's OriginalColumns.
    static (Dictionary<Symbol, SymbolInfo> map, HashSet<ColumnSymbol> lookupRhsKeys) BuildSymbolInfoMap(SyntaxNode root)
    {
        var map = new Dictionary<Symbol, SymbolInfo>();
        var lookupRhsKeys = new HashSet<ColumnSymbol>();

        SyntaxElement.WalkNodes(root, n =>
        {
            if (n is NameDeclaration || n is NameReference)
            {
                switch (n.ReferencedSymbol)
                {
                    case ColumnSymbol col:
                        if (n is NameDeclaration nd)
                            map[col] = new SymbolInfo(n.TextStart, NameDeclaration: nd);
                        break;
                    case TableSymbol tbl:
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
                else if (pipe.Operator is LookupOperator)
                {
                    // Collect the RHS key symbol from each lookup-merged key column.
                    // The lookup binder creates OriginalColumns = [LHS, RHS] for the merged key.
                    // OriginalColumns[1] is the RHS symbol; we track it so GetProvenanceSources
                    // can filter it out wherever it appears (including after union flattening).
                    foreach (var col in (pipe.ResultType as TableSymbol)?.Columns ?? [])
                    {
                        if (col.OriginalColumns.Count == 2 &&
                            string.Equals(col.OriginalColumns[0].Name, col.Name, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(col.OriginalColumns[1].Name, col.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            lookupRhsKeys.Add(col.OriginalColumns[1]);
                        }
                    }
                }
            }
        });

        return (map, lookupRhsKeys);
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
        Dictionary<Symbol, SymbolInfo> symbolInfoMap, HashSet<ColumnSymbol> lookupRhsKeys,
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
                sources.Add(BuildProvenanceNode(sourceCol, globals, symbolInfoMap, lookupRhsKeys, query, path, leafSources));
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
