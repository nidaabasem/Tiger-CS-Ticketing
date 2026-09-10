using System.Runtime.CompilerServices;

namespace TigerCS.Tests.Web;

/// <summary>
/// Ticket Details' Conversation History presentation (Genesys integration
/// phase 1): the calls and chats a ticket came from, in their own tab so they
/// never visually compete with the primary ticket information — the same
/// treatment Previous Tickets already gets. Presentation only; the
/// underlying read model is covered by
/// <c>GenesysConversationEndAppServiceTests</c>.
/// </summary>
public sealed class TicketDetailsConversationHistoryTests
{
    private static string SourceFile(string relativeToSrc, [CallerFilePath] string testFilePath = "")
    {
        var srcDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFilePath)!, "..", ".."));
        return Path.Combine(srcDir, relativeToSrc);
    }

    private static string TicketDetailsViewHtml() =>
        File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Pages", "TicketDetails.cshtml")));

    private static string TicketDetailsModelSource() =>
        File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Pages", "TicketDetails.cshtml.cs")));

    private static string SiteCss() =>
        File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "wwwroot", "css", "site.css")));

    [Fact]
    public void View_RendersAConversationHistorySection()
    {
        var html = TicketDetailsViewHtml();

        Assert.Contains("Conversation History", html);
        Assert.Contains("id=\"tab-conversations\"", html);
        Assert.Contains("for=\"tab-conversations\"", html);
        Assert.Contains("id=\"panel-conversations\"", html);
    }

    [Fact]
    public void View_DetailsTabIsStillSelectedByDefault_NotConversationHistory()
    {
        var html = TicketDetailsViewHtml();

        var start = html.IndexOf("id=\"tab-conversations\"", StringComparison.Ordinal);
        var tag = html[start..html.IndexOf("/>", start, StringComparison.Ordinal)];

        Assert.DoesNotContain("checked", tag);
    }

    [Fact]
    public void Css_ShowsTheConversationPanelWhenItsTabIsSelected()
    {
        // The tabs are CSS-only (no JS); a panel with no matching rule would
        // render as a tab that does nothing when clicked.
        Assert.Contains("#tab-conversations:checked ~ .tabs-body #panel-conversations", SiteCss());
    }

    [Fact]
    public void View_RendersEachInteractionsChannelAgentAndTimeWindow()
    {
        var html = TicketDetailsViewHtml();

        Assert.Contains("interaction.ChannelName", html);
        Assert.Contains("interaction.AgentName", html);
        Assert.Contains("interaction.StartedAtUtc", html);
        Assert.Contains("interaction.EndedAtUtc", html);
    }

    [Fact]
    public void View_RendersTheTranscriptWithSenderAndTimestamp()
    {
        var html = TicketDetailsViewHtml();

        Assert.Contains("interaction.Messages", html);
        Assert.Contains("message.SenderName", html);
        Assert.Contains("message.SentAtUtc", html);
        Assert.Contains("message.Body", html);
    }

    [Fact]
    public void View_ShowsTheInteractionStatus_WhichIsSeparateFromTheTicketStatus()
    {
        // "Chat ended" must never read as "ticket closed" — the interaction's
        // own status is rendered from the interaction, never from the ticket.
        var html = TicketDetailsViewHtml();

        Assert.Contains("interaction.Status", html);
        Assert.Contains("interaction.EndReason", html);
    }

    [Fact]
    public void View_DistinguishesNoConversationsFromAFailedLoad()
    {
        var html = TicketDetailsViewHtml();

        Assert.Contains("Model.InteractionsUnavailable", html);
        Assert.Contains("Conversation history is unavailable right now.", html);
        Assert.Contains("No calls or chats are recorded against this ticket.", html);
    }

    [Fact]
    public void View_ConversationPanelIsASiblingOfTheFactsPanel_NotNestedInsideIt()
    {
        var html = TicketDetailsViewHtml();

        var factsPanelEnd = html.IndexOf("</aside>", StringComparison.Ordinal);
        var panelStart = html.IndexOf("id=\"panel-conversations\"", StringComparison.Ordinal);

        Assert.True(factsPanelEnd > 0, "Expected a facts-panel <aside> in the view.");
        Assert.True(panelStart > factsPanelEnd, "The Conversation History panel must not be nested inside the facts-panel.");
    }

    [Fact]
    public void Model_LoadsInteractions_ViaTicketsApiClient_AndNeverCallsGenesysDirectly()
    {
        var source = TicketDetailsModelSource();

        Assert.Contains("ticketsApiClient.GetInteractionsAsync", source);
        Assert.Contains("Interactions", source);

        // The Web app reaches Genesys through nothing of its own: there is no
        // Genesys client, and no Genesys URL, in the page model — the
        // conversation history is read from TigerCS.Api like every other fact
        // on this page.
        Assert.DoesNotContain("GenesysApiClient", source);
        Assert.DoesNotContain("api/genesys", source);
    }
}
