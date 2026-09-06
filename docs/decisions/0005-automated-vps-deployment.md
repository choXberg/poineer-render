# ADR 0005: Automated VPS Deployment

## Status

Accepted

## Context

Issue #107 asks for automated deployment of POIneer.Render release artifacts to the VPS: the
renderer has so far only ever been started with `dotnet run` (locally, or ad hoc from a
Jenkins workspace), never from a stable, published artifact - which also means there was
nothing yet for a scheduler to point at (the `#108`/ADR 0001 crontab line and
`Publisher:DestinationDir` in `appsettings.Production.json` already assume a deployed artifact
at `/opt/poineer-render/app/POIneer.Render.dll`, but nothing produced it there).

The original `Deploy Placeholder` stage in the Jenkinsfile sketched this as a remote copy:

```text
# rsync -avz --delete "${PUBLISH_DIR}/" user@render-host:/opt/poineer/render/
# ssh user@render-host 'sudo systemctl restart poineer-render.service'
```

Checking the actual VPS changed that assumption: Jenkins runs directly on the render host
(`cho`), and `/opt/poineer-render/{app,data,logs}` already exists there, owned by the
`jenkins` user (from earlier manual setup/testing). Deploying is therefore a local filesystem
sync within the same Jenkins pipeline step, not a remote SSH/rsync-to-a-different-host - no
SSH credential needs to be created or stored in Jenkins for this. The commented-out
`systemctl restart` line was also never applicable: per ADR 0001, POIneer.Render is a
cron-triggered CLI process, not a long-running service, so there is no systemd unit to
restart.

## Decision

Replace the `Deploy Placeholder` stage with two real stages, both gated on `release/*`
branches (matching the scope the placeholder already used - no change to when deployment
happens, only to what it does):

- **`Deploy to VPS`**: `mkdir -p`s the target directories, then
  `rsync -a --delete "${PUBLISH_DIR}/" "${DEPLOY_APP_DIR}/"` (`DEPLOY_APP_DIR` =
  `/opt/poineer-render/app`). `--delete` is safe here because `DEPLOY_APP_DIR` only ever holds
  what `dotnet publish` produced for the current release - nothing under it is hand-maintained
  on the VPS, so removing anything not in the new publish output can't lose real data.
- **`Verify Deployment`**: runs the just-deployed DLL with `--Renderer:DryRun=true`
  (`/opt/dotnet/current/dotnet /opt/poineer-render/app/POIneer.Render.dll
  --Renderer:DryRun=true`). `Runner.RunAsync` logs its fully resolved configuration (regions
  file, work/output/lock paths) and returns `0` immediately when `DryRun` is set, before
  touching the network, the single-instance lock, or any region - the same flag the existing
  `Optional Dry-Run Render Check` stage already uses for a workspace-based check on `develop`.
  This proves the deployed artifact actually starts and resolves `appsettings.Production.json`
  + `regions.production.json` correctly, without doing any heavy rendering on Jenkins.
- `DEPLOY_ROOT`, `DEPLOY_APP_DIR`, and `DOTNET_CURRENT` (`/opt/dotnet/current/dotnet`, matching
  the `scripts/dotnet/dotnet-set-current.sh` convention already used for the production
  runtime) are centralized as pipeline `environment` variables, so the VPS path convention -
  already assumed by `appsettings.Production.json` and ADR 0001's crontab example - lives in
  exactly one place in the pipeline instead of being repeated per stage.
- `DEPLOY_ROOT/scripts` is created (empty) to match the directory layout issue #107 proposed
  (`/opt/poineer-render/{app,data,logs,scripts}`); nothing populates it yet.

Out of scope (see issue #107): blue/green deployment, rollback handling, containerization,
Kubernetes, zero-downtime deployment, multi-server deployment.

## Consequences

- Every `release/*` push now deploys automatically - no manual `scp`/`rsync` to the VPS is
  needed anymore, and the artifact ends up at exactly the path ADR 0001's crontab line and
  `appsettings.Production.json` already expect.
- The system crontab entry itself is **not** installed by this pipeline - this only deploys
  the binary. Someone still needs to add the crontab line from ADR 0001 on the VPS once, by
  hand:
  ```text
  0 3 * * * /opt/dotnet/current/dotnet /opt/poineer-render/app/POIneer.Render.dll >> /opt/poineer-render/logs/render.log 2>&1
  ```
- There is no rollback mechanism: a bad `release/*` deploy overwrites the previous good one
  immediately (`rsync --delete`). Recovery today means re-running an earlier good `release/*`
  build in Jenkins to redeploy it - acceptable since rollback handling is explicitly out of
  scope for #107, but worth knowing before relying on this for a real incident.
- Safety against two deploys racing on the same files currently comes from the pipeline's
  existing `disableConcurrentBuilds()` option, combined with Jenkins and the render host being
  the same machine. If Jenkins ever moves to a separate build agent, this implicit safety goes
  away and would need to be rebuilt explicitly (e.g. a real remote-deploy lock) - noted here so
  that move doesn't silently reintroduce a race.
- `Verify Deployment` failing (non-zero exit, e.g. a missing config file or a bad path) fails
  the whole Jenkins build, which is deliberate: a release that deploys but can't even start
  should not be reported as a successful pipeline run.

## Current Scheduler Note

This ADR documents the original published-DLL deployment path. Since `v0.2.1`, release Docker
builds are also verified and promoted to the stable local VPS tag `poineer-render:production`.
Future scheduled production runs should prefer that Docker image via a systemd timer and
oneshot service instead of adding or updating crontab entries. See
`docs/workflows/scheduled-renders.md`.

## CI Verification Update (VPS Ops Review Cleanup)

Following the VPS operations review (`docs/workflows/vps-render-service-operations.md`)
and issue #194, the `Deploy to VPS` and `Verify Deployment` stages described above - along
with the `DEPLOY_APP_DIR` and `DOTNET_CURRENT` pipeline variables - have been removed from
the Jenkinsfile. `Verify Docker Image` already proves that the artifact actually deployed
(the Docker image) starts correctly, and does so more thoroughly than the DLL-based check
ever did: it additionally confirms Java, Flyway, and `osmium` are present and runs a real
Flyway migration against a temporary SQLite database, whereas the DLL check only ever
performed a bare `--Renderer:DryRun=true` start. Keeping a separate, less thorough check
against an artifact nobody runs in production no longer served a purpose.

The published-DLL directory this stage used to populate (`/opt/poineer-render/app`) is no
longer written to by CI; the existing directory on the VPS from prior releases is leftover
and can be removed manually. `/opt/dotnet/current` is left in place for optional manual
debugging on the VPS, even though nothing in the pipeline depends on it anymore.

## References

- #107 - Automated deployment of POIneer.Render release artifacts
- #108 - Prevent concurrent renderer executions (closed; the crontab line above expects the
  already-implemented `ISingleInstanceLock`, not `flock`)
- ADR 0001 - Prevent Overlapping Scheduled Renders (crontab target, `DryRun` rationale)
- ADR 0002 - Local Dataset Publisher (`Publisher:DestinationDir` already assumes this VPS layout)
- #194 - Clean up obsolete poineer-render VPS/CI artifacts and add service timeout
