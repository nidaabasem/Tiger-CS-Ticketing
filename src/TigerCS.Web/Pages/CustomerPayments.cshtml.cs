using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Web.Models;
using TigerCS.Web.Services;

namespace TigerCS.Web.Pages;

public sealed class CustomerPaymentsModel(CustomerPaymentPanelLoader loader) : PageModel
{
    public CustomerPaymentPanel Panel { get; private set; } = new() { CustomerKey = "", State = PaymentPanelState.Error };
    public string? PhoneNumber { get; private set; }
    public string? CustomerKey { get; private set; }
    public async Task OnGetAsync(string? phoneNumber, string? customerKey, string? account, CancellationToken cancellationToken)
    {
        PhoneNumber = phoneNumber;
        CustomerKey = customerKey;
        if (!string.IsNullOrWhiteSpace(phoneNumber) && !string.IsNullOrWhiteSpace(customerKey))
            Panel = await loader.LoadLookupAsync(phoneNumber, customerKey, account, cancellationToken);
    }
}
