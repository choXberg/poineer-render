# Docker Renderer Image

The renderer can be packaged as a Docker image that contains the published .NET runtime
artifact plus the external tools required by production rendering:

- .NET runtime
- Java 21 runtime
- Flyway CLI
- pinned Planetiler JAR
- `osmium-tool` for polygon extraction when a region provides a `.poly` file

The image stores application files below `/opt/poineer-render/app` and bundled tools below
`/opt/poineer-render/tools`. Runtime data, rendered region outputs, published datasets, and
the single-instance lock live below `/opt/poineer-render/data` so concurrent containers that
share the same data mount also share the same lock.

The renderer writes application logs to stdout/stderr. Use `docker logs` or the Docker logging
driver configured by the host to retain container logs.

## Build

```bash
docker build \
  --build-arg PLANETILER_VERSION=0.10.2 \
  -t poineer-render:local .
```

For release builds, also pass the expected tool checksums:

```bash
docker build \
  --build-arg PLANETILER_VERSION=0.10.2 \
  --build-arg PLANETILER_SHA256=<sha256> \
  --build-arg FLYWAY_SHA256=<sha256> \
  -t poineer-render:<version> .
```

Jenkins enforces `PLANETILER_SHA256` and `FLYWAY_SHA256` for `release/*` branch builds.
Feature branch builds may use the upstream checksum sidecars as a convenience while iterating.
In Jenkins, provide the release checksums through the `PLANETILER_SHA256` and `FLYWAY_SHA256`
build parameters. They are public integrity values, not secrets.

The image runs as a non-root `poineer` user. Its default UID/GID is `10001`, and both can be
overridden at build time:

```bash
docker build \
  --build-arg APP_UID=10001 \
  --build-arg APP_GID=10001 \
  -t poineer-render:local .
```

## Verify

Run a dry start against the production configuration:

```bash
docker run --rm poineer-render:local --Renderer:DryRun=true
```

Check the bundled external tools:

```bash
docker run --rm --entrypoint sh poineer-render:local -c "id && java -version && flyway -v && osmium --version && test -s /opt/poineer-render/tools/planetiler/planetiler.jar"
```

Run a Flyway migration smoke test against a temporary SQLite database inside the container:

```bash
docker run --rm --entrypoint sh poineer-render:local -c "cd /opt/poineer-render/app && flyway -configFiles=\"migrations/flyway-poi.toml\" -url=\"jdbc:sqlite:/tmp/poineer-migration-smoke.sqlite\" -locations=\"filesystem:migrations/sql/poi\" migrate && test -s /tmp/poineer-migration-smoke.sqlite"
```

## Run On The VPS

Mount the production data directory:

```bash
docker run --rm \
  --name poineer-render \
  -v /opt/poineer-render/data:/opt/poineer-render/data \
  poineer-render:production
```

When using bind mounts on Linux, make sure the mounted data directory is writable by the
container user:

```bash
sudo mkdir -p /opt/poineer-render/data
sudo chown -R 10001:10001 /opt/poineer-render/data
```

Configuration is still loaded from `appsettings.Production.json`, environment variables with
the `POINEER_RENDER__` prefix, and command-line arguments. For example:

```bash
docker run --rm \
  -v /opt/poineer-render/data:/opt/poineer-render/data \
  poineer-render:production \
  --Renderer:OnlyRegionId=geofabrik/europe/germany/berlin \
  --VectorTiles:Enabled=true
```

Vector tile generation remains disabled by default in production configuration until a release
explicitly enables it or passes `--VectorTiles:Enabled=true`.

## Release Image Deployment

Jenkins builds every Docker image with a temporary CI tag derived from the branch and build
number, for example `poineer-render:release_v0.2.1-42`. After a successful `release/*`
build verifies the image, Jenkins promotes that exact image to two stable local VPS tags:

- `poineer-render:<release-version>`, for example `poineer-render:v0.2.1`
- `poineer-render:production`

The VPS scheduler should use `poineer-render:production`, so future releases do not require a
scheduler change. Releasing a new version moves the `production` tag to the newly verified
image. See [Scheduled Renderer Execution](scheduled-renders.md) for the recommended systemd
timer setup.

Release branches must be named like `release/v0.2.1`; Jenkins derives the release image tag
from the branch suffix. After promotion, Jenkins removes the temporary CI tag during post-build
cleanup. A release cleanup step keeps the two newest semantic release tags and leaves
`poineer-render:production` untouched.
