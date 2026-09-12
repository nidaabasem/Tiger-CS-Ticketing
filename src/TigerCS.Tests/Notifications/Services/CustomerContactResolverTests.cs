using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Application.Modules.Notifications.Services;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Notifications.Services;

public class CustomerContactResolverTests
{
    [Fact]
    public async Task VerifiedSnapshotWithEmailChannel_IsTheAuthoritativeSource()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedVerifiedTicketAsync("Ahmed@Example.com ");

        var contact = await f.CreateContactResolver().ResolveAsync(ticket);

        Assert.True(contact.IsDeliverable);
        Assert.Equal("Ahmed@Example.com", contact.EmailAddress);
        Assert.Equal("Ahmed Al-Farsi", contact.DisplayName);
        Assert.Null(contact.SkipReason);
    }

    [Fact]
    public async Task SnapshotWithPhoneChannel_FallsBackToTheOriginatingInteractionEmail()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedVerifiedTicketAsync("+971501234567");
        await f.Interactions.AddAsync(TicketInteraction.CreateFromGenesys(
            ticket.TicketId, WellKnownChannels.Phone, "+971501234567", "conv-1", null, null, null, null, null, null, "inbound",
            f.Time.GetUtcNow().UtcDateTime, isOriginatingInteraction: true, customerName: "Someone Else", customerEmail: "ahmed@example.com"));

        var contact = await f.CreateContactResolver().ResolveAsync(ticket);

        Assert.Equal("ahmed@example.com", contact.EmailAddress);
        // The verified snapshot's name still wins over the interaction's.
        Assert.Equal("Ahmed Al-Farsi", contact.DisplayName);
    }

    [Fact]
    public async Task NoSnapshotAndNoInteraction_IsNotDeliverableWithNoCustomerEmailReason()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedUnverifiedTicketAsync();

        var contact = await f.CreateContactResolver().ResolveAsync(ticket);

        Assert.False(contact.IsDeliverable);
        Assert.Null(contact.EmailAddress);
        Assert.Equal(CustomerEmailSkipReasons.NoCustomerEmail, contact.SkipReason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankInteractionEmail_CountsAsMissing(string blank)
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedGenesysTicketAsync(blank);

        var contact = await f.CreateContactResolver().ResolveAsync(ticket);

        Assert.False(contact.IsDeliverable);
        Assert.Equal(CustomerEmailSkipReasons.NoCustomerEmail, contact.SkipReason);
        Assert.Equal("Fatima Al-Mansoori", contact.DisplayName);
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("fatima@localhost")]
    [InlineData("fatima@@example.com")]
    public async Task MalformedInteractionEmail_IsInvalidNotGuessed(string malformed)
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedGenesysTicketAsync(malformed);

        var contact = await f.CreateContactResolver().ResolveAsync(ticket);

        Assert.False(contact.IsDeliverable);
        Assert.Equal(CustomerEmailSkipReasons.InvalidCustomerEmail, contact.SkipReason);
        Assert.DoesNotContain(malformed, contact.SkipReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CrmBuyerName_IsUsedWhenNoSnapshotNameExists()
    {
        var f = new NotificationServiceFixture();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var ticket = Ticket.CreateVerifiedFromCrmBuyer(
            "TG-CS-20260822-0009", department.DepartmentId, 1, 2, 3, 4,
            "Khalid Al-Nuaimi", "Tiger Tower", "1204", categoryId: 1, priorityId: 2, "Leak", f.Time.GetUtcNow().UtcDateTime);
        await f.Tickets.AddAsync(ticket);
        await f.Interactions.AddAsync(TicketInteraction.CreateFromGenesys(
            ticket.TicketId, WellKnownChannels.Phone, "+971501234567", "conv-9", null, null, null, null, null, null, "inbound",
            f.Time.GetUtcNow().UtcDateTime, isOriginatingInteraction: true, customerName: null, customerEmail: "khalid@example.com"));

        var contact = await f.CreateContactResolver().ResolveAsync(ticket);

        Assert.Equal("khalid@example.com", contact.EmailAddress);
        Assert.Equal("Khalid Al-Nuaimi", contact.DisplayName);
    }

    [Theory]
    [InlineData("ahmed@example.com", true)]
    [InlineData("  ahmed@example.com  ", true)]
    [InlineData("a.b-c+d@sub.example.co.uk", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("ahmed", false)]
    [InlineData("ahmed@localhost", false)]
    [InlineData("ahmed@.example.com", false)]
    [InlineData("ahmed@example.com.", false)]
    [InlineData("Ahmed <ahmed@example.com>", false)]
    [InlineData("ahmed@example.com other@example.com", false)]
    public void CustomerEmailAddress_IsValid_AcceptsOnlyOneBareAddress(string? value, bool expected) =>
        Assert.Equal(expected, CustomerEmailAddress.IsValid(value));

    [Theory]
    [InlineData("ahmed@example.com", "a***@example.com")]
    [InlineData("a@example.com", "a***@example.com")]
    [InlineData("nonsense", "***")]
    [InlineData("", "(none)")]
    [InlineData(null, "(none)")]
    public void CustomerEmailAddress_Mask_NeverRevealsTheLocalPart(string? value, string expected) =>
        Assert.Equal(expected, CustomerEmailAddress.Mask(value));
}
