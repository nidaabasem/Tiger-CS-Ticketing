using System.Runtime.CompilerServices;
using System.Text.Json;

namespace TigerCS.Tests.CustomerVerification.Services;

/// <summary>
/// The repository-wide guard on <c>Crm:SecretKey</c>: no committed
/// <c>appsettings*.json</c> may carry the key at all — not in <c>src/</c>, and
/// not in the <c>publish/</c> build output either.
///
/// <para>
/// <see cref="CrmProviderRegistrationTests.CommittedApiSettings_UseHttp_AndCarryNoCrmSecretKey"/>
/// already pins the two <c>src/TigerCS.Api</c> files by name. It did not cover
/// <c>publish/</c>, which is committed build output (docs/DEV-SETUP.md) — so a
/// real secret survived there after the <c>src/</c> copies were emptied. This
/// guard closes that gap by walking both trees instead of naming files, so a
/// new project or a newly published output is covered the day it is added.
/// </para>
///
/// <para>
/// The failure message names offending files only. It never echoes the value:
/// a leaked secret must not be copied into CI logs by the very test that
/// catches it.
/// </para>
/// </summary>
public sealed class CommittedCrmSecretGuardTests
{
    private const string SecretKeyName = "SecretKey";

    /// <summary>The scanned trees, relative to the repository root.</summary>
    private static readonly string[] ScannedRoots = ["src", "publish"];

    /// <summary>
    /// Build output copies the committed files verbatim, so <c>bin</c>/<c>obj</c>
    /// would report the same file twice — and after a fix, a stale copy would
    /// fail a guard about what is *committed*.
    /// </summary>
    private static readonly string[] IgnoredDirectories = ["bin", "obj"];

    private static string RepositoryRoot([CallerFilePath] string testFilePath = "")
        => Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFilePath)!, "..", "..", "..", ".."));

    private static IReadOnlyList<string> CommittedSettingsFiles()
    {
        var root = RepositoryRoot();

        return [.. ScannedRoots
            .Select(tree => Path.Combine(root, tree))
            .Where(Directory.Exists)
            .SelectMany(tree => Directory.EnumerateFiles(tree, "appsettings*.json", SearchOption.AllDirectories))
            .Where(path => !Path.GetRelativePath(root, path)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => IgnoredDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase)))
            .Select(Path.GetFullPath)
            .OrderBy(path => path, StringComparer.Ordinal)];
    }

    private static bool CarriesCrmSecretKey(string path)
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(path),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("Crm", out var crm)
            && crm.ValueKind == JsonValueKind.Object
            && crm.EnumerateObject().Any(p => string.Equals(p.Name, SecretKeyName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NoCommittedSettingsFile_CarriesCrmSecretKey_InEitherSrcOrPublish()
    {
        var root = RepositoryRoot();

        var offenders = CommittedSettingsFiles()
            .Where(CarriesCrmSecretKey)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Crm:SecretKey must not be committed in any appsettings file — it comes from user-secrets "
            + "(Development) or the Crm__SecretKey environment variable (UAT/Production), per docs/DEV-SETUP.md §3a. "
            + "Remove the key entirely (an empty value still fails) from: "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// A scan that silently finds nothing would pass forever. Pin that both
    /// trees are actually reached, so a moved test file or a renamed directory
    /// fails loudly instead of turning the guard above into a no-op.
    /// </summary>
    [Fact]
    public void TheScan_ActuallyReachesBothTrees()
    {
        var root = RepositoryRoot();
        var scanned = CommittedSettingsFiles()
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .ToList();

        Assert.True(
            File.Exists(Path.Combine(root, "docs", "DEV-SETUP.md")),
            $"Repository root did not resolve to the repository: {root}");

        foreach (var tree in ScannedRoots)
        {
            Assert.True(
                scanned.Any(path => path.StartsWith($"{tree}/", StringComparison.Ordinal)),
                $"Found no appsettings*.json under '{tree}/' — the guard would be vacuous. Scanned: "
                + (scanned.Count == 0 ? "(nothing)" : string.Join(", ", scanned)));
        }
    }
}
