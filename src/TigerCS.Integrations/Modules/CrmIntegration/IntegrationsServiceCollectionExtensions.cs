using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Integrations.Modules.EmailIntegration;

namespace TigerCS.Integrations.Modules.CrmIntegration;

public static class IntegrationsServiceCollectionExtensions
{
    public static IServiceCollection AddTigerCsIntegrations(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<CrmGatewayOptions>(configuration.GetSection(CrmGatewayOptions.SectionName));

        services.AddScoped<ICrmGateway>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<CrmGatewayOptions>>().Value;
            return options.Provider switch
            {
                "Mock" => new MockCrmGateway(),
                _ => throw new NotSupportedException(
                    $"Crm:Provider '{options.Provider}' is not supported. Only 'Mock' is implemented at this " +
                    "pilot phase (MVP-Implementation-Backlog.md S-06) — no real Tiger Group CRM endpoint details " +
                    "were available to build against. See MockCrmGateway's own remarks: it must never be " +
                    "described as production-ready, and a real ICrmGateway implementation is required before " +
                    "any other provider value can be used.")
            };
        });

        AddEmailSender(services, configuration);

        return services;
    }

    /// <summary>
    /// The email-provider boundary (ADR-0013's delivery leg / Module-Design.md's
    /// <c>IEmailGateway</c> external adapter contract). Same shape as the CRM
    /// gateway above, deliberately: provider selection is one configuration
    /// key and one switch in the composition root, so no business code ever
    /// learns which provider is in use.
    ///
    /// <para>
    /// <b>Singleton, unlike the CRM gateway.</b> <see cref="RecordingEmailSender"/>
    /// accumulates what it recorded, which is only useful if every scope
    /// shares one instance. A future real adapter that needs per-request
    /// state should be registered scoped instead — the interface does not
    /// care.
    /// </para>
    /// </summary>
    private static void AddEmailSender(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<EmailSenderOptions>(configuration.GetSection(EmailSenderOptions.SectionName));

        services.AddSingleton<RecordingEmailSender>();
        services.AddSingleton<IEmailSender>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<EmailSenderOptions>>().Value;
            return options.Provider switch
            {
                "Recording" => sp.GetRequiredService<RecordingEmailSender>(),
                _ => throw new NotSupportedException(
                    $"Notifications:Email:Provider '{options.Provider}' is not supported. Only 'Recording' is "
                    + "implemented at this pilot phase: no real email provider is confirmed. Module-Design.md names "
                    + "\"Office 365 Email\" and Solution-Analysis.md INT-05 marks its authentication "
                    + "[ASSUMPTION] SMTP relay/API key - no tenant, sender identity, relay host or credential exists "
                    + "in any merged document. A real IEmailSender implementation and confirmed operational "
                    + "configuration are required before any other provider value can be used, and no email is "
                    + "actually delivered until then. See RecordingEmailSender's own remarks.")
            };
        });
    }
}
