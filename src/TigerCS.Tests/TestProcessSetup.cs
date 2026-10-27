using System.Runtime.CompilerServices;

namespace TigerCS.Tests;

/// <summary>
/// Runs once, before any test host is built. Every <c>WebApplicationFactory</c> host registers file watchers (config reload,
/// Razor views) and the suite creates thousands of hosts in one process; with inotify-backed watchers that exhausts Linux's
/// default <c>fs.inotify.max_user_instances</c> (128) and every later host fails with an <see cref="IOException"/> from
/// <c>FileSystemWatcher.StartRaisingEvents</c> (seen on GitHub Actions runners and in containers).
/// Polling watchers use no inotify instances. Test-process only: nothing here reaches the Api, Web or their published output.
/// </summary>
internal static class TestProcessSetup
{
    [ModuleInitializer]
    internal static void UsePollingFileWatchers()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_USE_POLLING_FILE_WATCHER") is null)
        {
            Environment.SetEnvironmentVariable("DOTNET_USE_POLLING_FILE_WATCHER", "true");
        }
    }
}
