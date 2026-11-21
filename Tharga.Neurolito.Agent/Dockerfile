# ======================================================
# 1. Build & publish the .NET app
# ======================================================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
# Supplied by the pipeline as the build number (e.g. 0.0.47). Without it the assembly keeps the
# SDK default and every container reports itself as 1.0.0.0, which makes a deployed agent
# impossible to tie back to a build.
ARG BUILD_VERSION=1.0.0

WORKDIR /src

# Copy csproj files first (for proper restore caching)
COPY Tharga.Neurolito.Agent/*.csproj Tharga.Neurolito.Agent/
COPY Tharga.Neurolito.Client/*.csproj Tharga.Neurolito.Client/

RUN dotnet restore Tharga.Neurolito.Agent/Tharga.Neurolito.Agent.csproj

# Copy everything else
COPY . .

WORKDIR /src/Tharga.Neurolito.Agent

RUN dotnet publish \
    -c $BUILD_CONFIGURATION \
    -o /app/publish \
    -f net10.0 \
    /p:UseAppHost=false \
    /p:Version=$BUILD_VERSION


# ======================================================
# 2. Runtime: Ollama + ASP.NET Core
# ======================================================
FROM ollama/ollama:latest

# Install ASP.NET Core runtime
RUN apt-get update && \
    apt-get install -y curl ca-certificates && \
    curl -fsSL https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb \
        -o packages-microsoft-prod.deb && \
    dpkg -i packages-microsoft-prod.deb && \
    rm packages-microsoft-prod.deb && \
    apt-get update && \
    apt-get install -y aspnetcore-runtime-10.0 && \
    rm -rf /var/lib/apt/lists/*


WORKDIR /app

# Copy published app from build stage
COPY --from=build /app/publish .

# Copy startup script
COPY Tharga.Neurolito.Agent/start.sh /start.sh

# Fix Windows line endings + make executable (CRITICAL)
RUN sed -i 's/\r$//' /start.sh && chmod +x /start.sh

EXPOSE 5000
EXPOSE 11434

ENTRYPOINT ["/start.sh"]
