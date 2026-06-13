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
# The "-extra" variant adds ICU + tzdata: Microsoft.Data.SqlClient requires globalization
# support and throws "Globalization Invariant Mode is not supported" on the plain chiseled image
# (no ICU → .NET falls back to invariant mode → no SQL connection can be opened).
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra@sha256:de3e2d510c3b30dd10a3ababad927725839aacd0bbd6a3e8aef9a5a4408ccc12 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

USER $APP_UID

ENTRYPOINT ["dotnet", "MsSqlOperator.dll"]
