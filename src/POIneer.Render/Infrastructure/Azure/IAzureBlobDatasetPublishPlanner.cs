using POIneer.Render.Application.Contracts;

namespace POIneer.Render.Infrastructure.Azure;

public interface IAzureBlobDatasetPublishPlanner
{
    Task<AzureBlobDatasetPublishDecision> PlanAsync(
        DatasetPublishRequest request,
        CancellationToken cancellationToken = default);
}
