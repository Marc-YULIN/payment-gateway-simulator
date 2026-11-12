using MerchantReceiver;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders().AddSimpleConsole();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = context => context.ProblemDetails.Extensions.TryAdd("code", "9999"));
builder.Services.AddHealthChecks();
builder.Services.AddOptions<GatewayOptions>().BindConfiguration("Gateway").ValidateDataAnnotations()
    .Validate(o => o.Scheme is "HMAC-SHA256" or "MD5" or "RSA-SHA256", "Unsupported signature scheme.")
    .Validate(o => o.Scheme == "RSA-SHA256" ? File.Exists(o.PrivateKeyPemPath) && File.Exists(o.GatewayPublicKeyPemPath)
        : !string.IsNullOrEmpty(o.SharedSecret), "Signing credentials are required.").ValidateOnStart();
builder.Services.AddOptions<ReceiverOptions>().BindConfiguration("").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient("gateway", c => c.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<Receiver>();
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 64 * 1024);
var app = builder.Build();
app.UseExceptionHandler();
app.MapOpenApi();
app.MapHealthChecks("/health");
app.MapReceiver();
app.Run();
public partial class Program;
