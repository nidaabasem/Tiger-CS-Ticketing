using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace TigerCS.Web.Pages.Tickets.New;

/// <summary>
/// What the new-ticket wizard has established so far.
///
/// <para>
/// The wizard's steps map one-to-one onto the API's own workflow, and this
/// carries the identifiers each step produces to the next:
/// </para>
/// <list type="number">
/// <item>intake — <c>POST /api/intake-records</c> produces <see cref="IntakeRecordId"/></item>
/// <item>unit — <c>GET /api/crm/units/search</c> produces <see cref="UnitReferenceId"/></item>
/// <item>contact and confirmation — <c>POST /api/verification-sessions</c> produces <see cref="VerificationSessionId"/></item>
/// <item>classification and submit — <c>POST /api/tickets</c>, or <c>/provisional</c> on the outage path</item>
/// </list>
/// </summary>
public sealed record WizardState
{
    public long IntakeRecordId { get; init; }

    public string RawUnitNumberEntered { get; init; } = string.Empty;

    public byte? PriorityHint { get; init; }

    /// <summary>Set once a unit has been chosen from a CRM search.</summary>
    public int? UnitReferenceId { get; init; }

    public string? CrmUnitId { get; init; }

    public string? UnitLabel { get; init; }

    /// <summary>Set once a contact has been chosen and the agent has confirmed the match.</summary>
    public Guid? VerificationSessionId { get; init; }

    public string? ContactLabel { get; init; }

    /// <summary>
    /// True when the CRM could not be reached, which puts the wizard on
    /// ISSUE-006's fallback path: no verification session can exist, and only
    /// Critical/High can proceed as a provisional ticket.
    /// </summary>
    public bool CrmUnavailable { get; init; }

    /// <summary>
    /// A one-time token echoed by the submit form.
    ///
    /// <para>
    /// This is duplicate-submission protection, distinct from anti-forgery: a
    /// double-click, an impatient refresh or a browser retry would otherwise
    /// send the create request twice. The token is minted when the review step
    /// renders and cleared the moment a submission is accepted, so the second
    /// attempt is recognised and refused before it reaches the API.
    /// </para>
    /// </summary>
    public string SubmitToken { get; init; } = string.Empty;
}

/// <summary>
/// Carries <see cref="WizardState"/> between wizard steps inside a signed,
/// encrypted cookie.
///
/// <para>
/// <b>Why not a hidden form field, and why not server session state.</b> A
/// hidden field would let the browser choose which unit, contact and intake
/// record the ticket is built from - the agent could edit the confirmed
/// contact after confirming it, which defeats the point of the verification
/// step. Data Protection makes the payload unreadable and untamperable, so
/// what the review step submits is what the earlier steps actually
/// established. Server-side session state would need a distributed store this
/// deployment does not have.
/// </para>
///
/// <para>
/// The state holds identifiers and short display labels only - never a
/// contact's phone or email, which stay server-side and are read back from the
/// verification session when needed.
/// </para>
/// </summary>
public sealed class WizardStateStore(IDataProtectionProvider dataProtectionProvider)
{
    private const string CookieName = "TigerCS.NewTicket";

    private readonly IDataProtector _protector =
        dataProtectionProvider.CreateProtector("TigerCS.Web.NewTicketWizard.v1");

    public WizardState? Read(HttpContext httpContext)
    {
        if (!httpContext.Request.Cookies.TryGetValue(CookieName, out var protectedValue)
            || string.IsNullOrEmpty(protectedValue))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<WizardState>(_protector.Unprotect(protectedValue));
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Tampered, or protected by keys this instance no longer has. Either
            // way the only safe reading is "no wizard in progress".
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Write(HttpContext httpContext, WizardState state) =>
        httpContext.Response.Cookies.Append(
            CookieName,
            _protector.Protect(JsonSerializer.Serialize(state)),
            new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Strict,
                IsEssential = true,
                Path = "/",

                // Session-scoped: an abandoned wizard should not outlive the
                // browser session, and the verification session it references
                // expires on its own anyway.
                Expires = null
            });

    public void Clear(HttpContext httpContext) =>
        httpContext.Response.Cookies.Delete(
            CookieName,
            new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/" });
}
