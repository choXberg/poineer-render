# VPS Render Service Operations

This document describes how `poineer-render` actually runs on the production VPS today: the
systemd units, the directory layout, ownership, failure handling, and the day-to-day
operational commands. It complements the design-level docs already in this repository -
[Scheduled Renderer Execution](scheduled-renders.md), [Docker Renderer Image](docker-renderer.md),
[Hybrid Dataset Architecture](../architecture/hybrid-dataset-architecture.md), and
[ADR 0005: Automated VPS Deployment](../decisions/0005-automated-vps-deployment.md) - by
recording the verified, current state of the running system, not just the intended design.

All values below were captured directly from the production VPS (host `cho`) and are current
as of 2026-09-06. Commands that change system state are marked accordingly; everything else is
read-only and safe to run at any time.

## Scope

In scope: reviewing and documenting the existing systemd/Docker setup, the VPS directory
layout, ownership/permissions, failure handling, and operational commands; identifying
obsolete configuration.

Out of scope (per the originating issue): replacing Docker with a direct .NET execution model,
redesigning the deployment pipeline, changing the render schedule, implementing Geofabrik
checksum-based change detection, changing artifact retention/cleanup policy, and refactoring
`poineer-server`. Findings that would require any of these are recorded as follow-ups below
instead of being implemented here.

## Architecture At A Glance

```text
poineer-render.timer  (systemd, daily 03:00 UTC, Persistent=true)
        |
        v
poineer-render.service  (systemd, Type=oneshot)
        |
        v
docker run --rm --name poineer-render \
  -v /opt/poineer-render/data:/opt/poineer-render/data \
  poineer-render:production
        |
        v
container runs as uid/gid 10001 (poineer), writes stdout/stderr (captured by journald
via the systemd unit), reads/writes /opt/poineer-render/data/prod/*
```

The service never runs as a long-lived daemon: the timer fires once a day, the oneshot service
starts a container, waits for it to exit, and then goes back to `inactive (dead)` until the
next scheduled fire (or a manual start).

## systemd Units (Verified Content)

`/etc/systemd/system/poineer-render.service`:

```ini
[Unit]
Description=Render POIneer datasets
Requires=docker.service
After=docker.service

[Service]
Type=oneshot
ExecStart=/usr/bin/docker run --rm --name poineer-render -v /opt/poineer-render/data:/opt/poineer-render/data poineer-render:production
```

`/etc/systemd/system/poineer-render.timer`:

```ini
[Unit]
Description=Run POIneer renderer daily

[Timer]
OnCalendar=*-*-* 03:00:00
Persistent=true
Unit=poineer-render.service

[Install]
WantedBy=timers.target
```

Both units match `docs/workflows/scheduled-renders.md` exactly - no drift between the
documented recommendation and what is actually installed.

Notable properties:

