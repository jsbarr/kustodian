using System.Text.Json;

var path = Path.Combine(Path.GetDirectoryName(typeof(EnvironmentLoader).Assembly.Location)!, "environments");
var environments = EnvironmentLoader.Load(path);

var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/health", () => Results.Ok());

app.MapPost("/analyse", (AnalyseRequest req) =>
{
    try { return Results.Json(Analyser.Analyse(req, environments), jsonOptions); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.Run();
