# syntax=docker/dockerfile:1
# TgMcp: MCP stdio server for a Telegram user account (TDLib).
# Build:  docker build -t tgmcp:dev .
# Tests:  docker build --target test .

ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:11.0.100-rc.3
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/runtime:11.0.0-rc.3

# The build always runs natively on the build machine; arm64 output is cross-published (managed code + the
# arm64 TDLib native package), so no emulation is needed except for the small runtime stage.
FROM --platform=$BUILDPLATFORM ${SDK_IMAGE} AS build
WORKDIR /src
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
# Restore first for layer caching.
COPY global.json Directory.Build.props Directory.Packages.props TgMcp.slnx .editorconfig tgmcp.example.jsonc ./
COPY src/TgMcp.Domain/TgMcp.Domain.csproj src/TgMcp.Domain/
COPY src/TgMcp.Application/TgMcp.Application.csproj src/TgMcp.Application/
COPY src/TgMcp.Infrastructure.Telegram/TgMcp.Infrastructure.Telegram.csproj src/TgMcp.Infrastructure.Telegram/
COPY src/TgMcp.Server/TgMcp.Server.csproj src/TgMcp.Server/
COPY tests/Directory.Build.props tests/
COPY tests/TgMcp.Domain.Tests/TgMcp.Domain.Tests.csproj tests/TgMcp.Domain.Tests/
COPY tests/TgMcp.Application.Tests/TgMcp.Application.Tests.csproj tests/TgMcp.Application.Tests/
COPY tests/TgMcp.Infrastructure.Tests/TgMcp.Infrastructure.Tests.csproj tests/TgMcp.Infrastructure.Tests/
RUN dotnet restore TgMcp.slnx
COPY src/ src/
COPY tests/ tests/

FROM build AS test
RUN dotnet test --solution TgMcp.slnx --no-restore

FROM build AS publish
ARG TARGETARCH=amd64
RUN RID=$([ "$TARGETARCH" = "arm64" ] && echo linux-arm64 || echo linux-x64) \
    && dotnet publish src/TgMcp.Server/TgMcp.Server.csproj -c Release --no-restore -r "$RID" -o /app \
    && rm -f /app/*.pdb /app/*.xml

FROM ${RUNTIME_IMAGE} AS runtime
LABEL org.opencontainers.image.title="tgmcp" \
      org.opencontainers.image.description="Policy-gated MCP server for a Telegram user account (TDLib)" \
      org.opencontainers.image.licenses="MIT"
ENV DOTNET_EnableDiagnostics=0 \
    DOTNET_gcServer=0 \
    DOTNET_TieredPGO=0 \
    TGMCP_DataDir=/data
WORKDIR /app
COPY --from=publish /app/ ./
# The runtime image ships libssl3, zlib and libstdc++, which libtdjson.so needs (checked with ldd).
RUN mkdir -p /data && chown app:app /data && chmod 0700 /data
USER app
VOLUME ["/data"]
ENTRYPOINT ["/app/tgmcp"]
CMD ["serve"]
