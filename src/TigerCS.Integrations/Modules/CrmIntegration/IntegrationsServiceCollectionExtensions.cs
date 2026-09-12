using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.CustomerLookup;
using TigerCS.Application.Modules.CustomerVerification.PactIntegration;
using TigerCS.Application.Modules.Notifications;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Application.Modules.Notifications.Services;
using TigerCS.Integrations.Modules.EmailIntegration;
using TigerCS.Integrations.Modules.PactIntegration;
using TigerCS.Integrations.Modules.TasleehIntegration;

namespace TigerCS.Integrations.Modules.CrmIntegration;

public static class IntegrationsServiceCollectionExtensions
{
    public static IServiceCollection AddTigerCsIntegrations(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<CrmGatewayOptions>(configuration.GetSection(CrmGatewayOptions.SectionName));

        // ICrmGateway (unit/contact lookup) and ICrmCustomerLookupGateway
        // (phone-based customer search, CustomerLookupAppService's only caller)
        // are deliberately separate interfaces (see ICrmCustomerLookupGateway's
        // remarks) but share one Crm:Provider switch and one instance per
        // provider:
        //   "Http" — the standard for Development, UAT and Production. Tiger
        //            CRM publishes no endpoint for these two ports yet, so it
        //            resolves UnimplementedCrmHttpGateway, which fails closed
        //            through each port's own outage contract and never serves
        //            fixture data (see its remarks). The real CRM HTTP
        //            integration, CrmBuyerHttpGateway, is a separate port
        //            (AddCrmBuyerLookupGateway) not governed by this switch.
        //   "Mock" — MockCrmGateway, one fixture data set behind both ports.
        //            The API test host pins it (TigerCsApiFactory);
        //            CrmGatewaySafety refuses it outside Development/Testing.
        services.AddScoped<MockCrmGateway>();
        services.AddScoped<UnimplementedCrmHttpGateway>();

        services.AddScoped<ICrmGateway>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<CrmGatewayOptions>>().Value;
            return options.Provider switch
            {
                "Http" => (ICrmGateway)sp.GetRequiredService<UnimplementedCrmHttpGateway>(),
                "Mock" => sp.GetRequiredService<MockCrmGateway>(),
                _ => throw new NotSupportedException(UnsupportedCrmProviderMessage(options.Provider))
            };
        });

        services.AddScoped<ICrmCustomerLookupGateway>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<CrmGatewayOptions>>().Value;
            return options.Provider switch
            {
                "Http" => (ICrmCustomerLookupGateway)sp.GetRequiredService<UnimplementedCrmHttpGateway>(),
                "Mock" => sp.GetRequiredService<MockCrmGateway>(),
                _ => throw new NotSupportedException(UnsupportedCrmProviderMessage(options.Provider))
            };
        });

        AddCrmBuyerLookupGateway(services);
        AddPactGateway(services, configuration);
        AddTasleehGateway(services, configuration);
        AddEmailSender(services, configuration);

        return services;
    }

    private static string UnsupportedCrmProviderMessage(string? provider) =>
        $"Crm:Provider '{provider}' is not supported. Use \"Http\" — the standard for Development, UAT and " +
        "Production (the real CRM Buyer Lookup runs over HTTP; unit, contact and phone-search operations fail " +
        "closed until Tiger CRM publishes their endpoints) — or \"Mock\" (MockCrmGateway fixture data for the " +
        "automated test host; CrmGatewaySafety refuses it outside Development/Testing).";

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

    /// <summary>
    /// PACT mobile-based customer/contract lookup — a provider switch like
    /// <c>Crm:Provider</c> above, but with a real implementation available:
    /// "Mock" (default — <see cref="MockPactGateway"/>, so dev/tests stay
    /// deterministic and offline) or "Http"
    /// (<see cref="PactCustomerHttpGateway"/>, PACT's real
    /// <c>v1/contracts/{mobile}</c>/<c>…/customer-type</c> endpoints).
    ///
    /// <para>
    /// The HTTP implementation follows <see cref="AddCrmBuyerLookupGateway"/>'s
    /// typed-HttpClient shape exactly: base address read lazily from
    /// <see cref="PactApiOptions.BaseUrl"/> (never at startup, so a missing/
    /// blank value never prevents the host from starting —
    /// <c>PactCustomerHttpGateway</c> turns that into an <c>Unavailable</c>
    /// outcome on first use instead), and the API key applied per request by
    /// the gateway itself, never baked into the client.
    /// </para>
    /// </summary>
    private static void AddPactGateway(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PactGatewayOptions>(configuration.GetSection(PactGatewayOptions.SectionName));
        services.Configure<PactApiOptions>(configuration.GetSection(PactApiOptions.SectionName));

        services.AddHttpClient<PactCustomerHttpGateway>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<PactApiOptions>>().Value;
            // ResolveBaseAddress guarantees the trailing '/' — see its
            // remarks: without it a BaseUrl carrying a path prefix loses
            // that prefix to relative-URI resolution and every lookup
            // becomes a silent 404 -> "customer not found".
            if (options.ResolveBaseAddress() is { } baseAddress)
            {
                client.BaseAddress = baseAddress;
            }

            client.Timeout = TimeSpan.FromSeconds(15);
        });

        services.AddScoped<IPactCustomerLookupGateway>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<PactGatewayOptions>>().Value;
            return options.Provider switch
            {
                "Mock" => new MockPactGateway(),
                "Http" => sp.GetRequiredService<PactCustomerHttpGateway>(),
                _ => throw new NotSupportedException(
                    $"Pact:Provider '{options.Provider}' is not supported. Use 'Http' (the real PACT integration, " +
                    "PactCustomerHttpGateway — requires the PactApi section) or 'Mock' (MockPactGateway, " +
                    "fixture-backed; see its remarks — it must never be described as production-ready).")
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
    /// <summary>
    /// The customer email pipeline's outer edge: the <c>EmailNotifications</c>
    /// section, the provider adapter behind <see cref="IEmailSender"/>, and
    /// the <see cref="ICustomerEmailSender"/> façade the Outbox handlers call.
    ///   "Smtp"      — <see cref="SmtpEmailSender"/>, the Microsoft 365 SMTP
    ///                 account; the standard for UAT and Production.
    ///   "Recording" — <see cref="RecordingEmailSender"/>, in-memory; the
    ///                 automated test host and local development only
    ///                 (<see cref="EmailSenderSafety"/> refuses it elsewhere
    ///                 when delivery is enabled).
    /// </summary>
    private static void AddEmailSender(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<EmailNotificationOptions>(configuration.GetSection(EmailNotificationOptions.SectionName));

        services.AddSingleton(sp =>
            sp.GetRequiredService<IOptions<EmailNotificationOptions>>().Value.ToPolicy());

        services.AddSingleton<ISmtpTransport, SmtpClientTransport>();
        services.AddSingleton<SmtpEmailSender>();
        services.AddSingleton<RecordingEmailSender>();
        services.AddSingleton<IEmailSender>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<EmailNotificationOptions>>().Value;
            if (options.IsSmtp)
            {
                return sp.GetRequiredService<SmtpEmailSender>();
            }

            if (options.IsRecording)
            {
                return sp.GetRequiredService<RecordingEmailSender>();
            }

            throw new NotSupportedException(
                $"EmailNotifications:Provider '{options.Provider}' is not supported. Use \"Smtp\" (the Microsoft 365 "
                + "SMTP account — the standard for UAT and Production) or \"Recording\" (the in-memory adapter for the "
                + "automated test host and local development; never delivers anything).");
        });

        services.AddSingleton<ICustomerEmailSender, CustomerEmailSender>();
    }
}
