namespace POIneer.Render.Application.Contracts;

// Outcome of a publish attempt. WasSkipped is true when publishing left an existing
// destination file in place because the configured overwrite policy allowed skipping.
public sealed record DatasetPublishResult(
    string DestinationPath,
    bool WasSkipped);
