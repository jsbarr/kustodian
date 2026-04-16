using System.Text.Json;
using Kusto.Language;
using Kusto.Language.Symbols;

record EnvFunctionConfig(string? ParamSignature, string? Body);
record EnvConfig(Dictionary<string, Dictionary<string, string>>? Tables, Dictionary<string, EnvFunctionConfig>? Functions);

public static class EnvironmentLoader
{
    static TypeSymbol MapType(string type) => type switch
    {
        "object" => ScalarTypes.DynamicBag,
        "array" => ScalarTypes.DynamicArray,
        _ => ScalarTypes.GetSymbol(type)
            ?? throw new InvalidOperationException($"Unknown column type: '{type}'")
    };

    // Translates a JSON environment config into a Kusto GlobalState so the SDK can
    // perform full semantic analysis (type resolution, column binding) against it.
    static GlobalState BuildGlobalState(EnvConfig config)
    {
        var tables = (config.Tables ?? new())
            .Select(t => (Symbol)new TableSymbol(t.Key,
                t.Value.Select(c => new ColumnSymbol(c.Key, MapType(c.Value))).ToArray()));

        var functions = (config.Functions ?? new())
            .Select(kvp =>
            {
                var name = kvp.Key;
                var cfg = kvp.Value;
                if (string.IsNullOrWhiteSpace(cfg.Body))
                    throw new InvalidOperationException($"Function '{name}': body is required");
                try
                {
                    return (Symbol)new FunctionSymbol(name, $"({cfg.ParamSignature ?? ""})", cfg.Body);
                }
                catch (Exception ex) when (ex is not InvalidOperationException)
                {
                    throw new InvalidOperationException($"Function '{name}': {ex.Message}", ex);
                }
            });

        return GlobalState.Default.WithDatabase(new DatabaseSymbol("db", tables.Concat(functions)));
    }

    public static Dictionary<string, GlobalState> Load(string directory) =>
        Directory.GetFiles(directory, "*.json")
            .ToDictionary(
                f => Path.GetFileNameWithoutExtension(f),
                f => BuildGlobalState(JsonSerializer.Deserialize<EnvConfig>(
                    File.ReadAllText(f),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!));
}
