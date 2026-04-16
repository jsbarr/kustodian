using Kusto.Language;
using Xunit;

public class BadinputTests
{
    static readonly string DataDir = Path.Combine(
        Path.GetDirectoryName(typeof(BadinputTests).Assembly.Location)!, "data");

    static readonly Dictionary<string, GlobalState> Environments =
        EnvironmentLoader.Load(Path.Combine(DataDir, "environments"));

    [Fact]
    public void NullQuery_Throws() =>
        Assert.Throws<ArgumentException>(() =>
            Analyser.Analyse(new AnalyseRequest(null!, "AccountName", "defender-xdr"), Environments));

    [Fact]
    public void EmptyQuery_Throws() =>
        Assert.Throws<ArgumentException>(() =>
            Analyser.Analyse(new AnalyseRequest("", "AccountName", "defender-xdr"), Environments));

    [Fact]
    public void NullImpactedEntityField_Throws() =>
        Assert.Throws<ArgumentException>(() =>
            Analyser.Analyse(new AnalyseRequest("T | take 1", null!, "defender-xdr"), Environments));

    [Fact]
    public void EmptyImpactedEntityField_Throws() =>
        Assert.Throws<ArgumentException>(() =>
            Analyser.Analyse(new AnalyseRequest("T | take 1", "", "defender-xdr"), Environments));

    [Fact]
    public void NullEnvironment_Throws() =>
        Assert.Throws<ArgumentException>(() =>
            Analyser.Analyse(new AnalyseRequest("T | take 1", "AccountName", null!), Environments));

    [Fact]
    public void EmptyEnvironment_Throws() =>
        Assert.Throws<ArgumentException>(() =>
            Analyser.Analyse(new AnalyseRequest("T | take 1", "AccountName", ""), Environments));

    [Fact]
    public void UnknownEnvironment_Throws() =>
        Assert.Throws<ArgumentException>(() =>
            Analyser.Analyse(new AnalyseRequest("T | take 1", "AccountName", "nonexistent"), Environments));
}