- No `User=`/`Group=` on the service: the unit itself runs as **root** (required to talk to
  the Docker daemon via `/usr/bin/docker`). The actual render workload drops privileges inside
  the container to uid/gid `10001` (see [Directory Structure And Ownership](#directory-structure-and-ownership)).
  Root only ever runs `docker run`; it never touches application code or data directly.
- `Requires=docker.service` + `After=docker.service` correctly orders startup after Docker is
  available and stops the unit from starting (rather than failing later) if Docker is down.
- No `Restart=` directive. This is intentional for a oneshot batch job: the timer already
  re-triggers the next run on its own schedule, and blind auto-restart on failure could race
  with the application's own single-instance lock or a partially-written output. A failed run
  should be investigated (see [Troubleshoot A Failed Execution](#troubleshoot-a-failed-execution)),
  not silently retried.
- No `RemainAfterExit=yes`. This is what makes `inactive (dead)` the expected end state -
  see next section.

## Why `inactive (dead)` After A Successful Run Is Correct

Live status, captured right after a scheduled run:

```text
○ poineer-render.service - Render POIneer datasets
     Loaded: loaded (/etc/systemd/system/poineer-render.service; static)
     Active: inactive (dead) since Sun 2026-09-06 03:00:03 UTC; 3h 32min ago
TriggeredBy: ● poineer-render.timer
   Main PID: 1739293 (code=exited, status=0/SUCCESS)
        CPU: 53ms

$ systemctl is-failed poineer-render.service
inactive
```

`Type=oneshot` tells systemd "run `ExecStart` once, to completion, and consider the unit's
job done" - there is no long-running process for systemd to keep "active". Without
`RemainAfterExit=yes`, the unit transitions straight to `inactive` the moment `ExecStart`
exits with status 0. `status=0/SUCCESS` and `is-failed` reporting `inactive` (not `failed`)
together confirm the run completed successfully; `inactive (dead)` is simply what "finished
successfully" looks like for a oneshot unit. This is the expected, healthy steady state
between scheduled runs - **not** a sign that the service crashed or was never started.

Do not add `RemainAfterExit=yes` to "fix" this - doing so would make the unit report
`active (exited)` forever after the first successful run and would stop reflecting whether
the *next* run actually happened.

### A note on the `CPU: 53ms` figure

`systemctl status` reported only 53ms of CPU time for the run above, and the artifact
timestamps under `renderer-out-dir`/`renderer-publish-dir` were unchanged from a run several
weeks earlier (see [Directory Structure And Ownership](#directory-structure-and-ownership)).
Two things explain this together, and neither indicates a bug:

1. `docker run` (without `-d`) in the unit is a thin client that waits on the Docker daemon;
   the actual container process tree is typically accounted under Docker's/containerd's own
   cgroups, not fully rolled up into the systemd unit's own CPU accounting. So `systemctl
   status`'s CPU figure understates real container work and should not be used to judge how
   much rendering actually happened.
2. Per `docs/workflows/scheduled-renders.md`, the renderer checks the remote PBF's `ETag`/
   `Last-Modified` before downloading, and skips the download when the source is unchanged.
   `RenderRegion` still runs (existence/overwrite/staleness checks stay authoritative), but
   with `Publisher:OverwritePolicy=SkipIfIdentical` an unchanged source correctly produces no
   new output write. A fast, low-CPU, "successful" run with no new output files is the
   expected result when upstream OSM data hasn't changed since the last run - not evidence
   that the render silently failed.

To confirm what a specific run actually did, don't rely on the CPU figure alone - check the
journal for that run's log lines (see [View Current And Historical Logs](#view-current-and-historical-logs))
together with the output directory timestamps.

## Directory Structure And Ownership

Verified layout under `/opt/poineer-render` (trimmed to the parts that matter operationally):

| Path | Owner | Role |
| --- | --- | --- |
| `/opt/poineer-render/` | `jenkins:jenkins` | Deploy root |
| `/opt/poineer-render/app/` | `jenkins:jenkins` | Published `.NET` DLL artifact from the now-removed ADR 0005 deploy stage - CI no longer writes here; the directory itself is leftover from prior releases and pending manual removal, see [Known Issues](#known-issues--obsolete-configuration-found-during-this-review) |
| `/opt/poineer-render/scripts/` | `jenkins:jenkins` | Reserved by ADR 0005, still empty/unused |
| `/opt/poineer-render/data/` | `10001:10001` (`poineer`) | Shared bind mount into the container; matches `Renderer:*`/`Publisher:*` paths in `appsettings.Production.json` |
| `/opt/poineer-render/data/prod/renderer-work-dir/{berlin,mittelfranken}/` | `10001:10001` | Downloaded PBF + per-region work state |
| `/opt/poineer-render/data/prod/renderer-out-dir/{berlin,mittelfranken}/` | `10001:10001` | Canonical generated SQLite artifacts |
| `/opt/poineer-render/data/prod/renderer-publish-dir/{berlin,mittelfranken}/` | `10001:10001` | Locally published (verified) datasets - this is what a downstream consumer such as `poineer-server` would read from today (`Publisher:Target=Local`) |
| `/opt/poineer-render/data/prod/poineer-render.lock` | `10001:10001` | **Current, correct** single-instance lock file (matches `Renderer:LockFilePath`) |

Inside the container itself:

```text
$ docker run --rm --entrypoint id poineer-render:production
uid=10001(poineer) gid=10001(poineer) groups=10001(poineer)
```

This matches `docs/workflows/docker-renderer.md` exactly: the image runs as a non-root
`poineer` user (10001:10001), and the bind-mounted data directory is owned by that same
uid/gid so the container can read and write it without privilege escalation.

**A note on the current lock file content.** `cat`-ing the lock file shows stale-looking data
(`pid=1 startedUtc=2026-09-06T03:00:03...`) even when nothing is running - this is expected.
`pid=1` is the container's own PID namespace value (the renderer is PID 1 inside its
container), and the file's *content* is simply the metadata written when the lock was last
held; the OS-level advisory byte-range lock itself is released automatically when the holding
process exits, regardless of what the file still says. **Do not use the file's content to
decide whether a render is currently running** - use `systemctl status poineer-render.service`
(look for `Active: active (running)` vs `inactive`) or `docker ps --filter name=poineer-render`
instead.

### Region ID layout is about to change (ADR 0007 not yet deployed)

The directory names observed above (`berlin`, `mittelfranken`) are the **old, flat** region
ids. `docs/decisions/0007-hierarchical-region-identifiers.md` (merged into the repository's
`develop` history, commit `0a4126c`) switches configured region ids to hierarchical form
(`geofabrik/europe/germany/berlin`, `geofabrik/europe/germany/bayern/mittelfranken`), which
changes the on-disk layout to nested directories
(`renderer-out-dir/geofabrik/europe/germany/berlin/...`, etc.).

The currently promoted `poineer-render:production` image (`v0.2.1`, released 2026-08-28)
**predates** that change - which is exactly why the VPS still shows the old flat directories.
This is not a bug today, but it means the *next* `release/*` deploy that includes ADR 0007
will silently start writing to new, nested paths, and the old flat directories become
orphaned (ADR 0007 explicitly documents them as safe to delete, but does not delete them
automatically). Plan a one-time manual cleanup of the old `berlin/`/`mittelfranken/`
directories under `renderer-work-dir`, `renderer-out-dir`, and `renderer-publish-dir` once
that release has been running successfully for a few days - do not delete them pre-emptively
before the switch actually happens.

To confirm which region-id shape a given deployed image actually uses:

```bash
docker run --rm --entrypoint cat poineer-render:production \
  /opt/poineer-render/app/Cli/config/regions.production.json
```

## Environment And Production Configuration

Configuration is not injected through a separate systemd `EnvironmentFile=` or `.env` file -
there is none on the VPS (`/etc/systemd/system/poineer-render.service.d/*.conf` does not
exist, and no `*.env` file exists under `/opt/poineer-render`). Instead:

- `appsettings.Production.json` is baked into the Docker image at build time (`ASPNETCORE`/
  `DOTNET_ENVIRONMENT=Production` equivalent for this CLI app is set via the image's own
  entrypoint/config loading, not a host-level env file).
- Absolute production paths (`/opt/poineer-render/data/prod/...`) are already correct for the
  container because the same absolute paths exist both on the host and inside the container
  via the bind mount - no path translation is needed between host and container.
- `Publisher:Target` is `Local` in the current production config (not `AzureBlob`) - datasets
  are published to `/opt/poineer-render/data/prod/renderer-publish-dir`, not to Azure Blob
  Storage, despite the hybrid architecture doc describing both as implemented options. Any
  future consumer (e.g. `poineer-server`) integration should target this local publish
  directory unless/until `Publisher:Target` is deliberately switched.
- `VectorTiles:Enabled` is `false` in production - no Planetiler/vector tile output is
  produced today even though the image bundles the tool.

If production configuration ever needs a per-host override without rebuilding the image, the
conventional approach is `docker run -e POINEER_RENDER__<Section>__<Key>=<value> ...` added to
`ExecStart=`, not a new file - keep that in mind before introducing a new mechanism.

## Failure Handling, Timeouts, And Exit-Code Propagation

`docker run` (no `-d`) runs in the foreground and exits with the **container's own exit code**
once it stops - it does not swallow failures. Because `ExecStart` is exactly that `docker run`
invocation, a non-zero container exit code becomes `ExecStart`'s own non-zero exit code, which
systemd always treats as the unit failing:

- `systemctl status poineer-render.service` would show `Active: failed (Result: exit-code)`
  and the specific `status=<N>/FAILURE`-style line instead of `SUCCESS`.
- `systemctl is-failed poineer-render.service` would print `failed` instead of `inactive`.
- The failure is visible in `journalctl -u poineer-render.service` alongside whatever the
  renderer logged to stdout/stderr before exiting.

This satisfies the acceptance criterion that "Docker failures cause the systemd service
execution to be reported as failed" - the mechanism is correct by construction (foreground
`docker run` + oneshot). No historical failed run exists in the retained journal to point to
as a live example at the time of writing; if you want to see this behavior directly, the
safest way is a manual, throwaway test (e.g. temporarily running `docker run --rm
--entrypoint false poineer-render:production` by hand, *not* through the real unit) rather
than forcing a failure in the production timer path.

The one gap identified is the missing `TimeoutStartSec` override discussed above - a timeout
kill looks the same as an application failure in `is-failed`/`status`, so if you ever see a
"failed" run, check `journalctl` for a `start-timeout` fired by systemd itself.

## Concurrency Protection

Two independent layers currently prevent overlapping renders, and both were verified to still
be intact:

1. **Application-level lock** (`ISingleInstanceLock`, ADR 0001): `Renderer:LockFilePath` =
   `/opt/poineer-render/data/prod/poineer-render.lock`, inside the shared data bind mount, so
   it is shared by the timer-triggered container, any manual `systemctl start`, and (were it
   ever used again) a direct DLL invocation. A second run started while one is in-flight is
   skipped, not queued, and logs a clear message - it does not fail the unit.
2. **No competing scheduler**: `crontab -l` (user) is empty, `sudo crontab -l` contains only
   unrelated system entries (`sa-train.sh`, etc. - nothing for `poineer-render`), and
   `/etc/cron.d` has no `poineer-render` file either. The old ADR 0001/ADR 0005 crontab line
   has been fully removed - `poineer-render.timer` is the **only** scheduler, exactly as
   `docs/workflows/scheduled-renders.md` recommends. Nothing to migrate here; this was
   already done correctly.

`Runner.RunAsync` skips lock acquisition entirely when `--Renderer:DryRun=true` is passed
(e.g. the Jenkins "Verify Deployment"/"Verify Docker Image" stages), so CI verification runs
never contend with a real production render for the lock.

## Operational Commands

### Inspect service and timer status

```bash
systemctl status poineer-render.service --no-pager -l
systemctl status poineer-render.timer --no-pager -l
systemctl list-timers poineer-render.timer --no-pager
```

`list-timers` is the fastest way to see the last and next scheduled fire time at a glance.

### View current and historical logs

```bash
sudo journalctl -u poineer-render.service --no-pager -n 200
sudo journalctl -u poineer-render.service --since "-2d"
sudo journalctl -u poineer-render.service -f          # follow a run live
```

**Gotcha found during this review:** running `journalctl -u poineer-render.service` *without*
`sudo` as a regular, non-privileged user (e.g. `cho`) can print "No entries" even though the
unit has run and produced output - journald hides other users'/system logs from users who
aren't in the `adm`/`systemd-journal` group. Either always prefix with `sudo`, or add the
operating user once: `sudo usermod -aG systemd-journal <user>` (requires re-login to take
effect). Document this for anyone who is confused by an apparently-empty log for a service
that visibly ran (`Active: inactive (dead) since ...`).

`docker logs poineer-render` only works while the container still exists; because the unit
uses `--rm`, the container is removed immediately on exit, so `docker logs` is only useful
while watching a run in progress (`docker ps --filter name=poineer-render` first to confirm
it's currently running).

### Trigger a render manually

```bash
sudo systemctl start poineer-render.service
```

Safe to run at any time: it goes through the same image, the same data mount, and the same
application-level lock as a scheduled run. If a scheduled run happens to be in flight already,
the manual start's render is skipped (not queued) and that skip is logged - it does not error
out the unit.

### Stop or disable scheduled rendering

- Stop a run **currently in progress**: `sudo systemctl stop poineer-render.service` (only
  meaningful while `Active: active (running)`; a no-op once the run has already finished).
- Prevent all **future scheduled** runs: `sudo systemctl disable --now poineer-render.timer`
  (stops and disables the timer; the service itself is untouched and can still be started
  manually). Re-enable with `sudo systemctl enable --now poineer-render.timer`.
- Do not disable the *service* to pause scheduling - the service has no independent trigger of
  its own; disabling the timer is the correct way to pause scheduled execution.

### Verify the result of the most recent execution

```bash
systemctl is-failed poineer-render.service      # "inactive" = last run succeeded, "failed" = it didn't
systemctl status poineer-render.service --no-pager -l   # exit code + "since" timestamp
sudo journalctl -u poineer-render.service --since "-1d"  # what the run actually logged
ls -la /opt/poineer-render/data/prod/renderer-publish-dir/*/   # did output actually change?
```

Remember: a fast, "successful" run with unchanged output timestamps can be entirely correct
(see [the CPU/skip-detection note above](#a-note-on-the-cpu-53ms-figure)) - check the journal
for the actual per-run log lines before assuming something is wrong.

### Troubleshoot a failed execution

1. `systemctl status poineer-render.service --no-pager -l` - is it a systemd-level failure
   (e.g. `start-timeout`, missing image, Docker daemon down) or did the container itself exit
   non-zero?
2. `sudo journalctl -u poineer-render.service --since "-1d"` - the renderer's own log output
   (config resolution, PBF download errors, Flyway migration errors, validation failures) is
   here.
3. `docker image ls poineer-render` - confirm the `production` tag exists and points at the
   image you expect (compare the image ID against the last `release/*` Jenkins build).
4. `ls -la /opt/poineer-render/data` and subdirectories - confirm ownership is still
   `10001:10001`; a manual `chown`/`chmod` elsewhere on the host, or a bind-mount remount as a
   different UID, would cause the container to fail on its own data with permission errors.
5. `cat /opt/poineer-render/data/prod/poineer-render.lock` plus `systemctl status` /
   `docker ps -a` - rule out (extremely unlikely, see the lock-file note above) a genuinely
   stuck lock from a previous run that was killed ungracefully (e.g. host reboot mid-render).
6. `df -h /opt/poineer-render/data` - rule out disk space, especially before a large PBF
   download.

## Shared Conventions With `poineer-server`

`poineer-server` has **no VPS deployment yet** at the time of this review - its Jenkinsfile
contains placeholder ("Dummy Deployment") stages only, and "VPS deployment" is still listed as
a planned feature in its README. There is therefore nothing live to align *against* today.
Instead, this section records the conventions `poineer-render` already established on the VPS
so that `poineer-server`'s eventual deployment can follow the same pattern from day one
instead of inventing a new one:

| Convention | `poineer-render` (current) | Recommended for `poineer-server` |
| --- | --- | --- |
| Unit naming | `poineer-render.service` / `poineer-render.timer` | `poineer-server.service` (+ a timer only if it ever needs scheduled batch work; an always-on API more likely wants `Restart=on-failure` and no timer at all) |
| Filesystem root | `/opt/poineer-render/{app,data,logs,scripts}` | `/opt/poineer-server/{app,data,logs}` for consistency, even though the API's actual persistent-data needs will differ |
| Execution | Docker image, `--rm`, non-root in-container user | Same: containerize, run as a dedicated non-root uid (does not need to reuse `10001` - a distinct uid per service is fine and arguably clearer) |
| Logging | stdout/stderr captured by journald via the systemd unit (no bespoke log file) | Same. Do **not** replicate `poineer-render`'s old `logs/render.log` file-based pattern - that predates the journald convention and is now stale/obsolete (see below); journald should be the only log sink from the start |
| Image promotion | Jenkins builds a per-build tag, promotes to `<version>` + `<service>:production` on `release/*` | Same tagging scheme for consistency across services |

## Known Issues / Obsolete Configuration Found During This Review

Per the acceptance criteria, everything below was either identified for cleanup now (low-risk,
pure housekeeping) or is called out explicitly as a follow-up (anything that would touch the
deployment pipeline itself, which is out of scope for this issue):

| Finding | Risk | Recommendation |
| --- | --- | --- |
| `/opt/poineer-render/app` (published DLL) + Jenkins "Deploy to VPS"/"Verify Deployment" stages + `/opt/dotnet/current` symlink were exercised on every `release/*` build, but the artifact they produced was **not** used by the scheduled service anymore (superseded by Docker/systemd per `scheduled-renders.md`) | Low (CI-only, didn't affect production runtime) | **Decided (issue #194):** dropped. `Deploy to VPS` and `Verify Deployment` (plus the `DEPLOY_APP_DIR`/`DOTNET_CURRENT` variables) removed from the Jenkinsfile - see the CI update note in [ADR 0005](../decisions/0005-automated-vps-deployment.md#ci-verification-update-vps-ops-review-cleanup). The existing `/opt/poineer-render/app` directory on the VPS is now dead weight and still needs a one-time manual removal. |
| `/opt/poineer-render/logs/render.log` - last written 2026-08-28 03:00, i.e. before the systemd timer was even enabled that same day (10:54); no longer written since the cron migration | Low | Safe to archive/delete now; it is pure leftover from the pre-systemd cron setup and not read by anything |
| `/opt/poineer-render/poineer-render.lock` (root of the deploy tree, distinct from the correct `data/prod/poineer-render.lock`) | Low | Orphaned from the old, pre-migration `LockFilePath`; safe to delete now |
| `/opt/poineer-render/scripts/` - created empty by the ADR 0005 pipeline, still unused | None | Leave documented as "intentionally reserved, currently unpopulated" unless/until something actually needs it; removing the `mkdir` would touch the pipeline (follow-up territory) |
| Orphaned feature-branch Docker images (e.g. `poineer-render:feature_docker-image-planetiler-jar-2/-3`, ~961MB each) - the Jenkinsfile's pruning stage only matches `vX.Y.Z` release tags, so non-release build tags accumulate indefinitely | Medium (disk space over time) | Safe to `docker image rm` manually now; **follow-up issue** if you also want Jenkins to prune non-release tags automatically (that's a pipeline change) |
| No `TimeoutStartSec` override on `poineer-render.service` | Medium (a legitimately slow cold render could be killed by the systemd default and misreported as a failure) | Add an explicit generous `TimeoutStartSec` to the unit - a small, targeted unit-file fix, not a pipeline redesign |
| ADR 0007 hierarchical region ids merged in source but not yet in the deployed `production` image; on-disk layout is still the old flat `berlin/`/`mittelfranken/` shape | None today | Documented above; plan a one-time cleanup of the old flat directories once the next release (including ADR 0007) has been running successfully for a few days |
| `journalctl -u poineer-render.service` returns "No entries" for a non-privileged operator without `sudo`/`systemd-journal` group membership, even though the service has run | Low (operator confusion, not a system defect) | Documented above; add relevant operators to `systemd-journal` or always use `sudo` |

## Related Documents

- [Scheduled Renderer Execution](scheduled-renders.md)
- [Docker Renderer Image](docker-renderer.md)
- [Hybrid Dataset Architecture](../architecture/hybrid-dataset-architecture.md)
- [Azure Dataset Storage](../workflows/azure-dataset-storage.md)
- [ADR 0001: Prevent Overlapping Scheduled Renders](../decisions/0001-prevent-overlapping-scheduled-renders.md)
- [ADR 0005: Automated VPS Deployment](../decisions/0005-automated-vps-deployment.md)
- [ADR 0007: Hierarchical Region Identifiers](../decisions/0007-hierarchical-region-identifiers.md)
