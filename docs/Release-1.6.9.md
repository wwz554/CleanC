# CleanC 1.6.9

## Changes

- Rotate the offline RSA-3072 public key to match the newly configured production Pages Secret. The online ECDSA signing key is unchanged.
- Update the application, installer, settings and licensing request versions to 1.6.9.
- Include the activation layout, cleanup safety, driver status and repair verification changes documented in `修复与验收-20260923.md`.
- Add an explicitly opt-in live activation test using the real client encryption and verification code. It only accepts synthetic deployment test licenses; do not supply customer licenses.

## Offline key

Public SPKI SHA-256: `586222b50ac33e80f30606f594529d5d4a086e4da1d356945f36568aa580d6ed`.

The private key belongs only in Cloudflare's production `OFFLINE_RSA_PRIVATE_KEY` Secret and a protected operator backup. Never put it in this repository or release archives.

Existing installations must upgrade before creating new offline activation requests. Existing online authorizations are not reset by this rotation. Previously accepted offline records keep their existing expiry and revocation semantics.

## Verification and limitations

Build and QR decoding checks are separate from real phone scanning and fresh-install acceptance. The existing offline-v2 16-character shared-secret protocol is retained for compatibility; rotating its RSA transport key does not turn it into a server-only digital-signature license.

The installer is not Authenticode-signed. Full Windows 10/11 install, upgrade and hardware repair acceptance still requires the corresponding machines and user/admin contexts. No real disk cleanup or driver/system repair is run as part of this release verification.

The existing `updates/latest.json` continues to describe the last public GitHub release until a new release asset is actually published. Do not update its version or hash to point at a missing asset.
