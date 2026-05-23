using System.Text.Json;

var baseDir = Path.Combine(Path.GetDirectoryName(typeof(EnvironmentLoader).Assembly.Location)!, "data");
var overrideDir = Environment.GetEnvironmentVariable("KUSTODIAN_DATA_DIR");
var environments = EnvironmentLoader.Load(baseDir, overrideDir);

var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok());

app.MapGet("/environments", () =>
    Results.Json(environments.Keys.OrderBy(k => k).ToArray(), jsonOptions));

app.MapPost("/analyse", (AnalyseRequest req) =>
{
    try { return Results.Json(Analyser.Analyse(req, environments), jsonOptions); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.Run();
