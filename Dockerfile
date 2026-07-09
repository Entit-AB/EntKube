FROM mcr.microsoft.com/dotnet/sdk:10.0.101 AS build
WORKDIR /src

# wasm-tools provides Microsoft.AspNetCore.App.Internal.Assets, which contains
# blazor.web.js, blazor.server.js, and the other Blazor framework JS files.
# Without this workload the publish output silently omits those files.
RUN dotnet workload install wasm-tools

COPY Directory.Build.props ./
COPY src/EntKube.Web/EntKube.Web.csproj src/EntKube.Web/
COPY src/EntKube.Web.Client/EntKube.Web.Client.csproj src/EntKube.Web.Client/
RUN dotnet restore src/EntKube.Web/EntKube.Web.csproj

COPY src/ src/
RUN dotnet publish src/EntKube.Web/EntKube.Web.csproj \
    -c Release \
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
    chmod +x /usr/local/bin/kubectl

# helm — latest stable via official installer script
RUN curl -fsSL https://raw.githubusercontent.com/helm/helm/main/scripts/get-helm-3 | bash

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
