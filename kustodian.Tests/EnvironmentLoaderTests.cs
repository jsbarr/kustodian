using Kusto.Language;
using Xunit;

public class EnvironmentLoaderTests
{
    static string WriteTempDir(params (string name, string json)[] files)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        foreach (var (name, json) in files)
            File.WriteAllText(Path.Combine(dir, $"{name}.json"), json);
        return dir;
    }

    [Fact]
    public void Load_TablesOmitted_ReturnsEnvironmentWithNoTables()
    {
        var dir = WriteTempDir(("empty-env", "{}"));
        try
        {
            var envs = EnvironmentLoader.Load(dir);
            Assert.True(envs.ContainsKey("empty-env"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Load_FunctionsIncluded_FunctionPresentInDatabase()
    {
        var dir = WriteTempDir(("test", """
            {
              "tables": { "DeviceEvents": { "Timestamp": "datetime" } },
              "functions": {
                "MyHelper": {
                  "paramSignature": "T:(*)",
                  "body": "T | extend Tag = 'x'"
                }
              }
            }
            """));

        try
        {
            var envs = EnvironmentLoader.Load(dir);
            var db = envs["test"].Database;
            Assert.Contains(db.Functions, f => f.Name == "MyHelper");
        }
        finally { Directory.Delete(dir, true); }
    }


    [Fact]
    public void Load_UnknownColumnType_ThrowsInvalidOperationException()
    {
        var dir = WriteTempDir(("bad", """
            {
              "tables": { "Events": { "Id": "uuid_bad_type" } }
            }
            """));

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => EnvironmentLoader.Load(dir));
            Assert.Contains("uuid_bad_type", ex.Message);
        }
        finally { Directory.Delete(dir, true); }
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

    [Fact]
    public void Load_FunctionWithParamSignatureAndBody_OutputIncludesBodyDefinedColumn()
    {
        var dir = WriteTempDir(("test", """
            {
              "tables": { "Src": { "Id": "string" } },
              "functions": {
                "Enrich": {
                  "paramSignature": "T:(*), label:string",
                  "body": "T | extend Extra = label"
                }
              }
            }
            """));
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
        var dir = WriteTempDir(("test", """
            {
              "functions": { "BadFunc": { "paramSignature": "x:long" } }
            }
            """));
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
            ("env-a", """{ "tables": { "T1": { "X": "long" } } }"""),
            ("env-b", """{ "tables": { "T2": { "Y": "string" } } }"""));

        try
        {
            var envs = EnvironmentLoader.Load(dir);
            Assert.Equal(2, envs.Count);
            Assert.True(envs.ContainsKey("env-a"));
            Assert.True(envs.ContainsKey("env-b"));
        }
        finally { Directory.Delete(dir, true); }
    }
}
