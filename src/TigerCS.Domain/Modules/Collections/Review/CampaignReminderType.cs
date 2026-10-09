namespace TigerCS.Domain.Modules.Collections.Review;

/// <summary>The five Genesys contact-list reminder types, mapped one-to-one onto the approved campaign stages.</summary>
public enum CampaignReminderType
{
    CurrentMonth = 1,
    FollowUp = 2,
    Overdue = 3,
    LegalNotice = 4,
    LegalCase = 5
}

public static class CampaignReminderTypes
{
    public static CampaignReminderType FromStage(CollectionsCampaignStage stage) => stage switch
    {
        CollectionsCampaignStage.CurrentMonthReminder => CampaignReminderType.CurrentMonth,
        CollectionsCampaignStage.FollowUpReminder => CampaignReminderType.FollowUp,
        CollectionsCampaignStage.OverdueReminder => CampaignReminderType.Overdue,
        CollectionsCampaignStage.LegalNotice => CampaignReminderType.LegalNotice,
        CollectionsCampaignStage.LegalReferral => CampaignReminderType.LegalCase,
        _ => throw new ArgumentOutOfRangeException(nameof(stage))
    };

    public static CollectionsCampaignStage ToStage(CampaignReminderType type) => type switch
    {
        CampaignReminderType.CurrentMonth => CollectionsCampaignStage.CurrentMonthReminder,
        CampaignReminderType.FollowUp => CollectionsCampaignStage.FollowUpReminder,
        CampaignReminderType.Overdue => CollectionsCampaignStage.OverdueReminder,
        CampaignReminderType.LegalNotice => CollectionsCampaignStage.LegalNotice,
        CampaignReminderType.LegalCase => CollectionsCampaignStage.LegalReferral,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    /// <summary>Default ReminderType label written to Genesys; overridable by configuration after checking the supplied templates.</summary>
    public static string DefaultLabel(CampaignReminderType type) => type switch
    {
        CampaignReminderType.CurrentMonth => "Current Month",
        CampaignReminderType.FollowUp => "Follow Up",
        CampaignReminderType.Overdue => "Overdue",
        CampaignReminderType.LegalNotice => "Legal Notice",
        CampaignReminderType.LegalCase => "Legal Case",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };
}
