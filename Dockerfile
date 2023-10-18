FROM mcr.microsoft.com/dotnet/sdk:8.0.100-rc.2 AS build
WORKDIR /app
COPY . .
RUN dotnet publish -c Release

EXPOSE 5201

ENTRYPOINT ["dotnet", "PacketsPerSecond.dll"]
