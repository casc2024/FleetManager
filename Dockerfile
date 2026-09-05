# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY FleetManager/FleetManager.csproj FleetManager/
RUN dotnet restore FleetManager/FleetManager.csproj
COPY FleetManager/ FleetManager/
RUN dotnet publish FleetManager/FleetManager.csproj -c Release -o /app/publish /p:UseAppHost=false

# ---------- runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_ENVIRONMENT=Production
# Railway inyecta PORT; Program.cs lo usa para escuchar en 0.0.0.0:$PORT
EXPOSE 8080
ENTRYPOINT ["dotnet", "FleetManager.dll"]
