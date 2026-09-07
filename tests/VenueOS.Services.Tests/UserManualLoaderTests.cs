using VenueOS.Services;

namespace VenueOS.Services.Tests;

public sealed class UserManualLoaderTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "venueos-manual-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Loads_a_valid_bundled_manual_file()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, UserManualLoader.FileName), "# Hello\n\nSome content.");
        Assert.Equal("# Hello\n\nSome content.", UserManualLoader.Load(dir));
    }

    [Fact]
    public void Missing_manual_file_fails_gracefully_by_returning_null()
    {
        var dir = TempDir(); // directory exists, but no USER_MANUAL.md was written into it
        Assert.Null(UserManualLoader.Load(dir));
    }

    [Fact]
    public void Nonexistent_directory_fails_gracefully_by_returning_null()
    {
        Assert.Null(UserManualLoader.Load(Path.Combine(Path.GetTempPath(), "venueos-manual-does-not-exist-" + Guid.NewGuid())));
    }

    [Fact]
    public void Empty_manual_file_fails_gracefully_by_returning_null()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, UserManualLoader.FileName), "   \n  \n");
        Assert.Null(UserManualLoader.Load(dir));
    }
}
