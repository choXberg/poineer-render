# ADR 0008: Configurable Render Execution Targets (Azure Container Apps Jobs)

## Status

Proposed

## Context

POIneer.Render currently always runs on the VPS (`poineer-render.timer` /
`poineer-render.service`, Docker + systemd - see `docs/workflows/vps-render-service-operations.md`).
`docs/architecture/hybrid-dataset-architecture.md` already anticipated an "Azure renderer
(planned option)" as an additional compute environment, decoupled from storage - this ADR is
that option becoming concrete, for a deliberately small, cost-conscious slice of the region
set (learning/practice goal: hands-on Azure compute experience and safe handling of
concurrent access to a shared publish destination - not a scaling requirement).

The concrete goal: render a small, explicitly chosen subset of regions (1-2) on Azure in
addition to the VPS, with **at least one region rendered by both** the VPS and the Azure job,
publishing to the same Azure Blob Storage destination (`Publisher:Target=AzureBlob`). This
surfaces a real gap in the current design: `ISingleInstanceLock`/`FileSingleInstanceLock`
(ADR 0001) is a local OS advisory file lock, scoped to a single filesystem/host and to the
*entire renderer run* (all configured regions in one process). It gives no protection at all
between two independent renderer processes on two different hosts (VPS and Azure) that happen
to render the same region around the same time - which is exactly the scenario this change
introduces.

## Options Considered

### Compute target for the Azure-side renderer

| Option | Assessment |
| --- | --- |
| Azure VM (systemd + Docker, same as VPS) | Zero new concepts - but also zero new Azure PaaS experience, which is the point of doing this. |
| Azure Container Instances (ACI) | Simple, pay-per-second, reuses the existing Docker image unchanged - but has no built-in scheduling; a "run daily" job needs a bolted-on trigger (Logic App/Automation), and Azure's own guidance increasingly steers scheduled container workloads toward Container Apps Jobs instead. |
| **Azure Container Apps Jobs (chosen)** | Native scheduled/triggered container job support (built-in cron trigger), reuses the existing Docker image unchanged, and is the currently-idiomatic Azure pattern for "run a container on a schedule." Brings genuinely new, transferable concepts (Container Apps Environment, Jobs, Log Analytics-based logging) without requiring any renderer code changes. |

### Concurrency protection for the shared Azure Blob destination

| Option | Assessment |
| --- | --- |
| Rely only on existing `Publisher:OverwritePolicy=SkipIfIdentical` metadata comparison | No new code, but doesn't stop two renderers from racing to render the *same* region at the same time - wastes compute on both sides and can surface as a confusing `Fail` outcome for the loser mid-run instead of a clean, early skip. |
| Optimistic concurrency via Blob ETags (conditional upload) | Cheap, no extra Azure resource - but only protects the *upload* instant. The expensive part of a race here is the render itself (PBF download, OSM parsing, Flyway migration), which would still happen twice before either side finds out it lost. Poor fit as the *sole* mechanism. |
| **Azure Blob lease as a distributed, per-region lock (chosen)** | Idiomatic Azure-native mutex; symmetric with the existing `ISingleInstanceLock` Port/Adapter pattern from ADR 0001 (same abstraction shape, new adapter); works regardless of which side initiates the render; self-expiring (a lease has a bounded duration and must be renewed), so a crashed holder doesn't leave a permanently stuck lock the way a naive "marker file/blob" approach could. |

## Decision

1. **Azure compute: Azure Container Apps Jobs**, running the existing `poineer-render` Docker
   image unchanged, triggered on a cron schedule (mirroring `poineer-render.timer`'s
   `OnCalendar`). No renderer code changes are required for this part - only new Azure
   infrastructure (a Container Apps Environment + Job) and a Jenkins/CI step to push the same
   image Azure already builds and verifies today.

