namespace POIneer.Render.Infrastructure.Pathing;

public static class ContentRootResolver
{
    private const string AppSettingsFileName = "appsettings.json";
    private static readonly string ProjectDirectoryRelativeToBuildOutput = Path.Combine("..", "..", "..");

    public static string Resolve()
        => Resolve(Directory.GetCurrentDirectory(), AppContext.BaseDirectory);

    public static string Resolve(string currentDirectory, string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        var projectDirectory = Path.GetFullPath(Path.Combine(
            baseDirectory,
            ProjectDirectoryRelativeToBuildOutput));

        var candidates = new[] {
            currentDirectory,
            projectDirectory,
            baseDirectory };

        foreach (var candidate in candidates)
        {
            if (File.Exists(Path.Combine(candidate, AppSettingsFileName)))
                return candidate;
        }

        // appsettings.json is loaded as optional (Program.cs) and the host is fully
        // configurable via environment variables and command-line arguments, so a
        // missing/renamed appsettings.json is a supported deployment shape, not an
        // error. Fall back to the current directory - the same default the generic
        // host itself would use - rather than failing startup.
        return currentDirectory;
    }
}
