# syntax=docker/dockerfile:1

# -------------------------------------------------------------------- build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first so layer caching works while editing code.
COPY src/LabelBridge.Core/LabelBridge.Core.csproj src/LabelBridge.Core/
COPY src/LabelBridge.Relay/LabelBridge.Relay.csproj src/LabelBridge.Relay/
RUN dotnet restore src/LabelBridge.Relay/LabelBridge.Relay.csproj -r linux-x64

COPY src/LabelBridge.Core/ src/LabelBridge.Core/
COPY src/LabelBridge.Relay/ src/LabelBridge.Relay/

RUN dotnet publish src/LabelBridge.Relay/LabelBridge.Relay.csproj \
      -c Release -r linux-x64 --self-contained false \
      -p:PublishSingleFile=false -o /app

# -------------------------------------------------------------------- run
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime
WORKDIR /app

# Run unprivileged; /data is the only writable location.
RUN useradd --system --uid 10001 --home /app --shell /usr/sbin/nologin labelbridge \
 && mkdir -p /data \
 && chown -R labelbridge:labelbridge /app /data

COPY --from=build --chown=labelbridge:labelbridge /app/ ./

COPY --chown=labelbridge:labelbridge docker/entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh

ENV LB_LISTEN="http://+:8080/" \
    LB_DATA="/data" \
    LB_RETENTION_HOURS="168" \
    LB_MAX_MB="128" \
    LB_MAX_JOBS="2000" \
    DOTNET_gcServer="true" \
    DOTNET_TieredPGO="true"

VOLUME ["/data"]
EXPOSE 8080

USER labelbridge

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD ["dotnet", "/app/LabelBridge.Relay.dll", "--health-check", "http://127.0.0.1:8080/health"]

ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
CMD ["dotnet", "/app/LabelBridge.Relay.dll"]


