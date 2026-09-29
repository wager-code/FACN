# SCFA Content Center: current build and validation baseline

Updated: 2026-09-30  
Current client source version: `4.0.0-dev61`  
Current production gateway: `gateway-v61`

This file is a current validation checklist, not a chronological development diary. Older validation history remains in Git history.

## Current automated baseline

The latest dev61 main commit before repository cleanup was:

- `6442dc037de268163273c1cdf1fe3c22ea277853`
- message: `Smoke test administrator archive page rendering`

Observed GitHub Actions results for that commit:

- `build-windows`: **success**
- `release-publication-gateway`: **success**
- gateway release: `gateway-v61`
- release assets include the Linux x64 publication gateway archive and SHA-256 file.

The project file reports:

- assembly/file version: `4.0.0.61`
- informational version: `4.0.0-dev61`

## Client verification gate

Before merging a functional client change, run:

```powershell
dotnet restore SCFA.ContentCenter.sln
dotnet build SCFA.ContentCenter.sln -c Release --no-restore
.\run-regression-tests.ps1
```

Relevant WPF smoke checks include:

```powershell
dotnet run --project tests/SCFA.ContentCenter.RegressionTests/SCFA.ContentCenter.RegressionTests.csproj -c Release --no-build -- --profile-ui-smoke
dotnet run --project tests/SCFA.ContentCenter.RegressionTests/SCFA.ContentCenter.RegressionTests.csproj -c Release --no-build -- --archive-ui-smoke
```

The archive UI smoke check constructs and lays out the dev61 administrator archive page in an STA WPF thread. Keep it in Windows CI.

## Publication gateway verification gate

Before releasing/deploying the gateway:

```powershell
dotnet run --project server/SCFA.PublicationGateway.RegressionTests/SCFA.PublicationGateway.RegressionTests.csproj -c Release
```

The gateway regression suite should continue covering, at minimum:

- credential encryption/storage behavior;
- administrator authentication/role enforcement;
- COS credential verification before rotation;
- signed upload URL and staging-key constraints;
- map/MOD package validation;
- manifest conflict/version validation;
- publication commit verification;
- downlisting behavior;
- archive parsing;
- restore behavior.

## Production evidence already established

- The production publication gateway was inspected on 2026-09-30 and verified as `gateway-v61`.
- The running process and on-disk gateway executable matched SHA-256 `15f3bfa453f075ce29791806a89ab39299661243f2353cdba8f51c00838e7e50`.
- `/publication-healthz` returned HTTP 200 and anonymous archive access returned HTTP 401.
- A complete production backup was created and an off-host copy was checksum-verified before updater testing.
- The real gateway Release package size was verified as 41,115,979 bytes and its release SHA-256 passed.
- A production-side updater-v2 prototype completed:
  - full-package download with visible progress;
  - four-part cache reuse;
  - package SHA-256 verification;
  - same-version `gateway-v61` staged install;
  - executable backup;
  - service restart;
  - health/auth verification;
  - runtime-vs-disk SHA-256 verification.
- The deliberate forced-failure rollback path has **not** been exercised on production.
- The production release-check timer remains **disabled** and the systemd service still uses the older updater entry point.
- The owner reported a real map publication completed successfully from inside the software.
- Player install/sync safety has prior real/isolated validation for version protection, duplicate/conflict blocking, same-content skipping, backup, repair, and rollback.

## Source fixes required before final V4.0 acceptance

Before treating the client-update and gateway reliability work as complete, add regression coverage and fix:

1. `UpdateService.DownloadAsync`: dispose/close the `FileShare.None` output stream before reopening the downloaded file for SHA-256.
2. `CosTransport`: replace infinite request lifetime with an explicit bounded timeout/cancellation strategy that still supports large package transfers.
3. `PublicationCoordinator`: review/narrow the shared manifest critical section while preserving final mutation serialization and conflict checks.
4. Gateway health output: expose a safe version/build identifier.
5. Updater v2: move the validated behavior into source-controlled tooling and test rollback behavior before production auto-update is enabled.

## Still requires production acceptance

Do not mark the following complete until they are exercised against the current production gateway/account/COS configuration:

1. MOD publish → player install → repeat sync without duplicate download.
2. MOD new release → player update with backup and version verification.
3. Downlist through the publication gateway with a real administrator account.
4. Archive listing after downlist.
5. Restore a selected archived/downlisted version and verify the public manifest.
6. Player refresh/install/sync behavior after restore.
7. Failure/retry/rollback behavior during publication.
8. A real client update package through the configured update channel after the client updater file-handle fix.
9. Production updater-v2 rollback acceptance and only then a decision on enabling the release-check timer.

## Release discipline

- Keep credentials, tokens, private server data, real game packages, `artifacts/`, and local backups out of Git.
- A green build is necessary but does not replace production acceptance for COS/account/gateway behavior.
- Record only current validation state here. Use Git history for old per-dev evidence.
