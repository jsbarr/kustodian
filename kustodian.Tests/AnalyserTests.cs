using System.Text.Json;
using Kusto.Language;
using Xunit;

public class AnalyserTests
{
    static readonly string DataDir = Path.Combine(
        Path.GetDirectoryName(typeof(AnalyserTests).Assembly.Location)!, "data");

    static readonly Dictionary<string, GlobalState> Environments =
        EnvironmentLoader.Load(DataDir);

    public static IEnumerable<object[]> TestCases()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(DataDir, "tests"), "*.json", SearchOption.AllDirectories).OrderBy(f => f))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            yield return
            [
                root.GetProperty("id").GetString()!,
                root.GetProperty("name").GetString()!,
                root.GetProperty("input").Clone(),
                root.GetProperty("expected").Clone()
            ];
        }
    }

    [Theory]
    [MemberData(nameof(TestCases))]
    public void RunCase(string id, string name, JsonElement input, JsonElement expected)
    {
        var req = new AnalyseRequest(
            input.GetProperty("query").GetString()!,
            input.GetProperty("impactedEntityField").GetString()!,
            input.GetProperty("environment").GetString()!,
            input.TryGetProperty("namingConvention", out var nc) && nc.ValueKind != JsonValueKind.Null
                ? nc.GetString() : null,
            input.TryGetProperty("provenance", out var prov) && prov.ValueKind != JsonValueKind.Null
                ? prov.GetBoolean() : null);

        var result = Analyser.Analyse(req, Environments);
        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var actual = JsonDocument.Parse(JsonSerializer.Serialize(result, jsonOptions)).RootElement;

        AssertMatches(expected, actual, "$");
    }

    static void AssertMatches(JsonElement expected, JsonElement actual, string path)
    {
        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in expected.EnumerateObject())
                {
                    Assert.True(actual.TryGetProperty(prop.Name, out var actualProp),
                        $"Property '{path}.{prop.Name}' not found");
                    AssertMatches(prop.Value, actualProp, $"{path}.{prop.Name}");
                }
                break;

            case JsonValueKind.Array:
                var expectedItems = expected.EnumerateArray().ToList();
                var actualItems = actual.EnumerateArray().ToList();
                Assert.True(expectedItems.Count == actualItems.Count,
                    $"Array '{path}' length mismatch: expected {expectedItems.Count}, got {actualItems.Count}.\nActual: [{string.Join(", ", actualItems)}]");
                foreach (var exp in expectedItems)
                {
                    var idx = actualItems.FindIndex(a => { try { AssertMatches(exp, a, ""); return true; } catch { return false; } });
                    Assert.True(idx >= 0, $"No match in '{path}' for: {exp}\nActual: [{string.Join(", ", actualItems)}]");
                    actualItems.RemoveAt(idx);
                }
                break;

            case JsonValueKind.String:
                Assert.Equal(JsonValueKind.String, actual.ValueKind);
                Assert.Equal(expected.GetString()!, actual.GetString()!);
                break;

            default:
                Assert.Equal(expected.GetRawText(), actual.GetRawText());
                break;
        }
    }

}
