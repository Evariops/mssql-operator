# Build on the native build platform (no QEMU): the app is published without a RID, so the
# output is portable IL that runs on any architecture. buildx then copies it into the
# per-architecture (multi-arch) aspnet runtime base to assemble each platform's image.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
# Build context is the repo root so the publish sees the root-level Directory.Build.props
# (GenerateDocumentationFile -> MsSqlOperator.xml, read by the `generate` command for the CRD
# descriptions — the operator itself never reads it) and .editorconfig (CA2100=suggestion; required
# because TreatWarningsAsErrors is on).
WORKDIR /build
COPY ["src/MsSqlOperator/MsSqlOperator.csproj", "src/MsSqlOperator/"]
COPY ["src/MsSqlOperator.Core/MsSqlOperator.Core.csproj", "src/MsSqlOperator.Core/"]
RUN dotnet restore "src/MsSqlOperator/MsSqlOperator.csproj"
COPY . .
WORKDIR "/build/src/MsSqlOperator"
RUN dotnet publish -c Release -o /app/publish

# Chiseled runtime — no shell, no package manager, minimal attack surface.
# Runs as the non-root app user (UID 64198) baked into the chiseled image.
# The "-extra" variant adds ICU + tzdata: Microsoft.Data.SqlClient requires globalization
# support and throws "Globalization Invariant Mode is not supported" on the plain chiseled image
# (no ICU → .NET falls back to invariant mode → no SQL connection can be opened).
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra@sha256:9b755ea799ae649141a073e30d8b62d8095a5465af2b7694d17866e8c05131d8 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

USER $APP_UID

ENTRYPOINT ["dotnet", "MsSqlOperator.dll"]
