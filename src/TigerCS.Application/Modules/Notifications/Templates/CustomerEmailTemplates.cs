using System.Globalization;
using System.Net;
using System.Text;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Notifications.Templates;

/// <summary>
/// The customer-facing email templates — one method per notification, all
/// rendered through one shared layout so header, footer and signature can
/// never drift between notification types.
///
/// <para>
/// <b>Every dynamic value is HTML-encoded</b> on its way into the HTML body
/// (<see cref="WebUtility.HtmlEncode(string)"/>), and the plain-text body is
/// built from the same values without markup. Names, ticket numbers,
/// request-type names and resolution notes are all customer- or
/// agent-supplied text and are treated as untrusted.
/// </para>
///
/// <para>
/// <b>Nothing internal is rendered.</b> Templates receive only the fields
/// listed on <see cref="CustomerEmailTicketModel"/>: no database or employee
/// identifiers, no queue or workflow identifiers, no SLA state, no internal
/// notes. Adding a field here is a deliberate, reviewable act.
/// </para>
/// </summary>
public static class CustomerEmailTemplates
{
    public const string BrandName = "Tiger Properties";

    public const string SignatureTeam = "Tiger Properties Customer Service";

    public static CustomerEmailContent TicketCreated(CustomerEmailTicketModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var details = new List<(string Label, string Value)>
        {
            ("Ticket number", model.TicketNumber),
            ("Received on", FormatDate(model.OccurredAtUtc))
        };

        if (!string.IsNullOrWhiteSpace(model.RequestTypeName))
        {
            details.Insert(1, ("Request type", model.RequestTypeName));
        }

        return Render(
            subject: $"Your request has been received – Ticket {model.TicketNumber}",
            headline: "We have received your request",
            greeting: Greeting(model.CustomerName),
            paragraphs:
            [
                "Thank you for contacting Tiger Properties. Your request has been received and logged with the reference below.",
                "Our team is reviewing it and will be in touch with you as soon as possible. Please quote the ticket number in any further correspondence."
            ],
            details: details);
    }

    public static CustomerEmailContent TicketResolved(CustomerEmailTicketModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var details = new List<(string Label, string Value)>
        {
            ("Ticket number", model.TicketNumber),
            ("Status", model.ResolutionStatus ?? "Resolved"),
            ("Resolved on", FormatDate(model.OccurredAtUtc))
        };

        var paragraphs = new List<string>
        {
            "We are pleased to let you know that your request has been resolved."
        };

        if (!string.IsNullOrWhiteSpace(model.ResolutionMessage))
        {
            details.Add(("Resolution", model.ResolutionMessage));
        }

        paragraphs.Add("If you feel the matter has not been fully addressed, please contact us and quote your ticket number so we can look into it again.");

        return Render(
            subject: $"Your request has been resolved – Ticket {model.TicketNumber}",
            headline: "Your request has been resolved",
            greeting: Greeting(model.CustomerName),
            paragraphs: paragraphs,
            details: details);
    }

    public static CustomerEmailContent TicketClosed(CustomerEmailTicketModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        return Render(
            subject: $"Your request has been closed – Ticket {model.TicketNumber}",
            headline: "Your request has been closed",
            greeting: Greeting(model.CustomerName),
            paragraphs:
            [
                "Your request has now been closed. Thank you for giving us the opportunity to assist you.",
                "If you need further help with this or any other matter, please contact us and we will be glad to open a new request for you."
            ],
            details:
            [
                ("Ticket number", model.TicketNumber),
                ("Closed on", FormatDate(model.OccurredAtUtc))
            ]);
    }

    public static CustomerEmailContent TicketReopened(CustomerEmailTicketModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        return Render(
            subject: $"Your request has been reopened – Ticket {model.TicketNumber}",
            headline: "Your request has been reopened",
            greeting: Greeting(model.CustomerName),
            paragraphs:
            [
                "Your request has been reopened and our team is working on it again.",
                "We will keep you informed and let you know as soon as it has been resolved."
            ],
            details:
            [
                ("Ticket number", model.TicketNumber),
                ("Reopened on", FormatDate(model.OccurredAtUtc))
            ]);
    }

    /// <summary>
    /// Customer wording for a <see cref="ResolutionOutcome"/>. Deliberately
    /// plain: "Rejected" and "Duplicate" are agent vocabulary, not something
    /// a customer should read unexplained.
    /// </summary>
    public static string DescribeResolution(ResolutionOutcome? outcome) => outcome switch
    {
        ResolutionOutcome.Cancelled => "Cancelled",
        ResolutionOutcome.Rejected => "Reviewed – no further action required",
        ResolutionOutcome.Duplicate => "Merged with an existing request",
        _ => "Resolved"
    };

    private static string Greeting(string? customerName) =>
        string.IsNullOrWhiteSpace(customerName) ? "Dear Customer," : $"Dear {customerName.Trim()},";

