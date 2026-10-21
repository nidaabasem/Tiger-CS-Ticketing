using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace TigerCS.Web.Pages.Collections;

/// <summary>
/// "Review &amp; Approve" no longer exists as a step: selecting, approving and sending all happen on Collections Campaigns. This route only keeps old bookmarks and links to a send
/// (<c>?dispatch=</c>) working by forwarding to the same send on Campaigns.
/// </summary>
public sealed class ReviewRedirectModel : PageModel
{
    public IActionResult OnGet(Guid? dispatch) =>
        LocalRedirect(dispatch is { } id ? $"/Collections/Campaigns?dispatch={id:D}" : "/Collections/Campaigns");
}
