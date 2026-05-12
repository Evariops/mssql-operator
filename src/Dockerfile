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

USER $APP_UID

ENTRYPOINT ["dotnet", "MsSqlOperator.dll"]
