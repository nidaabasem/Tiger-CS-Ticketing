using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Integrations.Modules.EmailIntegration;

namespace TigerCS.Tests.IdentityAndAccess.Integration;

/// <summary>
/// The integration host must be isolated from whatever the committed or local appsettings files carry: no test may be redirected to a real mailbox or phone,
/// and no test may be able to send a real email. (A base-file edit once switched on VerificationTesting with a real EmailOverride and every CRM-document test
/// then observed that address instead of the fixture's CRM address.)
/// </summary>
public sealed class TestHostIsolationTests
{
    [Fact]
    public void TheIntegrationHost_HasNoVerificationRedirection_NoTestMailbox_AndNoSmtpPassword_WhateverTheAppsettingsSay()
    {
        using var factory = new TigerCsApiFactory(); using var _ = factory.CreateClient();
        var config = factory.Services.GetRequiredService<IConfiguration>();
        Assert.False(config.GetValue<bool>("VerificationTesting:Enabled"));
        Assert.True(string.IsNullOrEmpty(config["VerificationTesting:EmailOverride"])); Assert.True(string.IsNullOrEmpty(config["VerificationTesting:SmsOverride"]));
        Assert.True(string.IsNullOrEmpty(config["CustomerNotifications:TestEmail"])); Assert.True(string.IsNullOrEmpty(config["CustomerNotifications:TestPhone"]));
        Assert.True(string.IsNullOrEmpty(config["EmailNotifications:Password"]));
    }

    [Fact]
    public void TheIntegrationHost_CanOnlySendEmailInMemory_NeverThroughSmtp()
    {
        using var factory = new TigerCsApiFactory(); using var _ = factory.CreateClient();
        var sender = factory.Services.GetRequiredService<IEmailSender>();
        Assert.False(sender.SendsRealMail);                                                           // the sender every service resolves (behind its delivery guard) records instead of sending
        Assert.False(factory.EmailSender.SendsRealMail); Assert.IsType<RecordingEmailSender>(factory.EmailSender);   // ... into the in-process recorder: nothing leaves the process
        Assert.Equal("Recording", factory.Services.GetRequiredService<IConfiguration>()["EmailNotifications:Provider"]);
    }

    private static string ApiSettingsPath([System.Runtime.CompilerServices.CallerFilePath] string testFilePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFilePath)!, "..", "..", "..", "TigerCS.Api", "appsettings.json"));

    [Fact]
    public void TheShippedBaseConfiguration_CarriesNoCredential_NoPersonalMailbox_AndNoVerificationRedirection()
    {
        // Real deployments get their secrets and test mailboxes from their own environment, never from the base file every environment (and every test) loads.
        using var doc = JsonDocument.Parse(File.ReadAllText(ApiSettingsPath()));
        var root = doc.RootElement;
        Assert.Equal("", root.GetProperty("EmailNotifications").GetProperty("Password").GetString());
        Assert.False(root.GetProperty("VerificationTesting").GetProperty("Enabled").GetBoolean());
        Assert.Equal("", root.GetProperty("VerificationTesting").GetProperty("EmailOverride").GetString());
        Assert.Equal("", root.GetProperty("CustomerNotifications").GetProperty("TestEmail").GetString());
    }
}
