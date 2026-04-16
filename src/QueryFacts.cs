using Kusto.Language;
using Kusto.Language.Symbols;
using Kusto.Language.Syntax;

record InvokeContext(
    int InvokePos,
    string[] ScalarParamNames,
    int[] ScalarArgPositions,
    SyntaxNode BodySyntax,
    IReadOnlyList<ColumnSymbol> SourceColumns);

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
        // Pre-walk the AST once to build symbol-to-position and symbol-to-declaration lookups.
        var (columnRefMap, tableRefMap, declMap) = BuildRefMaps(code.Syntax);
        var invokeMap = BuildInvokeMap(code.Syntax);

        var built = columns.Select(c =>
        {
            var leafSources = new HashSet<ColumnSymbol>();
            var node = BuildProvenanceNode(c, globals, columnRefMap, tableRefMap, declMap, invokeMap, query, [], leafSources);
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
        Dictionary<ColumnSymbol, int> columnRefMap,
        Dictionary<TableSymbol, int> tableRefMap,
        Dictionary<ColumnSymbol, NameDeclaration> declMap,
        Dictionary<ColumnSymbol, InvokeContext> invokeMap,
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

        // Invoke case: column was introduced by an invoke operator.
        if (invokeMap.TryGetValue(col, out var ctx))
            return BuildInvokeProvenance(col, ctx, globals, columnRefMap, tableRefMap, declMap, invokeMap, query, path, leafSources);

        // Base case: column belongs to a real table — it's a leaf, no further recursion needed.
        var table = globals.GetTable(col);
        if (table != null)
        {
            leafSources.Add(col);
            var pos = tableRefMap.TryGetValue(table, out var p) ? p : 0;
            return new ProvenanceNode(Column: col.Name, Table: table.Name, Position: BuildPosition(query, pos));
        }

        var op = GetEnclosingOperator(col, declMap);
        var declPos = columnRefMap.TryGetValue(col, out var cp) ? cp : 0;

        var originalColumns = GetProvenanceSources(col, declMap);
        // No upstream sources found (e.g. a literal or aggregate with no column references).
        if (originalColumns.Count == 0)
            return new ProvenanceNode(Column: col.Name, Operator: op, Position: BuildPosition(query, declPos));

        // Recursive case: descend into each upstream column, collecting their provenance nodes.
        path.Add(col);
        var sources = originalColumns
            .Select(orig => BuildProvenanceNode(orig, globals, columnRefMap, tableRefMap, declMap, invokeMap, query, path, leafSources))
            .ToArray();
        path.Remove(col);

        return new ProvenanceNode(Column: col.Name, Operator: op, Position: BuildPosition(query, declPos), Sources: sources);
    }

    // Returns the immediate upstream columns that `col` is derived from.
    // Prefers the Kusto SDK's built-in OriginalColumns, but this is only populated for certain operators like project-rename.
    // For more complex operators like extend, summarize, etc, falls back to walking the AST expression for any column name references
    // (e.g. `extend foo = bar + baz` → [bar, baz]).
    static IReadOnlyList<ColumnSymbol> GetProvenanceSources(
        ColumnSymbol col,
        Dictionary<ColumnSymbol, NameDeclaration> declMap)
    {
        if (col.OriginalColumns.Count > 0) return col.OriginalColumns;

        if (declMap.TryGetValue(col, out var decl) && decl.Parent is SimpleNamedExpression sne)
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
    static string? GetEnclosingOperator(ColumnSymbol col, Dictionary<ColumnSymbol, NameDeclaration> declMap) =>
        declMap.TryGetValue(col, out var decl)
            ? decl.GetFirstAncestor<QueryOperator>()?.GetFirstToken()?.Text.ToLowerInvariant()
            : null;

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

    // For each PipeExpression ending with an InvokeOperator, records which new columns invoke introduces
    // and captures everything needed to trace their provenance into the function body.
    static Dictionary<ColumnSymbol, InvokeContext> BuildInvokeMap(SyntaxNode root)
    {
        var map = new Dictionary<ColumnSymbol, InvokeContext>();
        SyntaxElement.WalkNodes(root, n =>
        {
            if (n is not PipeExpression pipe || pipe.Operator is not InvokeOperator invoke) return;

            var funcCall = invoke.Function as FunctionCallExpression;
            var sig = (funcCall?.Name.ReferencedSymbol as FunctionSymbol)?.Signatures.FirstOrDefault();
            if (sig == null || string.IsNullOrEmpty(sig.Body)) return;

            // Kusto SDK serializes let-defined function bodies with outer braces; unwrap them.
            var bodyText = sig.Body.Trim();
            if (bodyText.StartsWith('{') && bodyText.EndsWith('}'))
                bodyText = bodyText[1..^1].Trim();
            if (string.IsNullOrEmpty(bodyText)) return;

            var bodySyntax = KustoCode.Parse(bodyText).Syntax;
            var scalarParams = sig.Parameters.Where(p => !p.IsTabular).ToList();
            var args = funcCall.ArgumentList.Expressions;
            var scalarParamNames = scalarParams.Select(p => p.Name).ToArray();
            var scalarArgPositions = Enumerable.Range(0, Math.Min(scalarParams.Count, args.Count))
                .Select(i => args[i].Element.TextStart)
                .ToArray();

            var inputTable = pipe.Expression.ResultType as TableSymbol;
            var sourceColumns = inputTable?.Columns ?? (IReadOnlyList<ColumnSymbol>)[];
            var inputNames = new HashSet<string>(sourceColumns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);

            foreach (var col in (pipe.ResultType as TableSymbol)?.Columns ?? [])
            {
                if (!inputNames.Contains(col.Name))
                    map[col] = new InvokeContext(invoke.InvokeKeyword.TextStart, scalarParamNames, scalarArgPositions, bodySyntax, sourceColumns);
            }
        });
        return map;
    }

    // Builds a flattened provenance node for a column introduced by an invoke operator.
    // Scalar param references → invoke-boundary leaves. Table column references → recurse into source.
    static ProvenanceNode BuildInvokeProvenance(
        ColumnSymbol col, InvokeContext ctx, GlobalState globals,
        Dictionary<ColumnSymbol, int> columnRefMap,
        Dictionary<TableSymbol, int> tableRefMap,
        Dictionary<ColumnSymbol, NameDeclaration> declMap,
        Dictionary<ColumnSymbol, InvokeContext> invokeMap,
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

            var sourceCol = ctx.SourceColumns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (sourceCol != null)
                sources.Add(BuildProvenanceNode(sourceCol, globals, columnRefMap, tableRefMap, declMap, invokeMap, query, path, leafSources));
        }

        path.Remove(col);
        return sources.Count == 0
            ? new ProvenanceNode(Column: col.Name, Operator: "invoke", Position: invokePos)
            : new ProvenanceNode(Column: col.Name, Operator: "invoke", Position: invokePos, Sources: sources.ToArray());
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

    // Single AST walk that builds three maps used throughout provenance tracing:
    // - columnMap: earliest text position of each column symbol (for pointing to its first use)
    // - tableMap: earliest text position of each table symbol
    // - declMap: the NameDeclaration node where each column symbol is introduced
    static (Dictionary<ColumnSymbol, int>, Dictionary<TableSymbol, int>, Dictionary<ColumnSymbol, NameDeclaration>) BuildRefMaps(SyntaxNode root)
    {
        var columnMap = new Dictionary<ColumnSymbol, int>();
        var tableMap = new Dictionary<TableSymbol, int>();
        var declMap = new Dictionary<ColumnSymbol, NameDeclaration>();
        SyntaxElement.WalkNodes(root, n =>
        {
            if (n is NameDeclaration decl && decl.ReferencedSymbol is ColumnSymbol declCol && !declMap.ContainsKey(declCol))
                declMap[declCol] = decl;

            if ((n is NameDeclaration || n is NameReference) && n is SyntaxNode sn)
            {
                switch (sn.ReferencedSymbol)
                {
                    case ColumnSymbol col:
                        // Keep the earliest (leftmost) occurrence of this symbol.
                        if (!columnMap.TryGetValue(col, out var cc) || sn.TextStart < cc)
                            columnMap[col] = sn.TextStart;
                        break;
                    case TableSymbol tbl:
                        if (!tableMap.TryGetValue(tbl, out var tc) || sn.TextStart < tc)
                            tableMap[tbl] = sn.TextStart;
                        break;
                }
            }
        });
        return (columnMap, tableMap, declMap);
    }

}
