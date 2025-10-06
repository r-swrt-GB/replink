FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 80

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["AnalyticsApi.csproj", "./"]
RUN dotnet restore "AnalyticsApi.csproj"
COPY . .
RUN dotnet build "AnalyticsApi.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "AnalyticsApi.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "AnalyticsApi.dll"]
