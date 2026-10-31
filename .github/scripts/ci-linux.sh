#!/usr/bin/env bash

set -euo pipefail

distribution="${1:?usage: ci-linux.sh <arch|rhel>}"

case "${distribution}" in
  arch)
    pacman --sync --refresh --sysupgrade --noconfirm
    pacman --sync --needed --noconfirm \
      ca-certificates curl fontconfig freetype2 gzip icu krb5 libunwind \
      libx11 libxext libxrender libxcb openssl tar util-linux-libs zlib
    ;;
  rhel)
    # UBI ships curl-minimal, which provides curl and conflicts with the full curl package.
    dnf install --assumeyes \
      ca-certificates fontconfig freetype gzip glibc glibc-langpack-en \
      krb5-libs libgcc libicu libstdc++ libX11 libXext libXrender libxcb \
      openssl-libs tar tzdata zlib
    dnf clean all
    ;;
  *)
    echo "Unsupported distribution: ${distribution}" >&2
    exit 2
    ;;
esac

curl --fail --location --silent --show-error \
  https://dot.net/v1/dotnet-install.sh \
  --output /tmp/dotnet-install.sh
bash /tmp/dotnet-install.sh \
  --channel 10.0 \
  --quality GA \
  --install-dir /opt/dotnet

export DOTNET_ROOT=/opt/dotnet
export PATH="${DOTNET_ROOT}:${PATH}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export NUGET_XMLDOC_MODE=skip

dotnet --info
dotnet restore Poseidon.slnx
dotnet build Core.Application.proj \
  --configuration Release \
  --no-restore
test_results_directory="artifacts/test-results/${distribution}"
dotnet test Poseidon.slnx \
  --configuration Release \
  --no-restore \
  --logger "trx;LogFilePrefix=${distribution}" \
  --results-directory "${test_results_directory}"

if [[ -z "$(find "${test_results_directory}" -type f -name '*.trx' -print -quit)" ]]; then
  echo "No TRX test results were produced in ${test_results_directory}." >&2
  exit 1
fi

publish_directory="/workspace/artifacts/publish/${distribution}"
dotnet publish System/Core/Core/Core.csproj \
  --configuration Release \
  --no-restore \
  --output "${publish_directory}"
dotnet build Core.Application.proj \
  --configuration Release \
  --no-restore \
  -p:ModuleDeploymentDirectory="${publish_directory}/modules"
