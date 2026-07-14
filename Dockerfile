# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
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
