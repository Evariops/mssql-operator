# Build on the native build platform (no QEMU): the app is published without a RID, so the
# output is portable IL that runs on any architecture. buildx then copies it into the
# per-architecture (multi-arch) aspnet runtime base to assemble each platform's image.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0@sha256:ea8bde36c11b6e7eec2656d0e59101d4462f6bd630730f2c8201ed0572b295d5 AS build
# Build context is the repo root so the publish sees the root-level Directory.Build.props
# (GenerateDocumentationFile -> MsSqlOperator.xml, read at runtime for CRD descriptions) and
# .editorconfig (CA2100=suggestion; required because TreatWarningsAsErrors is on).
WORKDIR /build
COPY ["src/MsSqlOperator/MsSqlOperator.csproj", "src/MsSqlOperator/"]
RUN dotnet restore "src/MsSqlOperator/MsSqlOperator.csproj"
COPY . .
WORKDIR "/build/src/MsSqlOperator"
RUN dotnet publish -c Release -o /app/publish

# Chiseled runtime — no shell, no package manager, minimal attack surface.
# Runs as the non-root app user (UID 64198) baked into the chiseled image.
# The "-extra" variant adds ICU + tzdata: Microsoft.Data.SqlClient requires globalization
# support and throws "Globalization Invariant Mode is not supported" on the plain chiseled image
# (no ICU → .NET falls back to invariant mode → no SQL connection can be opened).
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra@sha256:f35864ca57c18f2dcd7164dd20256a1b5236c34f7883a7f32abc42ba70a56f0f AS runtime
WORKDIR /app
COPY --from=build /app/publish .

USER $APP_UID

ENTRYPOINT ["dotnet", "MsSqlOperator.dll"]
