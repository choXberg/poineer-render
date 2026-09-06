# Scheduled Renderer Execution

POIneer.Render is intended to run as a scheduled, run-to-completion job on the VPS. The
application still owns overlap protection through `ISingleInstanceLock`; the scheduler only
decides when a run starts.

At the start of each region run, the renderer sends a `HEAD` request to the configured PBF
URL and compares the returned metadata with the last successfully processed state in
`<WorkDir>/<RegionId>/render-state.json`. `ETag` is preferred when available, with
`Last-Modified` as the fallback. `Content-Length` is stored and logged as diagnostic metadata.
When the local PBF exists and the remote metadata is unchanged, the download is skipped.
`RenderRegion` still runs so output existence, overwrite, vector tile, and local staleness
checks remain authoritative. If the metadata changed, the PBF is downloaded again and the
render state is updated only after the region finishes successfully.

Since `v0.2.1`, Jenkins promotes verified release Docker images to `poineer-render:production`.
The VPS scheduler should run that stable Docker tag instead of invoking the deployed DLL
directly. Releasing a new version moves the `production` tag, so the schedule does not need to
change for each release.

## Recommended Scheduler

Use a systemd timer and oneshot service instead of cron:

- `systemctl list-timers` shows the last and next renderer run.
- `journalctl -u poineer-render.service` shows attached renderer logs.
- `Persistent=true` catches up a missed run after VPS downtime.
- `systemctl is-failed poineer-render.service` provides a scriptable failure signal.
- Unit files live in one canonical system location instead of multiple possible cron locations.

## Service Unit

Create `/etc/systemd/system/poineer-render.service`:

```ini
[Unit]
Description=Render POIneer datasets
Requires=docker.service
After=docker.service

[Service]
Type=oneshot
TimeoutStartSec=30min
ExecStart=/usr/bin/docker run --rm --name poineer-render -v /opt/poineer-render/data:/opt/poineer-render/data poineer-render:production
```

The service runs attached to Docker, so stdout/stderr is captured by journald. The container is
removed after completion because of `--rm`.

## Timer Unit

Create `/etc/systemd/system/poineer-render.timer`:

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

## Install Or Update

Install or update the units on the VPS:

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now poineer-render.timer
```

Confirm the timer is active:

```bash
systemctl list-timers poineer-render.timer
systemctl status poineer-render.timer
```

Run the service manually when needed:

```bash
sudo systemctl start poineer-render.service
```

Inspect logs and failures:

```bash
journalctl -u poineer-render.service
systemctl is-failed poineer-render.service
```

## Data Directory

The service expects the production data directory to exist and be writable by the container
user:

```bash
sudo mkdir -p /opt/poineer-render/data
sudo chown -R 10001:10001 /opt/poineer-render/data
```

`Renderer:LockFilePath` resolves below the shared data mount, so manual runs, timer-triggered
runs, and container runs share the same application-level lock.

## Cron Migration

After the timer is enabled and a manual `systemctl start poineer-render.service` succeeds,
remove the old cron entry. Check both user and system cron locations:

```bash
crontab -l
sudo crontab -l
sudo ls /etc/cron.d
```

There should be exactly one scheduler for production renders. Keeping cron and the systemd
timer enabled at the same time can trigger duplicate starts; the application lock makes this
safe, but the duplicate schedule is still operational noise.
