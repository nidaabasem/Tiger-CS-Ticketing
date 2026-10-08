using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using TigerCS.Api.Controllers;

namespace TigerCS.Tests.GenesysIntegration.Contracts;

/// <summary>One public Genesys-facing route, as the controllers declare it.</summary>
/// <param name="Method">HTTP method (upper case).</param>
/// <param name="Route">Full route template without a leading slash and without constraints, e.g. <c>api/genesys/tickets/{ticketId}</c>.</param>
/// <param name="Action">The controller action.</param>
/// <param name="Statuses">Every status code the action declares through <c>[ProducesResponseType]</c>.</param>
/// <param name="Headers">Request headers the action binds (<c>[FromHeader]</c>).</param>
/// <param name="QueryParameters">Query-string parameters the action binds (<c>[FromQuery]</c>).</param>
public sealed record ApiRoute(
    string Method,
    string Route,
    MethodInfo Action,
    IReadOnlySet<int> Statuses,
    IReadOnlySet<string> Headers,
    IReadOnlySet<string> QueryParameters)
{
    public string Key => $"{Method} /{Route}";
}

/// <summary>
/// Reflection over the real TigerCS.Api controllers. The single source the
/// data-action contract tests and the TigerGroupWeb route-table test compare
/// against, so a renamed route, a new Genesys endpoint or a changed status
/// code fails the build until the documents are updated.
/// </summary>
public static class ApiRouteCatalog
{
    /// <summary>The controllers whose routes Genesys reaches through the public TigerGroupWeb proxy.</summary>
    public static readonly Type[] GenesysFacingControllers =
    [
        typeof(GenesysController),
        typeof(GenesysCollectionsController),
        typeof(GenesysDocumentsController),
        typeof(GenesysVerificationController)
    ];

    private static readonly Regex Constraint = new(@"\{(\*?\w+)(:[^}]*)?\}", RegexOptions.Compiled);

    public static IReadOnlyList<ApiRoute> GenesysRoutes { get; } = Discover(GenesysFacingControllers);

    public static IReadOnlyList<ApiRoute> Discover(IEnumerable<Type> controllers)
    {
        var routes = new List<ApiRoute>();
        foreach (var controller in controllers)
        {
            var prefix = controller.GetCustomAttributes<RouteAttribute>(inherit: false).Single().Template;
            foreach (var action in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (var verb in action.GetCustomAttributes<HttpMethodAttribute>(inherit: true))
                {
                    var template = string.IsNullOrEmpty(verb.Template) ? prefix : $"{prefix.TrimEnd('/')}/{verb.Template.TrimStart('/')}";
                    var stripped = StripConstraints(template);
                    var placeholders = Constraint.Matches(template).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    routes.Add(new ApiRoute(
                        verb.HttpMethods.Single().ToUpperInvariant(),
                        stripped,
                        action,
                        action.GetCustomAttributes<ProducesResponseTypeAttribute>(inherit: true).Select(a => a.StatusCode).ToHashSet(),
                        action.GetParameters()
                            .Select(p => p.GetCustomAttribute<FromHeaderAttribute>()?.Name)
                            .Where(n => n is not null).Select(n => n!).ToHashSet(StringComparer.OrdinalIgnoreCase),
                        action.GetParameters()
                            .Where(p => IsQueryParameter(p, placeholders))
                            .Select(p => p.GetCustomAttribute<FromQueryAttribute>()?.Name ?? p.Name!)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase)));
                }
            }
        }

        return routes.OrderBy(r => r.Route, StringComparer.Ordinal).ThenBy(r => r.Method, StringComparer.Ordinal).ToList();
    }

    /// <summary>A simple-typed parameter that is not in the route, body, header or services binds from the query string.</summary>
    private static bool IsQueryParameter(ParameterInfo p, ISet<string> routePlaceholders)
    {
        if (p.ParameterType == typeof(CancellationToken) || routePlaceholders.Contains(p.Name!))
        {
            return false;
        }

        if (p.GetCustomAttributes().Any(a => a is FromBodyAttribute or FromHeaderAttribute or FromServicesAttribute or FromRouteAttribute or FromFormAttribute))
        {
            return false;
        }

        var type = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;
        return type.IsPrimitive || type == typeof(string) || type == typeof(DateOnly) || type == typeof(Guid) || type == typeof(decimal);
    }

    /// <summary><c>tickets/{ticketId:long}</c> becomes <c>tickets/{ticketId}</c>.</summary>
    public static string StripConstraints(string template) => Constraint.Replace(template, "{$1}");

    /// <summary>Walks up from the test binaries to the repository root (the directory holding <c>docs/Genesys</c> and <c>src</c>).</summary>
    public static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "docs", "Genesys")) && Directory.Exists(Path.Combine(dir.FullName, "src")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("Repository root (docs/Genesys + src) not found above " + AppContext.BaseDirectory);
    }
}
