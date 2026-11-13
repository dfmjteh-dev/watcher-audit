# syntax=docker/dockerfile:1
# Тулчейн сборки комплекта агентов (tools/agent-kit/build-kit.sh): .NET 8 SDK,
# nfpm (.deb/.rpm), wixl + msitools (MSI на Linux), osslsigncode (Authenticode).
# Образ без исходников — репозиторий монтируется в /src при запуске
# (install-server.sh), ключи — в /keys только на чтение, результат — в /kit.
# Ubuntu 24.04, не Debian 12 (sdk:8.0 по умолчанию): osslsigncode в Debian 12 —
# 2.5, подписывать PowerShell-скрипты (install.ps1) он не умеет. Та же ОС, что
# у раннера CI (installer/test-msi.sh).
FROM mcr.microsoft.com/dotnet/sdk:8.0-noble

# Та же версия и контрольная сумма, что в .github/workflows/release.yml.
ARG NFPM_VERSION=2.47.0
ARG NFPM_SHA256=0660ca602b2d2d2ae4781a06c692b3eeb9d437ffea05b831d76e41f4a3188783

RUN apt-get update \
    && apt-get install -y --no-install-recommends wixl msitools osslsigncode zip unzip openssl ca-certificates curl python3 \
    && rm -rf /var/lib/apt/lists/*
RUN curl -fsSLo /tmp/nfpm.tgz "https://github.com/goreleaser/nfpm/releases/download/v${NFPM_VERSION}/nfpm_${NFPM_VERSION}_Linux_x86_64.tar.gz" \
    && echo "${NFPM_SHA256}  /tmp/nfpm.tgz" | sha256sum -c - \
    && tar -xzf /tmp/nfpm.tgz -C /usr/local/bin nfpm \
    && rm /tmp/nfpm.tgz

ENV DOTNET=dotnet NFPM=nfpm DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
WORKDIR /src
