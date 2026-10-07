extern alias TigerCsWeb;

using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.ClassificationAndRouting.Dto;
using TigerCsWeb::TigerCS.Web.Services;

namespace TigerCS.Tests.Web;

public sealed class NewTicketRequestTypePickerTests
{
    [Fact]
    public void CategoryIsRequired_AndSameNameTypesRemainSeparateBetweenCategories()
    {
        CategoryDto[] routing = [new(10, "E-mail", 1, "Customer Service"), new(20, "E-mail", 2, "Collections")];
        RequestTypeOptionDto[] types = [new(100, "E-mail", 1, 3, true), new(200, "E-mail", 2, 3, true)];
        Assert.Empty(NewTicketRequestTypePicker.Build(null, routing, types));
        var choice = Assert.Single(NewTicketRequestTypePicker.Build(2, routing, types));
        Assert.Equal("request-type:200", choice.Value);
        Assert.Equal(20, choice.CategoryId);
        Assert.Equal(200, choice.RequestTypeId);
    }

    [Fact]
    public void ExactNameMatchKeepsBothIdsOnce_AndLegacyTypesRemainAvailable()
    {
        var choices = NewTicketRequestTypePicker.Build(1,
            [new(10, " General Inquiry ", 1, "Customer Service"), new(11, "Other Inquiry", 1, "Customer Service")],
            [new(100, "general inquiry", 1, 3, true)]);
        Assert.Equal(2, choices.Count);
        var configured = Assert.Single(choices, c => c.RequestTypeId == 100);
        Assert.Equal(10, configured.CategoryId);
        var legacy = Assert.Single(choices, c => c.Value == "category:11");
        Assert.Null(legacy.RequestTypeId);
        Assert.Equal(11, legacy.CategoryId);
    }

    [Fact]
    public void UnpublishedWorkflowCannotBeBypassedThroughItsLegacyRoutingRow()
    {
        Assert.Empty(NewTicketRequestTypePicker.Build(2,
            [new(20, "Send Receipts", 2, "Collections")],
            [new(200, "Send Receipts", 2, 3, false)]));
    }

    [Fact]
    public void MissingOrAmbiguousRoutingNeverChoosesAnUnrelatedCategory()
    {
        var missing = Assert.Single(NewTicketRequestTypePicker.Build(2,
            [new(20, "Unrelated", 2, "Collections")],
            [new(200, "Send Receipts", 2, 3, true)]), c => c.RequestTypeId is not null);
        Assert.Null(missing.CategoryId);
        var ambiguous = Assert.Single(NewTicketRequestTypePicker.Build(2,
            [new(20, "Send Receipts", 2, "Collections"), new(21, "send receipts", 2, "Collections")],
            [new(200, "Send Receipts", 2, 3, true)]));
        Assert.Null(ambiguous.CategoryId);
    }
}
