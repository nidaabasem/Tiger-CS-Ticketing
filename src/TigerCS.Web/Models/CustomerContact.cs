using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Web.Models;

/// <summary>
/// How a customer's contact points are presented: one row per real way of
/// reaching them, never one row per spelling.
///
/// <para>
/// A customer's numbers arrive from several places — the intake of each of
/// their tickets, every recorded call or chat, and the identity itself — and
/// each captured whatever the agent typed. "+971509724162", "971509724162",
/// "+971 50 972 4162" and "971-50-972-4162" are one phone, so listing the
/// last three as "Also called from" beside the first is noise that reads as
/// fact. Comparison is canonical
/// (<see cref="CustomerPhoneNumber.Normalize"/>, the application's one phone
/// rule); display is the captured string, untouched. Nothing stored is
/// rewritten, and no number is reformatted to look tidier than it was.
/// </para>
/// </summary>
public static class CustomerContact
{
    /// <summary>
    /// The customer's other numbers: every candidate whose canonical form
    /// differs from <paramref name="primary"/>'s and from each earlier one
    /// kept, in the order given, each shown exactly as it was captured. A
    /// candidate with no digits at all cannot be compared canonically, so it
    /// is deduplicated on its own literal text instead.
    /// </summary>
    public static IReadOnlyList<string> OtherPhones(string? primary, IEnumerable<string>? candidates)
    {
        if (candidates is null)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (CustomerPhoneNumber.Normalize(primary) is { Length: > 0 } canonicalPrimary)
        {
            seen.Add(canonicalPrimary);
        }

        var others = new List<string>();
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var canonical = CustomerPhoneNumber.Normalize(candidate);
            if (seen.Add(canonical.Length > 0 ? canonical : candidate.Trim()))
            {
                others.Add(candidate);
            }
        }

        return others;
    }

    /// <summary>
    /// The customer's other emails: every candidate that is not
    /// <paramref name="primary"/> and not a repeat of one already kept,
    /// compared case-insensitively because a mailbox is not case-sensitive.
    /// </summary>
    public static IReadOnlyList<string> OtherEmails(string? primary, IEnumerable<string>? candidates)
    {
        if (candidates is null)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(primary))
        {
            seen.Add(primary.Trim());
        }

        var others = new List<string>();
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && seen.Add(candidate.Trim()))
            {
                others.Add(candidate);
            }
        }

        return others;
    }
}
