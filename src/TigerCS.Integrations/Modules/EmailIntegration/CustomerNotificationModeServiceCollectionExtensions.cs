using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.Notifications;

namespace TigerCS.Integrations.Modules.EmailIntegration;

public static class CustomerNotificationModeServiceCollectionExtensions
{
    /// <summary>
    /// Binds <c>CustomerNotifications</c> and registers the <see cref="CustomerNotificationModePolicy"/> that the email and SMS
    /// delivery guards read. Idempotent — the email and SMS registrations both call it, so neither can be wired without it.
    /// </summary>
    public static IServiceCollection AddCustomerNotificationMode(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<CustomerNotificationOptions>(configuration.GetSection(CustomerNotificationOptions.SectionName));
        services.TryAddSingleton(sp => sp.GetRequiredService<IOptions<CustomerNotificationOptions>>().Value.ToPolicy());
        return services;
    }
}
