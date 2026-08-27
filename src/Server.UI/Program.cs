using CleanArchitecture.Blazor.Application;
using CleanArchitecture.Blazor.Infrastructure;
using CleanArchitecture.Blazor.Infrastructure.Extensions;
using CleanArchitecture.Blazor.Server.UI;


var builder = WebApplication.CreateBuilder(args);
builder.RegisterSerilog();
builder.WebHost.UseStaticWebAssets();

builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration)
    .AddServerUI(builder.Configuration);
var app = builder.Build();

app.ConfigureServer(builder.Configuration);

await app.InitializeDatabaseAsync().ConfigureAwait(false);
await app.RunAsync().ConfigureAwait(false);

/// <summary>
/// Top-level statements compile to an internal Program class, which WebApplicationFactory cannot
/// use as its entry point. Declaring it public here is what lets the HTTP integration harness boot
/// THIS application - the real pipeline, the real middleware order - rather than a reconstruction
/// of it. Nothing else references this type.
/// </summary>
public partial class Program;
