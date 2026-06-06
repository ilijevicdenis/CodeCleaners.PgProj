# Container image bundling the pgproj CLI for any-CI use (GitHub, Azure DevOps, GitLab, Jenkins…).
#
# Build:   docker build -t pgproj:latest .
# Run:     docker run --rm -v "$PWD:/work" -w /work pgproj:latest build sample/SampleDb/SampleDb.pgproj
#          docker run --rm -v "$PWD:/work" -w /work -e PGPROJ_CONNECTION="$CONN" \
#                     pgproj:latest publish sample/SampleDb/SampleDb.pgproj --dry-run
#
# The entrypoint is the `pgproj` CLI, so `docker run pgproj:latest <verb> ...` maps 1:1 to
# `pgproj <verb> ...`. Process exit codes are the stable contract in docs/CICD.md, so CI systems
# that fail a step on a non-zero container exit get the classified codes for free.

# ---- build stage: compile + publish the CLI -----------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against the solution first for layer caching, then bring in the rest of the sources.
COPY PgProj.slnx ./
COPY src/ ./src/
COPY tests/ ./tests/
COPY bench/ ./bench/

# Publish only the CLI (self-contained framework-dependent; the runtime image carries the runtime).
RUN dotnet publish src/PgProj.Cli/PgProj.Cli.csproj -c Release -o /app

# ---- runtime stage: minimal runtime + the published CLI -----------------------------------------
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /work
COPY --from=build /app /opt/pgproj

# Wrapper so the image is invoked as `pgproj <verb>`; CI mounts the repo at /work (the WORKDIR).
RUN printf '#!/bin/sh\nexec dotnet /opt/pgproj/PgProj.Cli.dll "$@"\n' > /usr/local/bin/pgproj \
    && chmod +x /usr/local/bin/pgproj

ENTRYPOINT ["pgproj"]
CMD ["help"]
