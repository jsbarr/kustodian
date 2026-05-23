using Kusto.Language;
using Xunit;

public class EnvironmentLoaderTests
{
    static string WriteTempDir(params (string path, string json)[] files)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        foreach (var (path, json) in files)
        {
            var fullPath = Path.Combine(dir, path.Replace('/', Path.DirectorySeparatorChar) + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, json);
        }
        return dir;
    }

    [Fact]
    public void Load_EmptyManifest_ReturnsEnvironmentWithNoTablesOrFunctions()
    {
        var dir = WriteTempDir(("environments/empty-env", "{}"));
        try
        {
            var envs = EnvironmentLoader.Load(dir);
            Assert.True(envs.ContainsKey("empty-env"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Load_ManifestWithTable_TablePresentInDatabase()
    {
        var dir = WriteTempDir(
            ("environments/test", """{"tables": ["Events"]}"""),
            ("tables/Events", """{"Timestamp": "datetime"}"""));
        try
        {
            var envs = EnvironmentLoader.Load(dir);
            Assert.Contains(envs["test"].Database.Tables, t => t.Name == "Events");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Load_ManifestWithFunction_FunctionPresentInDatabase()
    {
        var dir = WriteTempDir(
            ("environments/test", """{"functions": ["MyHelper"]}"""),
            ("functions/MyHelper", """{"paramSignature": "T:(*)", "body": "T | extend Tag = 'x'"}"""));
        try
        {
            var envs = EnvironmentLoader.Load(dir);
            Assert.Contains(envs["test"].Database.Functions, f => f.Name == "MyHelper");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Load_UnknownColumnType_ThrowsInvalidOperationException()
    {
        var dir = WriteTempDir(
            ("environments/bad", """{"tables": ["Events"]}"""),
            ("tables/Events", """{"Id": "uuid_bad_type"}"""));
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => EnvironmentLoader.Load(dir));
            Assert.Contains("uuid_bad_type", ex.Message);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Load_MissingTableFile_ThrowsNamingMissingTable()
    {
        var dir = WriteTempDir(
            ("environments/test", """{"tables": ["GhostTable"]}"""));
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => EnvironmentLoader.Load(dir));
            Assert.Contains("GhostTable", ex.Message);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Load_MissingFunctionFile_ThrowsNamingMissingFunction()
    {
        var dir = WriteTempDir(
            ("environments/test", """{"functions": ["GhostFn"]}"""));
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => EnvironmentLoader.Load(dir));
            Assert.Contains("GhostFn", ex.Message);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Load_FunctionWithParamSignatureAndBody_OutputIncludesBodyDefinedColumn()
    {
        var dir = WriteTempDir(
            ("environments/test", """{"tables": ["Src"], "functions": ["Enrich"]}"""),
            ("tables/Src", """{"Id": "string"}"""),
            ("functions/Enrich", """{"paramSignature": "T:(*), label:string", "body": "T | extend Extra = label"}"""));
        try
        {
            var envs = EnvironmentLoader.Load(dir);
            var fn = envs["test"].Database.GetFunction("Enrich");
            Assert.NotNull(fn);
            Assert.Equal(2, fn!.Signatures[0].Parameters.Count);
            var code = KustoCode.ParseAndAnalyze("Src | invoke Enrich('x')", envs["test"]);
            Assert.Empty(code.GetDiagnostics());
            var result = code.ResultType as Kusto.Language.Symbols.TableSymbol;
            Assert.NotNull(result);
            Assert.Contains(result.Columns, c => c.Name == "Extra");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Load_FunctionMissingBody_ThrowsContainingFunctionName()
    {
        var dir = WriteTempDir(
            ("environments/test", """{"functions": ["BadFunc"]}"""),
            ("functions/BadFunc", """{"paramSignature": "x:long"}"""));
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => EnvironmentLoader.Load(dir));
            Assert.Contains("BadFunc", ex.Message);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Load_MultipleEnvironments_AllPresent()
    {
        var dir = WriteTempDir(
            ("environments/env-a", """{"tables": ["T1"]}"""),
            ("environments/env-b", """{"tables": ["T2"]}"""),
            ("tables/T1", """{"X": "long"}"""),
            ("tables/T2", """{"Y": "string"}"""));
        try
        {
            var envs = EnvironmentLoader.Load(dir);
            Assert.Equal(2, envs.Count);
            Assert.True(envs.ContainsKey("env-a"));
            Assert.True(envs.ContainsKey("env-b"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Load_OverrideDirTableWins_OverrideColumnsUsed()
    {
        var baseDir = WriteTempDir(
            ("environments/test", """{"tables": ["T1"]}"""),
            ("tables/T1", """{"BaseCol": "string"}"""));
        var overrideDir = WriteTempDir(
            ("tables/T1", """{"OverrideCol": "long"}"""));
        try
        {
            var envs = EnvironmentLoader.Load(baseDir, overrideDir);
            var t1 = envs["test"].Database.Tables.First(t => t.Name == "T1");
            Assert.Contains(t1.Columns, c => c.Name == "OverrideCol");
            Assert.DoesNotContain(t1.Columns, c => c.Name == "BaseCol");
        }
        finally
        {
            Directory.Delete(baseDir, true);
            Directory.Delete(overrideDir, true);
        }
    }

    [Fact]
    public void Load_OverrideDirEnvironmentWins_OverrideEnvTablesUsed()
    {
        var baseDir = WriteTempDir(
            ("environments/test", """{"tables": ["T1"]}"""),
            ("tables/T1", """{"Col": "string"}"""),
            ("tables/T2", """{"Col": "string"}"""));
        var overrideDir = WriteTempDir(
            ("environments/test", """{"tables": ["T2"]}"""));
        try
        {
            var envs = EnvironmentLoader.Load(baseDir, overrideDir);
            var db = envs["test"].Database;
            Assert.Contains(db.Tables, t => t.Name == "T2");
            Assert.DoesNotContain(db.Tables, t => t.Name == "T1");
        }
        finally
        {
            Directory.Delete(baseDir, true);
            Directory.Delete(overrideDir, true);
        }
    }

    [Fact]
    public void Load_OverrideDirOnlyEnvironment_AppearsInResult()
    {
        var baseDir = WriteTempDir(
            ("environments/base-env", """{"tables": ["T1"]}"""),
            ("tables/T1", """{"Col": "string"}"""));
        var overrideDir = WriteTempDir(
            ("environments/override-env", """{"tables": ["T2"]}"""),
            ("tables/T2", """{"Col": "long"}"""));
        try
        {
            var envs = EnvironmentLoader.Load(baseDir, overrideDir);
            Assert.Equal(2, envs.Count);
            Assert.True(envs.ContainsKey("base-env"));
            Assert.True(envs.ContainsKey("override-env"));
        }
        finally
        {
            Directory.Delete(baseDir, true);
            Directory.Delete(overrideDir, true);
        }
    }

    [Fact]
    public void ParamParseList_ScalarOnly_ReturnsTwoParams()
    {
        var ps = Kusto.Language.Symbols.Parameter.ParseList("(x:string, y:long)");
        Assert.Equal(2, ps.Count);
    }

    [Fact]
    public void ParamParseList_TabularAndScalar_ReturnsTwoParams()
    {
        var ps = Kusto.Language.Symbols.Parameter.ParseList("(T:(*), x:string)");
        Assert.Equal(2, ps.Count);
    }

    [Fact]
    public void FunctionSymbol_StringParamListConstructor_RegistersParameters()
    {
        var fn = new Kusto.Language.Symbols.FunctionSymbol("F", "(T:(*), x:string)", "T | extend E = x");
        Assert.Equal(2, fn.Signatures[0].Parameters.Count);
    }
}
