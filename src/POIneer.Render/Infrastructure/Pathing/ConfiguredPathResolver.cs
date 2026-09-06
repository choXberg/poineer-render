namespace POIneer.Render.Infrastructure.Pathing;

public static class ConfiguredPathResolver
{
    public static string Resolve(string contentRoot, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(contentRoot, path));
    }

    public static string? ResolveOptional(string contentRoot, string? path)
        => string.IsNullOrWhiteSpace(path)
            ? null
            : Resolve(contentRoot, path);
}
