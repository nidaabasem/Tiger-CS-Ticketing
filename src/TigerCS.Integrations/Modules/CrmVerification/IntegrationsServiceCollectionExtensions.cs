using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.CrmVerification.Abstractions;

namespace TigerCS.Integrations.Modules.CrmVerification;

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

        return services;
    }
}
