using Kusto.Language;
using Kusto.Language.Symbols;
using Xunit;

public class QueryFactsTests
{
    static GlobalState Env(string table, params (string name, TypeSymbol type)[] cols) =>
        GlobalState.Default.WithDatabase(new DatabaseSymbol("db",
            new TableSymbol(table, cols.Select(c => new ColumnSymbol(c.name, c.type)).ToArray())));

    [Fact]
    public void Build_ExtendDerivedColumn_SourceMapContainsOriginTableColumn()
    {
        var globals = Env("T", ("A", ScalarTypes.Long));
        var facts = QueryFacts.Build("T | extend B = A", globals);

        var b = facts.Columns.Single(c => c.Name == "B");
        var sources = facts.SourceMap[b];
        Assert.Single(sources);
        Assert.Equal("A", sources[0].Name);
    }

    [Fact]
    public void Build_AggregateColumn_SourceMapIsEmpty()
    {
        var globals = Env("T", ("A", ScalarTypes.Long));
        var facts = QueryFacts.Build("T | summarize Count = count()", globals);

        var count = facts.Columns.Single(c => c.Name == "Count");
        Assert.Empty(facts.SourceMap[count]);
    }

    [Fact]
    public void Build_TableColumn_ProvenanceHasTableName()
    {
        var globals = Env("Events", ("Timestamp", ScalarTypes.DateTime));
        var facts = QueryFacts.Build("Events | project Timestamp", globals);

        var ts = facts.Output.Single(c => c.Name == "Timestamp");
        Assert.Equal("Events", ts.Provenance?.Table);
    }

    [Fact]
    public void Build_MultilineQuery_ProvenancePositionReflectsSourceLine()
    {
        var globals = Env("T", ("A", ScalarTypes.Long));
        var facts = QueryFacts.Build("T\n| extend B = A", globals);

        var b = facts.Output.Single(c => c.Name == "B");
        Assert.Equal(2, b.Provenance?.Position?.Line);
    }

    [Fact]
    public void Build_MutualShadowExtend_TerminatesAndContainsBothColumns()
    {
        // T | extend A = B, B = A where T has both columns as table columns.
        // Verifies the provenance builder terminates under mutual shadowing
        // (each new column's source resolves to the original table column, not the shadowed one).
        var globals = Env("T", ("A", ScalarTypes.Long), ("B", ScalarTypes.Long));
        var facts = QueryFacts.Build("T | extend A = B, B = A", globals);

        Assert.Contains(facts.Output, c => c.Name == "A");
        Assert.Contains(facts.Output, c => c.Name == "B");
    }
}
