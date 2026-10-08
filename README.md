# DUSTORE Apple native validation

This public repository contains an allowlisted mirror for native macOS and iOS diagnostics of DUSTORE Free and Prime. It is authorized by the project owner and contains no external Git history, user profiles, real game libraries, credentials, signing keys, provisioning profiles, or original iOS binary.

The source owners are preparing the iteration 2 source freeze. The initial commit contains only the reproducible toolchain probe. Application source and validation scripts will be mirrored after their explicit freeze, with SHA-256 provenance.

Native matrix:

- macOS Free and Prime on ARM64 (`macos-15`) and Intel x64 (`macos-15-intel`).
- iOS Free and Prime on `macos-15`, explicitly selecting Xcode 16.4.

Reports and screenshots describe actual covered fixtures. A simulator build does not prove physical-device behavior. Unsigned iOS device build output is not a signed, installable IPA. Mac ad-hoc signing does not imply Developer ID notarization. Native launcher smoke tests do not claim arbitrary Wine game compatibility or game FPS.

Actions are official GitHub actions pinned to immutable commits in `ci/action-pins.json`. Workflows have read-only repository permissions, bounded timeouts, isolated fixture data, and failure-report artifact uploads. No IP license is added by this validation mirror.
