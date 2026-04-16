using Kusto.Language;
using Xunit;

public class DebugTests
{
    static readonly string DataDir = Path.Combine(
        Path.GetDirectoryName(typeof(DebugTests).Assembly.Location)!, "data");

    static readonly Dictionary<string, GlobalState> Environments =
        EnvironmentLoader.Load(Path.Combine(DataDir, "environments"));

    static readonly AnalyseRequest BaseRequest = new(
        Query: "DeviceEvents | project Timestamp, ReportId, DeviceId",
        ImpactedEntityField: "DeviceId",
        Environment: "defender-xdr");

    [Fact]
    public void Debug_False_SyntaxTreeIsNull()
    {
        var result = Analyser.Analyse(BaseRequest with { Debug = false }, Environments);
        Assert.Null(result.SyntaxTree);
    }

    [Fact]
    public void Debug_Null_SyntaxTreeIsNull()
    {
        var result = Analyser.Analyse(BaseRequest with { Debug = null }, Environments);
        Assert.Null(result.SyntaxTree);
    }

    [Fact]
    public void Debug_True_SyntaxTreeNotNull()
    {
        var result = Analyser.Analyse(BaseRequest with { Debug = true }, Environments);
        Assert.NotNull(result.SyntaxTree);
    }

    [Fact]
    public void Debug_True_RootKindIsQueryBlock()
    {
        var result = Analyser.Analyse(BaseRequest with { Debug = true }, Environments);
        Assert.Equal("QueryBlock", result.SyntaxTree!.Kind);
    }

    [Fact]
    public void Debug_True_TreeContainsToken()
    {
        var result = Analyser.Analyse(BaseRequest with { Debug = true }, Environments);
        Assert.True(HasLeaf(result.SyntaxTree!));

        static bool HasLeaf(SyntaxTreeNode node)
        {
            if (node.Token != null) return true;
            if (node.Children == null) return false;
            return node.Children.Any(c => HasLeaf(c));
        }
    }
}
