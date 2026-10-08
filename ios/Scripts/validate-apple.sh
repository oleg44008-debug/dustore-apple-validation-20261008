#!/bin/bash
# Native validation only. Run on macOS with Xcode 16.4+ and XcodeGen installed.
set -euo pipefail
cd "$(dirname "$0")/.."
command -v xcodebuild >/dev/null
command -v xcodegen >/dev/null
command -v python3 >/dev/null
mkdir -p Validation
xcodebuild -version | tee Validation/xcode-version.txt
xcodegen generate

# CoreSimulator can need more than a minute on the first run of a fresh CI image.
python3 - <<'PY'
import json, os, subprocess, time
for attempt in range(3):
    try:
        result = subprocess.run(['xcrun','simctl','list','devices','available','-j'], check=True, capture_output=True, text=True, timeout=150)
        devices = json.loads(result.stdout)['devices']
        requested = os.environ.get('DUSTORE_SIMULATOR_UDID')
        candidates = [device for runtime, group in devices.items() if '.iOS-' in runtime for device in group if device.get('isAvailable', False)]
        chosen = next((d for d in candidates if d['udid'] == requested), None) if requested else next((d for d in candidates if d['name'].startswith('iPhone')), None)
        if not chosen: raise RuntimeError('No matching available iOS simulator')
        open('Validation/simulator.json','w').write(json.dumps(chosen, indent=2))
        open('Validation/simulator-udid.txt','w').write(chosen['udid'])
        if chosen['state'] != 'Booted': subprocess.run(['xcrun','simctl','boot',chosen['udid']], check=True, timeout=150)
        subprocess.run(['xcrun','simctl','bootstatus',chosen['udid'],'-b'], check=True, timeout=240)
        break
    except (subprocess.SubprocessError, RuntimeError) as error:
        if attempt == 2: raise
        print('CoreSimulator cold-start retry:', error)
        time.sleep(5)
PY
simulator=$(cat Validation/simulator-udid.txt)

# Both editions and both optimization levels are compiled against a real Apple SDK.
for edition in Free Prime; do
  for mode in Debug Release; do
    config="${edition}${mode}"
    xcodebuild -project DustoreX.xcodeproj -scheme "DustoreX-${edition}" -configuration "$config" \
      -destination "id=${simulator}" -derivedDataPath "Validation/Derived-${config}" \
      CODE_SIGNING_ALLOWED=NO build analyze 2>&1 | tee "Validation/${config}-build-analyze.log"
    if [[ "${DUSTORE_DEVICE_BUILD:-1}" == "1" ]]; then
      xcodebuild -project DustoreX.xcodeproj -scheme "DustoreX-${edition}" -configuration "$config" \
        -destination 'generic/platform=iOS' -derivedDataPath "Validation/Device-${config}" \
        CODE_SIGNING_ALLOWED=NO build 2>&1 | tee "Validation/${config}-device-build.log"
    fi
  done
  result="Validation/${edition}-Tests-$(date +%Y%m%dT%H%M%S).xcresult"
  xcodebuild -project DustoreX.xcodeproj -scheme "DustoreX-${edition}" -configuration "${edition}Debug" \
    -destination "id=${simulator}" -derivedDataPath "Validation/Derived-${edition}Debug" \
    -resultBundlePath "$result" -parallel-testing-enabled NO CODE_SIGNING_ALLOWED=NO test \
    2>&1 | tee "Validation/${edition}-tests.log"
done
printf '%s\n' 'Native build/analyze/unit/UI checks passed. Device apps are UNSIGNED, not installable distribution IPAs.'
