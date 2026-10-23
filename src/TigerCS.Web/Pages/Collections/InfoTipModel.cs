using Microsoft.AspNetCore.Html;

namespace TigerCS.Web.Pages.Collections;

/// <summary>
/// One compact Info icon with its explanation (see <c>_InfoTip.cshtml</c>). <paramref name="Id"/> must be unique on the page: the icon points at it with aria-describedby.
/// <paramref name="Label"/> names the thing explained and becomes the icon's accessible name. <paramref name="Hook"/> is an existing data-* attribute (for example
/// <c>data-approve-reason</c>) that stays on the element holding the text, so tests and scripts keep finding the explanation where they always did.
/// <paramref name="Html"/> (already encoded by the caller) replaces <paramref name="Text"/> when the explanation needs its own marked-up parts, such as existing data-* hooks.
/// </summary>
public sealed record InfoTipModel(string Id, string Label, string Text, string? Hook = null, IHtmlContent? Html = null)
{
    /// <summary>The hook is always a literal from the page markup, never user input; anything but a data-* attribute name is dropped.</summary>
    public string? SafeHook => Hook is { Length: > 0 } h && h.StartsWith("data-", StringComparison.Ordinal) && h.All(c => char.IsAsciiLetterLower(c) || c == '-') ? h : null;
}
