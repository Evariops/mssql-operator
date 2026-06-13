# Build on the native build platform (no QEMU): the app is published without a RID, so the
# output is portable IL that runs on any architecture. buildx then copies it into the
# per-architecture (multi-arch) aspnet runtime base to assemble each platform's image.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0@sha256:c0790639332692a0d56cdd81ed581cfd24d040d9839764c138994866df89a3b6 AS build
WORKDIR /src
COPY ["MsSqlOperator/MsSqlOperator.csproj", "MsSqlOperator/"]
RUN dotnet restore "MsSqlOperator/MsSqlOperator.csproj"
COPY . .
WORKDIR "/src/MsSqlOperator"
RUN dotnet publish -c Release -o /app/publish

# Chiseled runtime — no shell, no package manager, minimal attack surface.
# Runs as the non-root app user (UID 64198) baked into the chiseled image.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled@sha256:f140a7fd9d0885581e70bfcc935ec1f738c30eeef04a54764db3064e60d9389e AS runtime
WORKDIR /app
COPY --from=build /app/publish .

USER $APP_UID

ENTRYPOINT ["dotnet", "MsSqlOperator.dll"]
