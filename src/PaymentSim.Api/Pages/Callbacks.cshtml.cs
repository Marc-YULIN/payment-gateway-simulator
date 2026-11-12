using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using PaymentSim.Api.Endpoints;
using PaymentSim.Infrastructure;

namespace PaymentSim.Api.Pages;

public sealed class CallbacksModel(IOptions<SimulatorOptions> options) : PageModel
{
    public IActionResult OnGet(string? key)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        return AdminEndpoints.IsValidKey(key, options.Value.AdminKey) ? Page() : Unauthorized();
    }
}
