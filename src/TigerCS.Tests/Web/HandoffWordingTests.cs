using System.Runtime.CompilerServices;

namespace TigerCS.Tests.Web;

/// <summary>
/// Ticket Details must never claim a customer asked for a human unless Genesys said so, and must never show a human agent for a
/// conversation the AI is handling alone. Presentation checks over the Razor page and its display helpers (the Web project is not
/// referenced by this assembly); the behaviour behind them is covered by <c>GenesysHandoffAndClassificationRegressionTests</c>.
/// </summary>
public sealed class HandoffWordingTests
{
    private static string Read(string relativeToSrc, [CallerFilePath] string testFilePath = "") =>
        File.ReadAllText(Path.Combine(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFilePath)!, "..", "..")), relativeToSrc));

    [Fact]
    public void TheActivityFeed_NoLongerHardCodesThatTheCustomerAskedForAHuman()
    {
        var page = Read("TigerCS.Web/Pages/TicketDetails.cshtml.cs");

        Assert.DoesNotContain("=> \"asked for a human agent\"", page);
        Assert.Contains("TicketDisplay.HandoffRequestedActivity(approvalEvent.Note)", page);
    }

    [Fact]
    public void TheHandoffSentence_FollowsTheStatedInitiator()
    {
        var display = Read("TigerCS.Web/Models/TicketDisplay.cs");

        Assert.Contains("\"CustomerRequestedHuman\" => \"customer asked for a human agent\"", display);
        Assert.Contains("\"AiEscalated\" => \"AI escalated the conversation to a human agent\"", display);
        Assert.Contains("_ => \"a human agent was requested\"", display);
        Assert.Contains("null or \"\" => \"Not stated by Genesys\"", display);
    }

    [Fact]
    public void TheInteractionsTab_ShowsHandlingHandoffRequestedInitiatorAndStartEnd_AndAnAgentOnlyWhenAHumanHasTheConversation()
    {
        var view = Read("TigerCS.Web/Pages/TicketDetails.cshtml");

        Assert.Contains("interaction.HandlingState is \"Human\" && !string.IsNullOrWhiteSpace(interaction.AgentName)", view);
        Assert.Contains("<dt>Handled by</dt>", view);
        Assert.Contains("<dt>Handoff requested</dt>", view);
        Assert.Contains("<dt>Initiated by</dt>", view);
        Assert.Contains("<dt>Handoff reason</dt>", view);
        Assert.Contains("<dt>Started</dt>", view);
        Assert.Contains("<dt>Ended</dt>", view);
    }

    [Fact]
    public void TheOverview_AlwaysShowsTheRequestType_AndWhoIsHandlingTheConversation()
    {
        var view = Read("TigerCS.Web/Pages/TicketDetails.cshtml");

        Assert.Contains("t.RequestTypeName ?? \"Unclassified\"", view);
        Assert.Contains("TicketDisplay.HandlingStateLabel(handlingInteraction.HandlingState, handlingInteraction.AgentName)", view);
    }
}
