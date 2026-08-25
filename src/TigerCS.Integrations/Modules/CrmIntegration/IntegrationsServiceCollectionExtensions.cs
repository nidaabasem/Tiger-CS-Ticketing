using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.CustomerLookup;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Integrations.Modules.EmailIntegration;
using TigerCS.Integrations.Modules.PactIntegration;
using TigerCS.Integrations.Modules.TasleehIntegration;

namespace TigerCS.Integrations.Modules.CrmIntegration;

public static class IntegrationsServiceCollectionExtensions
{
    public static IServiceCollection AddTigerCsIntegrations(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<CrmGatewayOptions>(configuration.GetSection(CrmGatewayOptions.SectionName));

        // ICrmGateway (unit-number lookup) and ICrmCustomerLookupGateway
        // (business-rule change: phone-based customer search,
        // CustomerLookupAppService's only caller) are deliberately separate
        // interfaces (see ICrmCustomerLookupGateway's remarks) but the same
        // "Mock" provider switch and the same single MockCrmGateway instance
        // backs both — one fixture data set, not two to keep in sync.
        services.AddScoped<MockCrmGateway>();

        services.AddScoped<ICrmGateway>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<CrmGatewayOptions>>().Value;
            return options.Provider switch
            {
                "Mock" => sp.GetRequiredService<MockCrmGateway>(),
                _ => throw new NotSupportedException(
                    $"Crm:Provider '{options.Provider}' is not supported. Only 'Mock' is implemented at this " +
                    "pilot phase (MVP-Implementation-Backlog.md S-06) — no real Tiger Group CRM endpoint details " +
                    "were available to build against. See MockCrmGateway's own remarks: it must never be " +
                    "described as production-ready, and a real ICrmGateway implementation is required before " +
                    "any other provider value can be used.")
            };
        });

        services.AddScoped<ICrmCustomerLookupGateway>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<CrmGatewayOptions>>().Value;
            return options.Provider switch
            {
                "Mock" => sp.GetRequiredService<MockCrmGateway>(),
                _ => throw new NotSupportedException(
                    $"Crm:Provider '{options.Provider}' is not supported for customer lookup either — see the " +
                    "ICrmGateway registration above for the same reasoning.")
            };
        });

        AddCrmBuyerLookupGateway(services);
        AddPactGateway(services, configuration);
        AddTasleehGateway(services, configuration);
        AddEmailSender(services, configuration);

        return services;
    }

    /// <summary>
    /// The CRM Buyer Lookup increment's own real HTTP integration — unlike
    /// <see cref="ICrmGateway"/>/<see cref="ICrmCustomerLookupGateway"/>
    /// above, there is no "Mock"/provider switch here: CRM's
    /// <c>GET /TicketingSystem/GetBuyerByPhone</c> endpoint has already been
    /// implemented and manually verified CRM-side, so <c>CrmBuyerHttpGateway</c>
    /// is always the real implementation.
    ///
    /// <para>
    /// Registered via <c>AddHttpClient&lt;TClient, TImplementation&gt;</c> —
    /// the idiomatic ASP.NET Core shape for a typed HttpClient (transient
    /// wrapper over a factory-pooled <see cref="HttpMessageHandler"/>);
    /// letting the factory own the client's lifetime is correct here even
    /// though the other CRM ports above are registered <c>Scoped</c>
    /// directly, since those hold no <see cref="HttpClient"/> at all yet.
    /// The base address is read from <see cref="CrmGatewayOptions.BaseUrl"/>
    /// lazily (per request), not at startup, so a missing/blank value never
    /// prevents the host from starting — <c>CrmBuyerHttpGateway</c> itself
    /// turns that into an <c>Unavailable</c> outcome on first use instead.
    /// </para>
    /// </summary>
    private static void AddCrmBuyerLookupGateway(IServiceCollection services)
    {
        services.AddHttpClient<ICrmBuyerLookupGateway, CrmBuyerHttpGateway>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<CrmGatewayOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(options.BaseUrl))
            {
                client.BaseAddress = new Uri(options.BaseUrl, UriKind.Absolute);
            }

            client.Timeout = TimeSpan.FromSeconds(15);
        });
    }

    /// <summary>Business-rule change: PACT phone-based customer search — same provider-switch shape as the CRM gateway above.</summary>
    private static void AddPactGateway(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PactGatewayOptions>(configuration.GetSection(PactGatewayOptions.SectionName));

        services.AddScoped<IPactGateway>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<PactGatewayOptions>>().Value;
            return options.Provider switch
            {
                "Mock" => new MockPactGateway(),
                _ => throw new NotSupportedException(
                    $"Pact:Provider '{options.Provider}' is not supported. Only 'Mock' is implemented at this " +
                    "pilot phase — no real PACT endpoint details were available to build against. See " +
                    "MockPactGateway's own remarks: it must never be described as production-ready, and a real " +
                    "IPactGateway implementation is required before any other provider value can be used.")
            };
        });
    }

    /// <summary>Business-rule change: Tasleeh phone-based customer search — same provider-switch shape as the CRM gateway above.</summary>
    private static void AddTasleehGateway(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TasleehGatewayOptions>(configuration.GetSection(TasleehGatewayOptions.SectionName));

        services.AddScoped<ITasleehGateway>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<TasleehGatewayOptions>>().Value;
            return options.Provider switch
            {
                "Mock" => new MockTasleehGateway(),
                _ => throw new NotSupportedException(
                    $"Tasleeh:Provider '{options.Provider}' is not supported. Only 'Mock' is implemented at this " +
                    "pilot phase — no real Tasleeh endpoint details were available to build against. See " +
                    "MockTasleehGateway's own remarks: it must never be described as production-ready, and a " +
                    "real ITasleehGateway implementation is required before any other provider value can be used.")
            };
        });
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
