# SCFA Content Center project handoff

Updated: 2026-09-25. This file is intentionally safe for the public source repository. It is a project record, not a copy of private server configuration.

## What the system does

- A Windows .NET 8 WPF client lets players discover, install, update, and submit SCFA maps and MODs.
- Tencent Cloud COS stores published game content. A separate account service handles player sign-in and related API calls.
- The client has a review screen for authorized administrators. A complete submission-to-publication workflow still requires server-side work.

## Confirmed state

- The client source in this repository builds and has regression checks. The latest source milestone is dev41.
- Account sign-in and several authenticated client functions were exercised successfully during development.
- The client can read the published COS catalog and has been checked against real local map and MOD directories.
- The sync center avoids repeating an installation when the local and cloud versions match but the catalog has no content fingerprint. When a fingerprint exists, it still checks the full installed directory and repairs a mismatch after backing up the local copy.
- Catalog entries with the same declared destination folder are rejected before installation. Package metadata, version, and content are verified before replacing a local copy; the installer checks the local version again immediately before replacement.
- If several local folders match one catalog item, sync can recognize an already installed copy only when its version and complete content fingerprint match the catalog. It leaves every local folder intact and reports the duplicate. Without that proof it stops the automatic action.
- A read-only catalog audit on 2026-09-25 found two legacy map records with inconsistent version metadata: `6_fields_of_isis` is labeled `13` and `xxx_survival` is labeled `2026.08.15`, while both published ZIPs contain scenario version `3`. The ZIP file hashes match their catalog records, and their extracted content matches the corresponding audited local folders. This is a catalog metadata issue, not a damaged download. The client must keep blocking a version-mismatched replacement.
- The client now accepts an optional `game_version` field for catalog items. It compares and validates the version inside map/MOD files against `game_version`, while keeping `version` as the visible release label. Older catalog items still use `version` for both meanings.
- On 2026-09-25, the corrected `scfa/manifest/latest.json` was published to COS. A fresh public download matched SHA256 `630AB534C5E8064EB32B7841A61F303C234B10E987FBBB00DF821BE5ABB58194` and contained all 18 maps. The two corrected records retain their release labels and now include `game_version: "3"` plus verified `content_sha256` values: `6_fields_of_isis` = `7ced89b270736347849029408f8917d55f4fea0177925e28b25298ba4ed98926`; `xxx_survival` = `774a02a00ab4fd6196a582968e596c7175b86cc2415ee275d94874babd331dbb`. The prior catalog is saved under the local `_源码备份` directory.
- The deployed service does not currently fulfill the client's review-list request. Review and publication are therefore not end-to-end verified.
- The current repository does not contain the account-service source.

## Unknowns: do not guess

- The owner recalls a third-party site or service for reviewing player submissions. Its name, role, and integration details have not been recovered. Do not claim a specific vendor was used.
- The source and exact build process for the deployed account-service executable are not yet available.
- No production review or publication action has been validated. Do not treat a client screen or a permission name as proof of a working server workflow.

## Recommended next milestone

Run a supervised sync with a small test game library using the published catalog. Confirm that matching maps and MODs are skipped, edited same-version files are backed up before repair, and ambiguous duplicate local folders are never overwritten automatically. A catalog item without a content fingerprint cannot prove that an edited same-version local folder matches the cloud package; automatic sync deliberately leaves that folder alone.

Then preserve the existing account service and published COS objects while implementing a testable submission and review service. The intended flow is: authenticated submission, isolated pending content, package validation, authorized human decision, publication, and recorded history. Confirm the integration against a test deployment before changing production routing or published content.

## Moving to another computer

1. Clone `https://github.com/wager-code/FACN` into the location chosen for the new computer, and open that checkout as a local Codex project.
2. Ask Codex to read `AGENTS.md` and this handoff file, then inspect the current code and tests before changing anything.
3. Install a compatible Windows .NET SDK and run the solution build and relevant regression checks.
4. Restore local game files, private configuration, source backups, and any server or COS backups separately. They are not part of this public repository.
5. Re-check live service behavior before relying on old observations. Record new evidence and decisions here after each major milestone.

GitHub holds the portable client source and safe project record. Private credentials and server data require separate, protected backups. Chat history and local AI memory are useful context but are not the project record of truth.