    private static string FormatDate(DateTime utc) =>
        utc.ToString("dd MMMM yyyy, HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static CustomerEmailContent Render(
        string subject,
        string headline,
        string greeting,
        IReadOnlyList<string> paragraphs,
        IReadOnlyList<(string Label, string Value)> details)
    {
        return new CustomerEmailContent(subject, RenderText(greeting, paragraphs, details), RenderHtml(headline, greeting, paragraphs, details));
    }

    private static string RenderText(
        string greeting, IReadOnlyList<string> paragraphs, IReadOnlyList<(string Label, string Value)> details)
    {
        var lines = new List<string> { greeting, string.Empty };

        foreach (var paragraph in paragraphs)
        {
            lines.Add(paragraph);
            lines.Add(string.Empty);
        }

        foreach (var (label, value) in details)
        {
            lines.Add($"{label}: {value}");
        }

        lines.AddRange(
        [
            string.Empty,
            "Kind regards,",
            SignatureTeam,
            string.Empty,
            $"This is an automated message from {BrandName}. Please do not reply to this email."
        ]);

        return string.Join(Environment.NewLine, lines);
    }

    private static string RenderHtml(
        string headline, string greeting, IReadOnlyList<string> paragraphs, IReadOnlyList<(string Label, string Value)> details)
    {
        var sb = new StringBuilder(2048);

        sb.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">")
          .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
          .Append("<title>").Append(E(headline)).Append("</title></head>")
          .Append("<body style=\"margin:0;padding:0;background-color:#f4f5f7;font-family:Arial,Helvetica,sans-serif;color:#1f2933;\">")
          .Append("<table role=\"presentation\" width=\"100%\" cellspacing=\"0\" cellpadding=\"0\" style=\"background-color:#f4f5f7;\"><tr><td align=\"center\" style=\"padding:24px 12px;\">")
          .Append("<table role=\"presentation\" width=\"100%\" cellspacing=\"0\" cellpadding=\"0\" style=\"max-width:600px;background-color:#ffffff;border-radius:6px;overflow:hidden;\">")
          .Append("<tr><td style=\"background-color:#1c3f60;padding:20px 24px;color:#ffffff;font-size:20px;font-weight:bold;\">")
          .Append(E(BrandName)).Append("</td></tr>")
          .Append("<tr><td style=\"padding:24px;font-size:15px;line-height:1.6;\">")
          .Append("<h1 style=\"margin:0 0 16px 0;font-size:20px;color:#1c3f60;\">").Append(E(headline)).Append("</h1>")
          .Append("<p style=\"margin:0 0 16px 0;\">").Append(E(greeting)).Append("</p>");

        foreach (var paragraph in paragraphs)
        {
            sb.Append("<p style=\"margin:0 0 16px 0;\">").Append(E(paragraph)).Append("</p>");
        }

        sb.Append("<table role=\"presentation\" cellspacing=\"0\" cellpadding=\"0\" style=\"width:100%;margin:8px 0 20px 0;border-collapse:collapse;font-size:14px;\">");
        foreach (var (label, value) in details)
        {
            sb.Append("<tr><td style=\"padding:8px 12px;border-bottom:1px solid #e4e7eb;color:#52606d;width:40%;\">")
              .Append(E(label)).Append("</td>")
              .Append("<td style=\"padding:8px 12px;border-bottom:1px solid #e4e7eb;font-weight:bold;\">")
              .Append(E(value)).Append("</td></tr>");
        }

        sb.Append("</table>")
          .Append("<p style=\"margin:0;\">Kind regards,<br>").Append(E(SignatureTeam)).Append("</p>")
          .Append("</td></tr>")
          .Append("<tr><td style=\"background-color:#f4f5f7;padding:16px 24px;font-size:12px;color:#7b8794;\">")
          .Append(E($"This is an automated message from {BrandName}. Please do not reply to this email."))
          .Append("</td></tr></table></td></tr></table></body></html>");

        return sb.ToString();
    }

    private static string E(string value) => WebUtility.HtmlEncode(value);
}

/// <summary>
/// Everything a customer email may render — and, by construction, nothing
/// else. Built by the notification handler from the ticket and its
/// resolved contact; never from request input.
/// </summary>
/// <param name="TicketNumber">The customer-facing ticket reference.</param>
/// <param name="CustomerName">Greeting name, when known.</param>
/// <param name="OccurredAtUtc">When the event happened (creation, resolution, closure, reopening).</param>
/// <param name="RequestTypeName">Request type or category name, when the ticket is classified.</param>
/// <param name="ResolutionStatus">Customer wording for the resolution outcome (resolved email only).</param>
/// <param name="ResolutionMessage">The resolution note, only when configuration allows sharing it.</param>
public sealed record CustomerEmailTicketModel(
    string TicketNumber,
    string? CustomerName,
    DateTime OccurredAtUtc,
    string? RequestTypeName = null,
    string? ResolutionStatus = null,
    string? ResolutionMessage = null);
