# SCFA Content Center: current build and validation baseline

Updated: 2026-09-28  
Current client source version: `4.0.0-dev61`

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

- The publication gateway has been deployed previously and the administrator was able to configure corrected COS credentials.
- The owner reported a real map publication completed successfully from inside the software.
- Player install/sync safety has prior real/isolated validation for version protection, duplicate/conflict blocking, same-content skipping, backup, repair, and rollback.

These facts do not automatically prove the newer dev60/dev61 server operations are deployed.

## Still requires production acceptance

Do not mark the following complete until they are exercised against the current production gateway/account/COS configuration:

1. MOD publish → player install → repeat sync without duplicate download.
2. MOD new release → player update with backup and version verification.
3. Downlist through the publication gateway with a real administrator account.
4. Archive listing after downlist.
5. Restore a selected archived/downlisted version and verify the public manifest.
6. Player refresh/install/sync behavior after restore.
7. Failure/retry/rollback behavior during publication.
8. A real client update package through the configured update channel.

## Release discipline

- Keep credentials, tokens, private server data, real game packages, `artifacts/`, and local backups out of Git.
- A green build is necessary but does not replace production acceptance for COS/account/gateway behavior.
- Record only current validation state here. Use Git history for old per-dev evidence.
