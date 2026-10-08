#!/bin/bash
# Packaging does not sign an application. A successful archive is never inferred from ZIP creation.
set -euo pipefail
cd "$(dirname "$0")/.."
edition="${1:-Prime}"
[[ "$edition" == Free || "$edition" == Prime ]]
app="Validation/Device-${edition}Release/Build/Products/${edition}Release-iphoneos/DustoreX.app"
[[ -d "$app" && -f "$app/Info.plist" && -f "$app/DustoreX" ]]
output="Validation/Packages/${edition}"
mkdir -p "$output/Payload"
ditto "$app" "$output/Payload/DustoreX.app"
ditto -c -k --keepParent "$output/Payload" "$output/DustoreX-${edition}-1.2.0-UNSIGNED.ipa"
shasum -a 256 "$output/DustoreX-${edition}-1.2.0-UNSIGNED.ipa" > "$output/SHA256.txt"
printf '%s\n' 'UNSIGNED native device build; signing/provisioning is required before device installation.' > "$output/UNSIGNED.txt"
