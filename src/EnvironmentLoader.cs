using System.Text.Json;
using Kusto.Language;
using Kusto.Language.Symbols;

record EnvFunctionConfig(string? ParamSignature, string? Body);
record EnvManifest(string[]? Tables, string[]? Functions);

public static class EnvironmentLoader
{
    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    static TypeSymbol MapType(string type) => type switch
    {
        "object" => ScalarTypes.DynamicBag,
        "array" => ScalarTypes.DynamicArray,
        _ => ScalarTypes.GetSymbol(type)
            ?? throw new InvalidOperationException($"Unknown column type: '{type}'")
    };

    static T Deserialize<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOpts)!;

    static string ResolveFile(string name, string subdir, string baseDir, string? overrideDir)
    {
        if (overrideDir != null)
        {
            var p = Path.Combine(overrideDir, subdir, $"{name}.json");
            if (File.Exists(p)) return p;
        }
        var basePath = Path.Combine(baseDir, subdir, $"{name}.json");
        if (File.Exists(basePath)) return basePath;
        throw new InvalidOperationException($"{subdir[..^1]} file not found: '{name}'");
    }

    static GlobalState BuildGlobalState(EnvManifest manifest, string baseDir, string? overrideDir)
    {
        var tables = (manifest.Tables ?? [])
            .Select(name =>
            {
                var path = ResolveFile(name, "tables", baseDir, overrideDir);
                var columns = Deserialize<Dictionary<string, string>>(path);
                return (Symbol)new TableSymbol(name,
                    columns.Select(c => new ColumnSymbol(c.Key, MapType(c.Value))).ToArray());
            });

        var functions = (manifest.Functions ?? [])
            .Select(name =>
            {
                var path = ResolveFile(name, "functions", baseDir, overrideDir);
                var cfg = Deserialize<EnvFunctionConfig>(path);
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

    public static Dictionary<string, GlobalState> Load(string baseDir, string? overrideDir = null)
    {
        var envFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var baseEnvDir = Path.Combine(baseDir, "environments");
        if (Directory.Exists(baseEnvDir))
            foreach (var f in Directory.GetFiles(baseEnvDir, "*.json"))
                envFiles[Path.GetFileNameWithoutExtension(f)] = f;

        if (overrideDir != null)
        {
            var overrideEnvDir = Path.Combine(overrideDir, "environments");
            if (Directory.Exists(overrideEnvDir))
                foreach (var f in Directory.GetFiles(overrideEnvDir, "*.json"))
                    envFiles[Path.GetFileNameWithoutExtension(f)] = f;
        }

        return envFiles.ToDictionary(
            kvp => kvp.Key,
            kvp => BuildGlobalState(Deserialize<EnvManifest>(kvp.Value), baseDir, overrideDir));
    }
}
