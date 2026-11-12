using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using PaymentSim.Api.Endpoints;
using PaymentSim.Core;
using PaymentSim.Infrastructure;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders().AddSimpleConsole();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = context => context.ProblemDetails.Extensions.TryAdd("code", "9999"));
builder.Services.AddHealthChecks();
builder.Services.AddRazorPages();
builder.Services.AddDataProtection().SetApplicationName("PaymentSim")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Configuration["Seed:KeyDirectory"] ?? "appdata/keys", "data-protection")));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddPaymentSimInfrastructure(builder.Configuration);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 64 * 1024);
var app = builder.Build();
Directory.CreateDirectory("appdata");
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PaymentSimDbContext>();
    await db.Database.EnsureCreatedAsync(app.Lifetime.ApplicationStopping);
    await scope.ServiceProvider.GetRequiredService<DemoSeeder>().SeedAsync(app.Lifetime.ApplicationStopping);
}
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    try { await next(context); }
    catch (Exception ex) when (ex is PaymentException or FormatException or JsonException or BadHttpRequestException)
    {
        if (context.Response.HasStarted) throw;
        var error = ex as PaymentException;
        var status = error?.StatusCode ?? 400;
        var code = error?.Code ?? "9999";
        var message = error?.Message ?? "Invalid request format.";
        var fields = new Dictionary<string, string?>
        {
            ["type"] = "about:blank", ["title"] = "Request failed", ["status"] = status.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["detail"] = message, ["code"] = code, ["message"] = message
        };
        if (context.Items["merchant"] is Merchant merchant) GatewayEndpoints.Sign(fields, merchant);
        var extensions = new Dictionary<string, object?> { ["code"] = code, ["message"] = message };
        if (fields.TryGetValue("sign", out var sign)) extensions["sign"] = sign;
        await Results.Problem(type: "about:blank", title: "Request failed", statusCode: status, detail: message, extensions: extensions).ExecuteAsync(context);
    }
});
app.UseStaticFiles();
app.MapOpenApi();
app.MapScalarApiReference();
app.MapHealthChecks("/health");
app.MapGateway();
app.MapAdmin();
app.MapRazorPages();
app.Run();
public partial class Program;
