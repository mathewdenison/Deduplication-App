# Backend Dockerfile
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["NASdeduplication.csproj", "./"]
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .

# Install PowerShell Core and CIFS utils for Test Lab and SMB mounting
RUN apt-get update && apt-get install -y wget apt-transport-https software-properties-common cifs-utils \
    && wget -q https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb \
    && dpkg -i packages-microsoft-prod.deb \
    && apt-get update && apt-get install -y powershell \
    && rm packages-microsoft-prod.deb && rm -rf /var/lib/apt/lists/*

ENTRYPOINT ["dotnet", "NASdeduplication.dll"]
