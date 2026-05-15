// Stub entry point — full Razor MVC implementation in subsequent milestones.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllersWithViews();
var app = builder.Build();
app.MapGet("/", () => "UI running (stub)");
app.Run();
public partial class Program { }
