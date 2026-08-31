# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
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

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /out/ ./
COPY viewer/ ./viewer/
EXPOSE 8137
ENTRYPOINT ["./SmokeSolver.Cli"]
CMD ["serve", "--bind", "any"]
