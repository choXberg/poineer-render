namespace POIneer.Render.Application.Ports;

public interface IDatasetVersionCalculator
{
    // Computes a stable dataset version for a region from the file its dataset was rendered
    // from (the source/cut OSM PBF). Identical input content always produces the identical
    // version string, so re-publishing unchanged data is a no-op under IDatasetPublisher's
    // configured overwrite policy - a new version is only ever produced when the source
    // data actually changed, or a deployment deliberately bumps
    // PublisherOptions.SchemaVersion.
    Task<string> CalculateAsync(string sourcePath, CancellationToken cancellationToken = default);
}
