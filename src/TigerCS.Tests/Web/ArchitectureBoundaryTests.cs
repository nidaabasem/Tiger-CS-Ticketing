using System.Reflection;
using TigerCS.Web;

namespace TigerCS.Tests.Web;

/// <summary>
/// The web tier must not reach the database.
///
/// <para>
/// The brief's constraint - "do not access SQL Server or EF Core directly from
/// TigerCS.Web" - was a convention while the project still referenced
/// TigerCS.Infrastructure, which is the project that carries EF Core, the SQL
/// Server provider and <c>TigerCsDbContext</c>. That reference was removed, and
/// these tests are what keep it removed: they assert on the assembly's actual
/// reference closure, so re-adding it - directly, or transitively through some
/// future reference - fails here rather than in review.
/// </para>
/// </summary>
public sealed class ArchitectureBoundaryTests
{
    private static readonly Assembly WebAssembly = typeof(WebHostMarker).Assembly;

    /// <summary>Every assembly TigerCS.Web can reach, directly or transitively.</summary>
    private static IReadOnlyCollection<string> ReferenceClosure()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<Assembly>();
        pending.Enqueue(WebAssembly);

        while (pending.Count > 0)
        {
            foreach (var reference in pending.Dequeue().GetReferencedAssemblies())
            {
                if (reference.Name is null || !seen.Add(reference.Name))
                {
                    continue;
                }

                // Only this solution's own assemblies are walked further. The
                // framework's closure is large, irrelevant here, and not always
                // loadable in a test host.
                if (reference.Name.StartsWith("TigerCS.", StringComparison.Ordinal))
                {
                    try
                    {
                        pending.Enqueue(Assembly.Load(reference));
                    }
                    catch (FileNotFoundException)
                    {
                        // Not resolvable here; its name is still recorded above.
                    }
                }
            }
        }

        return seen;
    }

    [Fact]
    public void TigerCsWeb_DoesNotReferenceTigerCsInfrastructure()
    {
        var direct = WebAssembly.GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToArray();

        Assert.DoesNotContain("TigerCS.Infrastructure", direct);
    }

    [Fact]
    public void TigerCsWeb_CannotReachEntityFrameworkCoreOrTheSqlServerProviderAtAll()
    {
        // The transitive check is the one that matters: a future reference to
        // some other project that itself references Infrastructure would
        // reopen the path without touching this project's own reference list.
        var closure = ReferenceClosure();

        foreach (var forbidden in new[]
        {
            "Microsoft.EntityFrameworkCore",
            "Microsoft.EntityFrameworkCore.Relational",
            "Microsoft.EntityFrameworkCore.SqlServer",
            "Microsoft.Data.SqlClient",
            "TigerCS.Infrastructure"
        })
        {
            Assert.False(
                closure.Contains(forbidden),
                $"TigerCS.Web can reach '{forbidden}'. The web tier must talk to the API, never to the database.");
        }
    }

    [Fact]
    public void TigerCsWeb_DefinesNoDbContextAndNoDatabaseType()
    {
        var suspicious = WebAssembly.GetTypes()
            .Where(t => t.Name.Contains("DbContext", StringComparison.OrdinalIgnoreCase)
                        || t.Name.Contains("Repository", StringComparison.OrdinalIgnoreCase)
                        || t.Name.Contains("Migration", StringComparison.OrdinalIgnoreCase))
            .Select(t => t.FullName)
            .ToArray();

        Assert.Empty(suspicious);
    }

    [Fact]
    public void TigerCsWeb_ReferencesTigerCsApplicationForItsDtosOnly()
    {
        // Application is referenced deliberately, so the typed clients
        // serialize the API's own contract types. That is safe precisely
        // because Application itself reaches no database - which the closure
        // check above proves, since Application is inside it.
        var direct = WebAssembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();

        Assert.Contains("TigerCS.Application", direct);
    }

    [Fact]
    public void NoTypeInTigerCsWeb_NamesAConnectionString()
    {
        // A connection string in the web tier would mean the boundary had been
        // crossed by configuration rather than by a reference.
        var configuration = WebAssembly.GetTypes()
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Where(p => p.Name.Contains("ConnectionString", StringComparison.OrdinalIgnoreCase))
            .Select(p => $"{p.DeclaringType?.Name}.{p.Name}")
            .ToArray();

        Assert.Empty(configuration);
    }
}
