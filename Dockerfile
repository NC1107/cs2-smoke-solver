# syntax=docker/dockerfile:1
# Base images are pinned by digest, not by the floating 10.0 tag: Microsoft
# repoints that tag on every patch release, so two builds of the same commit
# would otherwise pull different runtimes. The trailing comment is the tag each
# digest stood for; bump both deliberately.
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c AS build
WORKDIR /src
# Project files first: this restore layer only exists to download the NuGet
# packages once, so a source-only change reuses it and the publish below
# re-restores from the warm local cache without touching the network.
COPY src/Cli/SmokeSolver.Cli.csproj src/Cli/
COPY src/Sim/SmokeSolver.Sim.csproj src/Sim/
COPY src/Solver/SmokeSolver.Solver.csproj src/Solver/
COPY src/Extraction/SmokeSolver.Extraction.csproj src/Extraction/
RUN dotnet restore src/Cli/SmokeSolver.Cli.csproj -r linux-x64
COPY src/ src/
RUN dotnet publish src/Cli/SmokeSolver.Cli.csproj \
    -c Release -r linux-x64 --self-contained false -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:a4556ed033fa96f984bb7a8d348851cb2d36b1281dd2420070045f664fbb5f94 AS runtime
WORKDIR /app
COPY --from=build /out/ ./
COPY viewer/ ./viewer/
EXPOSE 8137

# NOT switched to USER $APP_UID yet, deliberately. The process writes the solve
# cache into the bind-mounted ./data, and on the current prod host data/cache is
# owned by root:root because this container created it while running as root.
# Adding USER here without first running
#     sudo chown -R 1654:1654 ~/docker-server/npc_projects/cs2-smoke-solver/data
# on the host makes every cache write fail on the next `compose up`. Do the
# chown and the USER line together, in that order.

# Checks that solves would return something, which a liveness ping cannot: an
# empty attribute filter, an unmounted data volume, or missing nav data all
# leave a process that answers every request promptly with zero lineups.
#
# The --attrs here must match the CMD below. The check builds the same filter
# the server builds, and passing nothing would silently fall back to the default
# filter and pass while the server itself was running on an empty one - which is
# precisely the failure this exists to catch.
#
# Runs in ~0.4s and ~70MB: it loads one map, deliberately not all of them (that
# peaks near the container's whole 2G allowance and would OOM the server).
HEALTHCHECK --interval=5m --timeout=30s --start-period=90s --retries=3 \
    CMD ["./SmokeSolver.Cli", "selfcheck", "--root", "/app", "--attrs", "Default,default,EntitySolid"]

ENTRYPOINT ["./SmokeSolver.Cli"]
# --attrs belongs in the image, not only in compose. Without it the attribute
# filter is empty, which drops world geometry, and every solve then returns zero
# lineups from a process that looks perfectly healthy - this project's single
# most-repeated failure. Compose should override only what varies per deploy.
CMD ["serve", "--bind", "any", "--attrs", "Default,default,EntitySolid"]
