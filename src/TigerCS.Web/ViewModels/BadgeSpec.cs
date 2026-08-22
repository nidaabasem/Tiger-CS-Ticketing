namespace TigerCS.Web.ViewModels;

/// <summary>
/// One status indicator, resolved to everything the view needs to render it
/// accessibly.
///
/// <para>
/// <b>Colour is never the only signal.</b> Every badge carries a text label,
/// and the ones that mean "something is wrong" also carry a glyph, so the
/// distinction survives greyscale, colour-blindness and a printed page.
/// <see cref="ScreenReaderText"/> supplies the meaning a sighted user gets
/// from position and colour, for the ones where the label alone is ambiguous.
/// </para>
/// </summary>
/// <param name="Label">The visible text. Never empty.</param>
/// <param name="CssModifier">The badge variant, e.g. <c>critical</c>.</param>
/// <param name="Glyph">An optional leading glyph for states that need to stand out without colour.</param>
/// <param name="ScreenReaderText">Extra context announced but not shown, when the label alone is not self-describing.</param>
public readonly record struct BadgeSpec(
    string Label,
    string CssModifier,
    string? Glyph = null,
    string? ScreenReaderText = null);
