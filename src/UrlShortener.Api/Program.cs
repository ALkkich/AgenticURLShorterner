using UrlShortener.Api;
using UrlShortener.Api.Api;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddUrlShortener(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.MapUrlShortenerEndpoints();

app.Run();

// Exposed for WebApplicationFactory-based integration tests.
public partial class Program;
