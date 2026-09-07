# The in-cluster telemetry node — entit.azurecr.io/entkube-telemetry.
#
# One image, two roles (Node__Role=indexer|querier). Unlike the management-plane image this needs no
# kubectl, helm, clusterctl or git: it never talks to the Kubernetes API, it only receives OTLP, indexes
# it, and answers queries. Keeping it that small also keeps its attack surface small, which matters
# because this is the one component that holds raw log bodies.
#
# Build from the repository root:
#   docker build -f src/EntKube.TelemetryNode/Dockerfile -t entit.azurecr.io/entkube-telemetry:<tag> .

# Compiles on BUILD_PLATFORM and cross-publishes for the image's architecture, so a linux/arm64 image
# never needs an arm64 compiler. Defaults to the builder's own platform — CI builds each architecture on
# a runner of that architecture, where the --arch below is a no-op. scripts/release.sh overrides it on an
# Apple M-series host, whose hypervisor makes the arm64 .NET toolchain die with SIGILL; the identical
# explanation is on the management-plane Dockerfile, and the detail is in docs/releasing.md.
ARG BUILD_PLATFORM=$BUILDPLATFORM
FROM --platform=${BUILD_PLATFORM} mcr.microsoft.com/dotnet/sdk:10.0.101 AS build

# Docker says amd64/arm64, .NET says x64/arm64. RUN uses dash, so the translation is a sed rather than
# bash's ${var/a/b}.
ARG TARGETARCH

WORKDIR /src

COPY Directory.Build.props ./
COPY src/EntKube.Telemetry/EntKube.Telemetry.csproj src/EntKube.Telemetry/
COPY src/EntKube.TelemetryNode/EntKube.TelemetryNode.csproj src/EntKube.TelemetryNode/
RUN ARCH="$(echo "$TARGETARCH" | sed 's/^amd64$/x64/')" && \
    dotnet restore src/EntKube.TelemetryNode/EntKube.TelemetryNode.csproj -a "$ARCH"

COPY src/EntKube.Telemetry/ src/EntKube.Telemetry/
COPY src/EntKube.TelemetryNode/ src/EntKube.TelemetryNode/
# --self-contained false: the runtime stage is an aspnet image, so the framework is already there.
RUN ARCH="$(echo "$TARGETARCH" | sed 's/^amd64$/x64/')" && \
    dotnet publish src/EntKube.TelemetryNode/EntKube.TelemetryNode.csproj \
    -c Release \
    -a "$ARCH" \
    --self-contained false \
    -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN groupadd --system appgroup && useradd --system --gid appgroup --no-create-home appuser

# The hot active index, the warm sealed segments and the SQLite catalog all live here. Mount a
# PersistentVolume: an indexer with an emptyDir loses everything not yet sealed to object storage on
# every restart, and re-downloads its whole warm tier afterwards.
RUN mkdir -p /data && chown appuser:appgroup /data
VOLUME ["/data"]

COPY --from=build /app/publish .
RUN chown -R appuser:appgroup /app

USER appuser

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
ENV Telemetry__DataPath=/data/telemetry

EXPOSE 8080

ENTRYPOINT ["dotnet", "entkube-telemetry.dll"]
