# Native validation, iOS 16 and later

Use macOS with Xcode 16.4 or later and XcodeGen. The deployment target stays at iOS 16.0; building guarded `WKWebView.isInspectable` requires a modern SDK. No certificate, provisioning profile or account secret is stored in this project.

```sh
brew install xcodegen
bash Scripts/validate-apple.sh
```

The script generates the project, compiles and analyzes `FreeDebug`, `FreeRelease`, `PrimeDebug` and `PrimeRelease` for a real simulator SDK, builds all four for the device SDK without signing, and runs both schemes' XCTest and XCUITest suites. It preserves Xcode version, logs, simulator identity and `.xcresult` attachments under `Validation/`. Set `DUSTORE_SIMULATOR_UDID` to an existing iOS/iPadOS simulator UDID to test an iPad too. Fresh CI hosts get bounded CoreSimulator cold-start retries. Set `DUSTORE_DEVICE_BUILD=0` to skip device builds.

For independent CI matrix jobs set `DUSTORE_CI_EDITION=Free` or `Prime`; the default checks both. Portable `node Scripts/test-input-bridge.cjs` checks keyboard adapter transitions against a mocked DOM and is explicitly separate from the native WebKit XCTest.

The unit suite covers old library decoding, malformed ZIP bounds, cooperative cancellation, source preservation, actual web-folder import, motion policy and keyboard event press/release inside native WebKit. UI tests use `-dustoreUITest -dustoreFixture empty|library|transfer|player -dustoreOfflineStore` in debug builds only. They create small, explicitly owned files in the app's own test sandbox. Their web page is a diagnostic fixture, not a production game or evidence that an unrelated game works.

UI checks cover library search/favorites, empty-state actions, cancellation and dismissal, store offline recovery, direct eX actions, the real WKWebView player, safe close, and large accessibility text. Named screenshots remain inside `.xcresult`. Run the suites on iPhone and iPad, portrait and landscape. Real VoiceOver, Switch Control, physical keyboard/gamepad, simultaneous finger input, backgrounding, actual Godot builds and Instruments performance still require separate device checks. A simulator test does not establish these results.

After a successful native device Release build:

```sh
bash Scripts/package-unsigned-device.sh Free
bash Scripts/package-unsigned-device.sh Prime
```

These packages are clearly marked `UNSIGNED`; they need valid signing and provisioning before installation. They do not replace the supplied original IPA. Native success must be read from Xcode logs/results, not from syntax parsing or package creation.
