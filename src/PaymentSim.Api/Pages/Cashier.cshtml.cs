using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PaymentSim.Core;
using PaymentSim.Infrastructure;

namespace PaymentSim.Api.Pages;

public sealed class CashierModel(PaymentSimDbContext db, OrderService orders) : PageModel
{
    public PaymentOrder Order { get; private set; } = null!;
    public string MerchantName { get; private set; } = "";

    public async Task<IActionResult> OnGetAsync(string orderNo, CancellationToken cancellationToken)
    {
        var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(x => x.OrderNo == orderNo && x.Type == OrderType.PayIn, cancellationToken);
        if (order is null) return NotFound();
        Order = order;
        MerchantName = await db.Merchants.Where(x => x.Id == order.MerchantId).Select(x => x.Name).SingleAsync(cancellationToken);
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string orderNo, bool success, CancellationToken cancellationToken)
    {
        await orders.CompletePayInAsync(orderNo, success, cancellationToken);
        return RedirectToPage(new { orderNo });
    }
}
