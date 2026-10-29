using Microsoft.Extensions.Options;

namespace Tiger.TigerGroupWeb.Genesys
{
    // TEMPORARY diagnostics for the Production 401 on POST /api/auth/login. Remove once the cause is found.
    // Logs: environment, BaseUrl, Username, the configuration provider that supplies Ticketing:Password, and ONE boolean
    // (does the effective password equal the one in appsettings.Production.json). Never logs the password, a hash,
    // a length, a fingerprint, a token or any other secret. Cannot fail startup: every failure is swallowed.
    public static class TicketingConfigDiagnostics
    {
        public static void Log(IServiceProvider services)
        {
            try
            {
                var env = services.GetRequiredService<IWebHostEnvironment>();
                var config = services.GetRequiredService<IConfiguration>();
                var options = services.GetRequiredService<IOptions<TicketingOptions>>().Value;
                var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("TicketingDiagnostics");

                logger.LogWarning(
                    "Ticketing diagnostics: Environment={Environment} BaseUrl={BaseUrl} Username={Username} " +
                    "PasswordProvider={PasswordProvider} PasswordMatchesAppsettingsProduction={Matches}",
                    env.EnvironmentName,
                    options.BaseUrl,
                    options.Username,
                    FindPasswordProvider(config),
                    MatchesProductionFile(env, options));
            }
            catch
            {
                // Diagnostics must never take the application down.
            }
        }

        // The last provider holding the key wins, which is how IConfiguration resolves it.
        // A provider's ToString() names its type and file (never a value).
        private static string FindPasswordProvider(IConfiguration config)
        {
            try
            {
                if (config is IConfigurationRoot root)
                {
                    foreach (var provider in root.Providers.Reverse())
                    {
                        if (provider.TryGet("Ticketing:Password", out _))
                        {
                            return provider.ToString() ?? provider.GetType().Name;
                        }
                    }
                }

                return "(not set by any provider)";
            }
            catch
            {
                return "(unknown)";
            }
        }

        // true / false, or "file-unreadable" when the file cannot be read; the values are compared in memory only.
        private static string MatchesProductionFile(IWebHostEnvironment env, TicketingOptions options)
        {
            try
            {
                var path = Path.Combine(env.ContentRootPath, "appsettings.Production.json");
                if (!File.Exists(path))
                {
                    return "file-not-found";
                }

                var fileValue = new ConfigurationBuilder()
                    .AddJsonFile(path, optional: false, reloadOnChange: false)
                    .Build()["Ticketing:Password"];

                return string.Equals(fileValue, options.Password, StringComparison.Ordinal) ? "true" : "false";
            }
            catch
            {
                return "file-unreadable";
            }
        }
    }
}
