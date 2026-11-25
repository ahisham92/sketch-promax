#!/usr/bin/env bash
# Refreshes lib/cdt-sdk/ (the compiled CDT SDK DLLs Sketch ProMax merges into SketchProMax.dll) from a checkout of
# the private CDT-SDK repository at the commit in cdt-sdk.ref. Only the DLLs are copied: the SDK's source
# never goes into this repository. Usage: scripts/update-cdt-sdk.sh /path/to/CDT-SDK
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
sdk="${1:?path to a CDT-SDK checkout}"
want="$(tr -d '[:space:]' < "$root/cdt-sdk.ref")"
have="$(git -C "$sdk" rev-parse HEAD)"
[ "$have" = "$want" ] || { echo "CDT-SDK is at $have; cdt-sdk.ref names $want" >&2; exit 1; }
for f in net48 net8.0-windows net10.0-windows; do
  dotnet build "$sdk/CDT.Wpf/CDT.Wpf.csproj" -c Release -f "$f" -p:EnableWindowsTargeting=true -v q -nologo
  mkdir -p "$root/lib/cdt-sdk/$f"
  cp "$sdk/CDT.Wpf/bin/Release/$f/CDT.Core.dll" "$sdk/CDT.Wpf/bin/Release/$f/CDT.Wpf.dll" "$root/lib/cdt-sdk/$f/"
done
echo "lib/cdt-sdk updated to CDT-SDK $want"