2. **Region selection stays config-only, no new domain concept.** The Azure job points
   `Renderer:RegionsJson` at a new, smaller config file (e.g. `Cli/config/regions.azure.json`)
   containing only the 1-2 chosen regions - deliberately including one region already present
   in `regions.production.json` (the VPS's full list), so the dual-render/concurrency scenario
   is actually exercised. No change to `RegionDto`, `IRegionSource`, or the domain model - this
   reuses the exact mechanism that already lets VPS and local dev use different region files.

3. **New distributed, per-region publish lock**, introduced as a *second, independent* lock
   layer alongside (not replacing) the existing whole-run `ISingleInstanceLock`:
   - New port `IDatasetPublishLock` (kept separate from `ISingleInstanceLock` because the
     scope differs: whole-run/host-local vs. per-region/cross-host) in
     `Application/Ports`, following the same acquire/skip-if-held/release shape as
     `ISingleInstanceLock`.
   - New adapter `BlobLeaseDatasetPublishLock` in `Infrastructure/Azure`, acquiring an
     exclusive lease on a small per-region marker blob (e.g. `{regionId}/.render-lock`)
     before that region's render+publish step starts, releasing it on completion. Lease
     duration is bounded and renewed for the duration of the render, so a crashed process
     (VPS or Azure) doesn't leave a stuck lock - it simply expires.
   - Scope: acquired **inside** the per-region loop, around render+publish for that one
     region - not around the whole run. This is intentional: the VPS renders every
     configured region in one process per run, while the Azure job renders only 1-2: a
     whole-run-level distributed lock would block the Azure job for the VPS's entire
     (unrelated) run, which is wrong. Per-region scope only blocks when two sides actually
     touch the *same* region.
   - Activated whenever `Publisher:Target=AzureBlob` (regardless of which compute target is
     running), so it also protects two accidental concurrent VPS-only or manual runs against
     the same Azure destination, not just the new VPS-vs-Azure case.
   - Existing `Publisher:OverwritePolicy` metadata checks (ADR 0002-0004) are unchanged and
     remain as a secondary safety net (e.g. if a lease already expired on an unexpectedly long
     run).
   - When `Publisher:Target=Local`, this new lock is not engaged - the existing per-run file
     lock already fully covers the single-host case.

## Consequences

- A developer gets hands-on experience with a real Azure PaaS scheduling primitive (Container
  Apps Jobs) and a real distributed-locking pattern (Blob lease), without a rewrite: the only
  new production code is the `IDatasetPublishLock`/`BlobLeaseDatasetPublishLock` pair; region
  selection and compute are configuration/infrastructure only.
- Cost stays bounded by construction: only 1-2 regions ever render on Azure compute, chosen
  explicitly via `regions.azure.json`, independent of how many regions the VPS renders.
- The renderer now has two independent concurrency mechanisms with different scopes
  (whole-run/local vs. per-region/distributed) - this needs to be documented clearly (in
  `docs/workflows/scheduled-renders.md` and the VPS ops runbook) so a future reader doesn't
  assume the existing `ISingleInstanceLock` alone covers the cross-host case.
- CI/CD needs a second deployment step (push the already-built/verified image to the Azure
  Container Apps Job), in addition to the existing VPS `rsync`/Docker-tag-promotion steps -
  this is additive to the existing Jenkins pipeline, not a redesign of it.
- `Publisher:Target` must be `AzureBlob` for any region that is (or might become) dual-rendered;
  a region published only `Local` on the VPS never engages the new lock and is unaffected.

## Out Of Scope

- Migrating VPS-only regions to Azure compute, or moving all rendering to Azure.
- Autoscaling, high availability, or multi-region Azure compute redundancy.
- Changing `Publisher:OverwritePolicy` semantics or the existing Azure Blob verifier (ADR 0004).
- Cost-control tooling (Azure Budgets/alerts, CDN in front of Blob Storage) - a separate,
  storage/distribution-side concern, not part of adding a second compute target.

## References

- [Hybrid Dataset Architecture](../architecture/hybrid-dataset-architecture.md)
- [Azure Dataset Storage](../workflows/azure-dataset-storage.md)
- [Scheduled Renderer Execution](../workflows/scheduled-renders.md)
- [ADR 0001: Prevent Overlapping Scheduled Renders](0001-prevent-overlapping-scheduled-renders.md)
- [ADR 0002: Local Dataset Publisher](0002-local-dataset-publisher.md)
- [ADR 0004: Verify Published Dataset Integrity](0004-verify-published-dataset-integrity.md)
