using Kusto.Language;
using Kusto.Language.Symbols;
using Xunit;
using Xunit.Abstractions;

public class DiagLookupTest(ITestOutputHelper output)
{
    static GlobalState ThreeTableEnv() =>
        GlobalState.Default.WithDatabase(new DatabaseSymbol("db",
            new TableSymbol("DeviceEvents",
                new ColumnSymbol("DeviceId", ScalarTypes.String),
                new ColumnSymbol("FileName", ScalarTypes.String)),
            new TableSymbol("DeviceProcessEvents",
                new ColumnSymbol("DeviceId", ScalarTypes.String),
                new ColumnSymbol("FileName", ScalarTypes.String)),
            new TableSymbol("DeviceFileEvents",
                new ColumnSymbol("DeviceId", ScalarTypes.String))));

    const string Q =
        "let ExtraData = DeviceFileEvents | summarize Count=count() by DeviceId;\n" +
        "let Subsearch1 = DeviceEvents | where FileName=~\"evil.exe\" | lookup ExtraData on DeviceId;\n" +
        "let Subsearch2 = DeviceProcessEvents | where FileName=~\"evil.exe\" | lookup ExtraData on DeviceId;\n" +
        "union Subsearch1, Subsearch2";

    [Fact]
    public void Diag_PrintOriginalColumns()
    {
        var globals = ThreeTableEnv();
        var code = KustoCode.ParseAndAnalyze(Q, globals);
        var result = code.ResultType as TableSymbol;
        foreach (var col in result?.Columns ?? [])
        {
            output.WriteLine($"Col: {col.Name} (id={col.GetHashCode()}) origCount={col.OriginalColumns.Count}");
            for (int i = 0; i < col.OriginalColumns.Count; i++)
            {
                var orig = col.OriginalColumns[i];
                output.WriteLine($"  [{i}] {orig.Name} (id={orig.GetHashCode()}) tbl={globals.GetTable(orig)?.Name ?? "null"} origCount={orig.OriginalColumns.Count}");
                for (int j = 0; j < orig.OriginalColumns.Count; j++)
                {
                    var o2 = orig.OriginalColumns[j];
                    output.WriteLine($"    [{i}.{j}] {o2.Name} (id={o2.GetHashCode()}) tbl={globals.GetTable(o2)?.Name ?? "null"} origCount={o2.OriginalColumns.Count}");
                }
            }
        }
    }
}
