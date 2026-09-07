using VenueOS.Services;

namespace VenueOS.Services.Tests;

/// <summary>Covers the 0.2.1 hotfix: a live installed Dalamud plugin's <c>Assembly.Location</c> is not reliable
/// (Dalamud does not load the plugin assembly the way a normal <c>Assembly.LoadFrom(path)</c> would), so
/// <see cref="UserManualLoader.Load"/> takes an explicit, ordered list of candidate directories instead of guessing
/// one itself — the composition root (Plugin.cs) is responsible for supplying
/// <c>IDalamudPluginInterface.AssemblyLocation</c>'s directory first, with the reflection-based path only as a
/// last-resort fallback. This file only exercises the pure candidate-resolution logic; which real directories
/// Plugin.cs actually passes in is a fact verified by code review, not something a unit test can observe (no
/// VenueOS.Plugin test project exists — ImGui/Dalamud need a live context).</summary>
public sealed class UserManualLoaderTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "venueos-manual-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Preferred_candidate_containing_the_manual_loads_from_it()
    {
        var preferred = TempDir();
        File.WriteAllText(Path.Combine(preferred, UserManualLoader.FileName), "# Hello\n\nFrom preferred.");
        var result = UserManualLoader.Load([preferred]);
        Assert.Equal("# Hello\n\nFrom preferred.", result.Markdown);
    }

    [Fact]
    public void Falls_back_to_the_second_candidate_when_the_first_does_not_have_the_file()
    {
        var preferred = TempDir(); // exists, but no USER_MANUAL.md inside it
        var fallback = TempDir();
        File.WriteAllText(Path.Combine(fallback, UserManualLoader.FileName), "# Hello\n\nFrom fallback.");
        var result = UserManualLoader.Load([preferred, fallback]);
        Assert.Equal("# Hello\n\nFrom fallback.", result.Markdown);
    }

    [Fact]
    public void Stops_at_the_first_candidate_that_succeeds_and_never_tries_the_rest()
    {
        var preferred = TempDir();
        File.WriteAllText(Path.Combine(preferred, UserManualLoader.FileName), "# From preferred");
        var fallbackThatWouldAlsoWork = TempDir();
        File.WriteAllText(Path.Combine(fallbackThatWouldAlsoWork, UserManualLoader.FileName), "# From fallback");
        var result = UserManualLoader.Load([preferred, fallbackThatWouldAlsoWork]);
        Assert.Equal("# From preferred", result.Markdown);
        Assert.Single(result.Candidates); // the fallback was never even checked
    }

    [Fact]
    public void No_candidate_containing_the_manual_fails_gracefully_with_null()
    {
        var a = TempDir();
        var b = TempDir();
        var result = UserManualLoader.Load([a, b]);
        Assert.Null(result.Markdown);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Fact]
    public void A_nonexistent_candidate_directory_fails_gracefully_with_null()
    {
        var result = UserManualLoader.Load([Path.Combine(Path.GetTempPath(), "venueos-manual-does-not-exist-" + Guid.NewGuid())]);
        Assert.Null(result.Markdown);
        Assert.False(result.Candidates[0].DirectoryExists);
    }

    [Fact]
    public void A_null_or_empty_candidate_path_is_recorded_and_skipped_without_throwing()
    {
        var real = TempDir();
        File.WriteAllText(Path.Combine(real, UserManualLoader.FileName), "# OK");
        var result = UserManualLoader.Load([null, "", real]);
        Assert.Equal("# OK", result.Markdown);
        Assert.Equal(3, result.Candidates.Count);
    }

    [Fact]
    public void Empty_manual_file_fails_gracefully_with_null()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, UserManualLoader.FileName), "   \n  \n");
        var result = UserManualLoader.Load([dir]);
        Assert.Null(result.Markdown);
        Assert.Equal("The file exists but is empty.", result.Candidates[0].FailureReason);
    }

    [Fact]
    public void Candidate_resolution_is_deterministic_and_never_touches_directories_outside_the_supplied_list()
    {
        // Two independent runs against the same input must check exactly the same directories in exactly the same
        // order - nothing here may expand into a filesystem search or vary between calls.
        var a = TempDir();
        var b = TempDir();
        var first = UserManualLoader.Load([a, b]);
        var second = UserManualLoader.Load([a, b]);
        Assert.Equal(first.Candidates.Select(c => c.Directory), second.Candidates.Select(c => c.Directory));
        Assert.Equal([a, b], first.Candidates.Select(c => c.Directory));
    }

    [Fact]
    public void Failure_result_describes_every_candidate_checked_and_why_each_failed()
    {
        var missing = Path.Combine(Path.GetTempPath(), "venueos-manual-does-not-exist-" + Guid.NewGuid());
        var emptyDir = TempDir();
        var result = UserManualLoader.Load([missing, emptyDir]);
        Assert.Null(result.Markdown);
        Assert.Equal(2, result.Candidates.Count);
        Assert.False(result.Candidates[0].DirectoryExists);
        Assert.NotNull(result.Candidates[0].FailureReason);
        Assert.True(result.Candidates[1].DirectoryExists);
        Assert.False(result.Candidates[1].FileExists);
        Assert.NotNull(result.Candidates[1].FailureReason);
    }
}
