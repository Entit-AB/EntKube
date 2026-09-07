# The compile runs on BUILD_PLATFORM and cross-publishes for the image's architecture, so building a
# linux/arm64 image never requires running an arm64 compiler.
#
# It defaults to the builder's own platform, which is what CI wants: deploy.yml gives each architecture
# a native runner, so build and target already match and the --arch below is a no-op. It is an ARG
# because one common host cannot use its own platform. An Apple M-series Mac runs Linux containers under
# Hypervisor.framework, which advertises SME2 CPU features to the guest that it then cannot execute; the
# .NET toolchain detects them, emits them, and dies with SIGILL (exit 132) part-way through the compile.
# There is no in-container workaround — with hardware intrinsics disabled the crash simply moves from csc
# to the MSBuild worker node. scripts/release.sh pins this to linux/amd64 on such a host, where the very
# same compile runs under Rosetta and completes. See docs/releasing.md.
ARG BUILD_PLATFORM=$BUILDPLATFORM
FROM --platform=${BUILD_PLATFORM} mcr.microsoft.com/dotnet/sdk:10.0.101 AS build

# Docker names the architectures amd64/arm64, .NET names them x64/arm64, and the sed below is the whole
# of the disagreement between them. It is written out rather than done with bash's ${var/a/b} because
# RUN uses /bin/sh, which is dash here and would reject that as a bad substitution.
ARG TARGETARCH

WORKDIR /src

# wasm-tools provides Microsoft.AspNetCore.App.Internal.Assets, which contains
# blazor.web.js, blazor.server.js, and the other Blazor framework JS files.
# Without this workload the publish output silently omits those files.
RUN dotnet workload install wasm-tools

COPY Directory.Build.props ./
COPY src/EntKube.Web/EntKube.Web.csproj src/EntKube.Web/
COPY src/EntKube.Web.Client/EntKube.Web.Client.csproj src/EntKube.Web.Client/
RUN ARCH="$(echo "$TARGETARCH" | sed 's/^amd64$/x64/')" && \
    dotnet restore src/EntKube.Web/EntKube.Web.csproj -a "$ARCH"

COPY src/ src/
# --self-contained false: the runtime stage is an aspnet image, so the framework is already there.
RUN ARCH="$(echo "$TARGETARCH" | sed 's/^amd64$/x64/')" && \
    dotnet publish src/EntKube.Web/EntKube.Web.csproj \
    -c Release \
    -a "$ARCH" \
    --self-contained false \
    -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# libgit2sharp needs libssl/libcurl; git is used by GitOperationsService;
# kubectl and helm are invoked by KubernetesOperationsService/ComponentLifecycleService;
# clusterctl + ssh (openssh-client) are invoked by ClusterProvisioningService to stand up
# OpenStack clusters (Cluster API + CAPO, ephemeral-bootstrap + pivot).
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates \
    curl \
    libssl3 \
    libcurl4 \
    git \
    openssh-client \
    && rm -rf /var/lib/apt/lists/*

# kubectl — latest stable, architecture-aware
RUN ARCH=$(uname -m | sed 's/x86_64/amd64/;s/aarch64/arm64/') && \
    KUBECTL_VERSION=$(curl -fsSL https://dl.k8s.io/release/stable.txt) && \
    curl -fsSL "https://dl.k8s.io/release/${KUBECTL_VERSION}/bin/linux/${ARCH}/kubectl" \
         -o /usr/local/bin/kubectl && \
    chmod +x /usr/local/bin/kubectl && \
    kubectl version --client

# helm — architecture-aware, from the official release tarball.
#
# Pinned to the 3.x line, and deliberately NOT installed via `curl … | bash`: the exit status of
# a pipeline is the last command's, so a failed download left bash reading empty input, exiting 0,
# and the build succeeding with no helm in the image. That is exactly how a 429 from
# raw.githubusercontent.com shipped an image whose every Helm operation failed with
# "An error occurred trying to start process 'helm'". Chaining with && and finishing on
# `helm version` makes a broken download fail the build instead.
ARG HELM_VERSION=v3.21.4
RUN ARCH=$(uname -m | sed 's/x86_64/amd64/;s/aarch64/arm64/') && \
    curl -fsSL "https://get.helm.sh/helm-${HELM_VERSION}-linux-${ARCH}.tar.gz" -o /tmp/helm.tar.gz && \
    tar -xzf /tmp/helm.tar.gz -C /tmp && \
    install -m 0755 "/tmp/linux-${ARCH}/helm" /usr/local/bin/helm && \
    rm -rf /tmp/helm.tar.gz "/tmp/linux-${ARCH}" && \
    helm version

# clusterctl — Cluster API CLI, architecture-aware. Resolves the latest release tag so
# the download URL always points at a real asset (override with --build-arg CLUSTERCTL_VERSION).
ARG CLUSTERCTL_VERSION=
RUN ARCH=$(uname -m | sed 's/x86_64/amd64/;s/aarch64/arm64/') && \
    VERSION="${CLUSTERCTL_VERSION:-$(curl -fsSL https://api.github.com/repos/kubernetes-sigs/cluster-api/releases/latest | grep -oP '"tag_name":\s*"\K[^"]+')}" && \
    curl -fsSL "https://github.com/kubernetes-sigs/cluster-api/releases/download/${VERSION}/clusterctl-linux-${ARCH}" \
         -o /usr/local/bin/clusterctl && \
    chmod +x /usr/local/bin/clusterctl && \
    clusterctl version

# Run as non-root
RUN groupadd --system appgroup && useradd --system --gid appgroup --no-create-home appuser
RUN mkdir -p /app/Data && chown appuser:appgroup /app/Data

COPY --from=build /app/publish .
RUN chown -R appuser:appgroup /app

USER appuser

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

# SQLite database lives here — mount a persistent volume at /app/Data
VOLUME ["/app/Data"]

EXPOSE 8080

ENTRYPOINT ["dotnet", "EntKube.Web.dll"]
