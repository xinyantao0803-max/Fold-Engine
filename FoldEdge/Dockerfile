# FoldEdge (FH) container.
# Build from the repository root:
#   docker build -f FoldEdge/Dockerfile -t foldedge:latest .

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY FoldEdge/FoldEdge.csproj FoldEdge/
RUN dotnet restore FoldEdge/FoldEdge.csproj

COPY FoldEdge/ FoldEdge/
RUN dotnet publish FoldEdge/FoldEdge.csproj \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

COPY --from=build /app/publish .

# FH listens on loopback by default. With Docker host networking, the game
# server on the same host can reach ws://127.0.0.1:9000/ws.
ENV ASPNETCORE_URLS=http://127.0.0.1:9000
EXPOSE 9000

ENTRYPOINT ["dotnet", "FoldEdge.dll"]
