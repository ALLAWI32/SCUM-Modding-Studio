#!/usr/bin/env bash
# Tests ScumStudio.sln on Linux/macOS. Extra arguments are passed to "dotnet test", e.g.
#   scripts/test.sh -c Release
#   scripts/test.sh -p:ArtifactsPath=/tmp/my-artifacts --filter "FullyQualifiedName~Core"
# SDK selection: $DOTNET if set, else the Microsoft SDK in /opt/dotnet-ms when present, else "dotnet" on PATH.
# global.json pins the .NET 8 SDK band (8.0.100, rollForward latestFeature).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# Run from the repo root so global.json selects the SDK.
cd "$ROOT"

if [[ -z "${DOTNET:-}" ]]; then
  if [[ -x /opt/dotnet-ms/dotnet ]]; then
    export DOTNET_ROOT=/opt/dotnet-ms
    DOTNET=/opt/dotnet-ms/dotnet
  else
    DOTNET=dotnet
  fi
fi

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

echo "Using $("$DOTNET" --version) ($DOTNET)" >&2
exec "$DOTNET" test "$ROOT/ScumStudio.sln" "$@"
