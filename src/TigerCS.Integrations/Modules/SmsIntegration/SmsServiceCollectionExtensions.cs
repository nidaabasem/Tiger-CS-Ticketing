using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.CrmDocuments;
using TigerCS.Application.Modules.Notifications;
using TigerCS.Application.Modules.Notifications.Services;
using TigerCS.Integrations.Modules.EmailIntegration;
using TigerCS.Application.Modules.Notifications.Abstractions;

namespace TigerCS.Integrations.Modules.SmsIntegration;

public static class SmsServiceCollectionExtensions
{
    /// <summary>
    /// <c>Sms:Provider</c>: <c>Disabled</c> (default, nothing is sent), <c>Broadnet</c> (real SMS — stays inert until every
    /// provider setting is confirmed and supplied), <c>Fake</c> (Development/Testing only, nothing is sent).
    /// </summary>
    public static IServiceCollection AddTigerCsSms(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SmsOptions>(configuration.GetSection(SmsOptions.SectionName));
        services.AddCustomerNotificationMode(configuration);

        services.AddSingleton<FakeSmsSender>();
        services.AddSingleton<DisabledSmsSender>();

        // RemoveAllLoggers: the default HttpClient logging prints the full request URL — password included.
        // Redirects are never followed; the response must come from the configured endpoint.
        services.AddHttpClient<BroadnetSmsSender>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            .RemoveAllLoggers();

        // The provider is always wrapped in the customer-delivery guard (CustomerNotifications:IsTestMode), the final SMS boundary.
        services.AddScoped<ISmsSender>(sp => new CustomerDeliveryGuardSmsSender(
            CreateProvider(sp),
            sp.GetRequiredService<CustomerNotificationModePolicy>(),
            sp.GetRequiredService<ILogger<CustomerDeliveryGuardSmsSender>>()));

        return services;
    }

    private static ISmsSender CreateProvider(IServiceProvider sp)
    {
        var provider = sp.GetRequiredService<IOptions<SmsOptions>>().Value.Provider;
        return provider switch
        {
            var p when string.Equals(p, "Broadnet", StringComparison.OrdinalIgnoreCase) => sp.GetRequiredService<BroadnetSmsSender>(),
            var p when string.Equals(p, "Fake", StringComparison.OrdinalIgnoreCase) => sp.GetRequiredService<FakeSmsSender>(),
            var p when string.IsNullOrWhiteSpace(p) || string.Equals(p, "Disabled", StringComparison.OrdinalIgnoreCase) => sp.GetRequiredService<DisabledSmsSender>(),
            var p => throw new NotSupportedException($"Sms:Provider '{p}' is not supported. Use \"Disabled\", \"Broadnet\" or (Development/Testing) \"Fake\".")
        };
    }
}
