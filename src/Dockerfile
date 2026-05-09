FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["MsSqlOperator/MsSqlOperator.csproj", "MsSqlOperator/"]
RUN dotnet restore "MsSqlOperator/MsSqlOperator.csproj"
COPY . .
WORKDIR "/src/MsSqlOperator"
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

RUN adduser --disabled-password --gecos "" appuser
USER appuser

ENTRYPOINT ["dotnet", "MsSqlOperator.dll"]
