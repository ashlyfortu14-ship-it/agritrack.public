FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY api/AgriTrack.Api.csproj api/
RUN dotnet restore api/AgriTrack.Api.csproj
COPY api/ api/
RUN dotnet publish api/AgriTrack.Api.csproj -c Release -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app/api
COPY --from=build /out ./
COPY index.html /app/index.html
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
CMD ["dotnet", "AgriTrack.Api.dll"]
