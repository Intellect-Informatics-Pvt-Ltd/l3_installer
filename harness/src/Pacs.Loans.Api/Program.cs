// Stub entry point — full implementation in subsequent milestones.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
var app = builder.Build();
app.MapGet("/", () => "running (stub)");
app.MapGet("/health/live",  () => Results.Ok());
app.MapGet("/health/ready", () => Results.Ok());
app.Run();
public partial class Program { }
