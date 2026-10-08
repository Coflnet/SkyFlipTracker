FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /build
RUN git init dev \
    && git -C dev remote add origin https://github.com/Coflnet/HypixelSkyblock.git \
    && git -C dev fetch --depth=1 origin c4ff80f4993e3710503cdf3a6832950096cb0957 \
    && git -C dev checkout --detach FETCH_HEAD
WORKDIR /build/sky
COPY SkyFlipTracker.csproj SkyFlipTracker.csproj
RUN dotnet restore
COPY . .
RUN dotnet test
RUN dotnet publish -c release -o /app && rm /app/items.json

# noble-chiseled-extra ships a pre-configured non-root $APP_UID user and no
# shell/package manager. Deployed with readOnlyRootFilesystem: true, so
# nothing may write to disk at runtime besides transient state under
# HOME=/tmp.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra
WORKDIR /app

COPY --from=build --chown=$APP_UID:$APP_UID /app .

ENV ASPNETCORE_URLS=http://+:8000 \
    DOTNET_EnableDiagnostics=0 \
    COMPlus_EnableDiagnostics=0 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    HOME=/tmp \
    TMPDIR=/tmp

USER $APP_UID

ENTRYPOINT ["dotnet", "SkyFlipTracker.dll", "--hostBuilder:reloadConfigOnChange=false"]
