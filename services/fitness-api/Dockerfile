FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 80
# Install curl for health checks
RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["FitnessApi.csproj", "./"]
RUN dotnet restore "FitnessApi.csproj"
COPY . .
RUN dotnet build "FitnessApi.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "FitnessApi.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "FitnessApi.dll"]
