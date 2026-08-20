# Release process

Releases are produced only from annotated semantic-version tags such as
`v0.1.0`. The `Release` workflow runs on a GitHub-hosted Windows runner and
does the following:

The maintainer SSH signing key is published in `.github/allowed_signers`. Its
current fingerprint is `SHA256:o/GfOh3vUrpgNQIDlidVjIQvahcynffAHhOJEk5ALcQ`.
Treat a fingerprint change as a security-sensitive maintenance event.

1. Verifies the tag shape, requires an annotated tag object, and fails unless
   `git verify-tag` validates the SSH signature against the versioned
   `.github/allowed_signers` trust file. GitHub may additionally display the
   signature as `Verified` when the same public key is registered on the
   maintainer account.
2. Restores, builds, and tests the complete solution in Release mode.
3. Validates JavaScript and PowerShell syntax.
4. Publishes self-contained `win-x64` service and launcher binaries, verifies
   the package manifest, and creates a ZIP containing the package, installer,
   deinstaller, license, changelog, and README.
5. Writes SHA-256 manifests for the package and all top-level release assets.
6. Generates an SPDX 2.2 SBOM with Microsoft's pinned SBOM tool.
7. Creates GitHub/Sigstore provenance and SBOM attestations using GitHub's OIDC
   identity. These attestations are separate from the signed Git tag and from
   Windows Authenticode.
8. Uploads the ZIP, checksum files, and SBOM to the GitHub release for the tag.

## Authenticode boundary

This repository does not contain a code-signing certificate or private key.
The workflow intentionally does not create a self-signed certificate and does
not claim that the EXE files are Authenticode-signed. A production Windows
signing step must be added by the release owner with a protected certificate
and timestamping service. Until then, consumers should verify the GitHub
attestation, the mandatory signed Git tag, the SPDX SBOM, and the published
SHA-256 manifests.

Verify a downloaded release ZIP with GitHub CLI:

```powershell
gh attestation verify .\WorkRouter-v0.1.0-win-x64.zip --repo mateuszsury/workrouter-windows
Get-FileHash .\WorkRouter-v0.1.0-win-x64.zip -Algorithm SHA256
```

## Local package verification

After running `scripts/build-release.ps1`, verify the package without starting
the application:

```powershell
./scripts/verify-release.ps1 -PackagePath ./artifacts/publish -ValidateScripts
```

The verifier is read-only. It checks manifest syntax, path traversal, hashes,
required binaries, and (with `-ValidateScripts`) PowerShell parser errors.

## Workflow permissions

CI retains read-only repository permissions. CodeQL receives only
`security-events: write`. The release workflow receives `contents: write` to
upload release assets, plus `id-token: write` and `attestations: write` for the
artifact attestation. No cloud deployment or host/runtime operation is part of
the workflow.
