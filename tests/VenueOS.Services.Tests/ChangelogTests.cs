using System.Text.Json;
using System.Text.RegularExpressions;
using VenueOS.Services;

namespace VenueOS.Services.Tests;

/// <summary>0.3.10: the canonical repository-root <c>CHANGELOG.md</c>, its Settings → Changelog reader (the shared
/// <see cref="BundledDocumentLoader"/>/<see cref="ManualMarkdown"/> path the User Manual already uses), its packaging
/// requirement, and the 0.3.10 version metadata. The ImGui screen itself has no test project (no live Dalamud
/// context); everything it renders comes from the loader + parser exercised here.</summary>
public sealed class ChangelogTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "venueos-changelog-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string RepoRoot() => Path.GetDirectoryName(Path.GetDirectoryName(ManualTestPaths.FindRepoManualPath()))!;

    private static string RepoFile(params string[] parts) => Path.Combine([RepoRoot(), .. parts]);

    private static string CsprojVersion(string element)
    {
        var match = Regex.Match(File.ReadAllText(RepoFile("src", "VenueOS.Plugin", "VenueOS.Plugin.csproj")), $"<{element}>([^<]+)</{element}>");
        Assert.True(match.Success, $"<{element}> not found in VenueOS.Plugin.csproj");
        return match.Groups[1].Value;
    }

    private static IReadOnlyList<string> VersionHeadings(string? markdown) =>
        ManualMarkdown.Parse(markdown)
            .Where(b => b.Kind == MarkdownBlockKind.Heading && b.HeadingLevel == 2)
            .Select(b => b.PlainText.Split(' ')[0])
            .ToArray();

    [Fact]
    public void Changelog_loader_reads_the_bundled_changelog_file()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, ChangelogLoader.FileName), "# VenueOS Changelog\n\n## 1.2.3 — 2030-01-01\n\n- Something changed.");
        var result = ChangelogLoader.Load([dir]);
        Assert.Equal("# VenueOS Changelog\n\n## 1.2.3 — 2030-01-01\n\n- Something changed.", result.Markdown);
        Assert.Equal("CHANGELOG.md", ChangelogLoader.FileName);
    }

    [Fact]
    public void Changelog_loader_never_reads_the_manual_and_vice_versa()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, UserManualLoader.FileName), "# Manual");
        Assert.Null(ChangelogLoader.Load([dir]).Markdown);
        File.WriteAllText(Path.Combine(dir, ChangelogLoader.FileName), "# Changelog");
        Assert.Equal("# Manual", UserManualLoader.Load([dir]).Markdown);
        Assert.Equal("# Changelog", ChangelogLoader.Load([dir]).Markdown);
    }

    /// <summary>Settings → Changelog must degrade to its "could not be loaded" state, never throw: a missing file,
    /// a missing directory, a null candidate and an empty file all return null Markdown with an explanation, and
    /// the parser turns null into an empty document rather than failing.</summary>
    [Fact]
    public void Missing_or_empty_changelog_fails_gracefully()
    {
        var noFile = TempDir();
        var empty = TempDir();
        File.WriteAllText(Path.Combine(empty, ChangelogLoader.FileName), "   ");
        var missingDir = Path.Combine(Path.GetTempPath(), "venueos-changelog-missing-" + Guid.NewGuid());

        var result = ChangelogLoader.Load([null, missingDir, noFile, empty]);

        Assert.Null(result.Markdown);
        Assert.Equal(4, result.Candidates.Count);
        Assert.All(result.Candidates, c => Assert.False(string.IsNullOrEmpty(c.FailureReason)));
        Assert.Contains("CHANGELOG.md was not found", result.Candidates[2].FailureReason);
        Assert.Empty(ManualMarkdown.Parse(result.Markdown));
    }

    /// <summary>The viewer shows whatever the file says — a version that exists only in this test's file appears,
    /// and nothing from the real release history is injected from code.</summary>
    [Fact]
    public void Viewer_content_comes_from_the_file_not_a_hard_coded_release_list()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, ChangelogLoader.FileName), "# Changelog\n\n## 9.8.7 — 2031-02-03\n\n- Only in this file.\n\n## 9.8.6 — 2031-01-01\n\n- Also only here.");
        Assert.Equal(["9.8.7", "9.8.6"], VersionHeadings(ChangelogLoader.Load([dir]).Markdown));

        var offenders = Directory.EnumerateFiles(RepoFile("src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"## 0\.\d+\.\d+ —"))
            .ToArray();
        Assert.Empty(offenders);
    }

    [Fact]
    public void Repository_changelog_is_newest_first_and_starts_with_the_current_version()
    {
        var markdown = File.ReadAllText(RepoFile("CHANGELOG.md"));
        var versions = VersionHeadings(markdown);

        Assert.Equal(CsprojVersion("Version"), versions[0]);
        Assert.Equal(versions.OrderByDescending(v => Version.Parse(v)).ToArray(), versions);
        Assert.Contains("0.3.9", versions);
        Assert.Contains("0.3.8", versions);
        Assert.Equal("0.1.0", versions[^1]);
    }

    [Fact]
    public void Plugin_project_copies_both_bundled_documents_into_the_build_output()
    {
        var csproj = File.ReadAllText(RepoFile("src", "VenueOS.Plugin", "VenueOS.Plugin.csproj"));
        Assert.Contains(@"<None Include=""..\..\docs\USER_MANUAL.md"" Link=""USER_MANUAL.md"">", csproj);
        Assert.Contains(@"<None Include=""..\..\CHANGELOG.md"" Link=""CHANGELOG.md"">", csproj);
    }

    /// <summary>Package-Release.ps1 throws if a required file is missing from the build output, and again if the
    /// finished ZIP lacks a required document or the changelog has no entry for the packaged version.</summary>
    [Fact]
    public void Package_validation_requires_both_the_manual_and_the_changelog()
    {
        var script = File.ReadAllText(RepoFile("scripts", "Package-Release.ps1"));

        var requiredFiles = Regex.Match(script, @"\$requiredFiles = @\((.*?)\n\)", RegexOptions.Singleline);
        Assert.True(requiredFiles.Success);
        Assert.Contains("\"USER_MANUAL.md\"", requiredFiles.Groups[1].Value);
        Assert.Contains("\"CHANGELOG.md\"", requiredFiles.Groups[1].Value);

        Assert.Contains("$requiredDocuments = @(\"USER_MANUAL.md\", \"CHANGELOG.md\")", script);
        Assert.Contains("Release ZIP is missing required document", script);
        Assert.Contains("CHANGELOG.md has no '## $version' entry", script);
    }

    [Fact]
    public void Version_metadata_is_0_3_10()
    {
        Assert.Equal("0.3.10", CsprojVersion("Version"));
        Assert.Equal("0.3.10.0", CsprojVersion("AssemblyVersion"));
        Assert.Equal("0.3.10.0", CsprojVersion("FileVersion"));

        using var manifest = JsonDocument.Parse(File.ReadAllText(RepoFile("src", "VenueOS.Plugin", "VenueOS.json")));
        Assert.Equal("0.3.10.0", manifest.RootElement.GetProperty("AssemblyVersion").GetString());
    }
}
