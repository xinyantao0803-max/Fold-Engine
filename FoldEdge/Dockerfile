# FoldEdge (FH) container — All-in-One server brain with the /dashboard Web UI.
# Build from the repository root:
#   docker build -f FoldEdge/Dockerfile -t ghcr.io/xinyantao0803-max/fold-engine:v1.2 .

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY FoldEdge/FoldEdge.csproj FoldEdge/
RUN dotnet restore FoldEdge/FoldEdge.csproj

COPY FoldEdge/ FoldEdge/
RUN dotnet publish FoldEdge/FoldEdge.csproj \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# FFmpeg enables /api/media/upload transcoding. Without it FH still serves
# existing mp4/webm files, but browser-friendly transcodes are unavailable.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg ca-certificates \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

RUN mkdir -p /app/Data /app/Modules /app/Assets /app/Media /app/keys

# ListenUrl overrides appsettings.json, so the dashboard is reachable from
# outside the container (or from the host when network_mode: host is used).
ENV ListenUrl=http://0.0.0.0:9000 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_ENVIRONMENT=Production

EXPOSE 9000

ENTRYPOINT ["dotnet", "FoldEdge.dll"]
