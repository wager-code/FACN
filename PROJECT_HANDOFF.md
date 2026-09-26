# SCFA Content Center project handoff

Updated: 2026-09-26. This file is intentionally safe for the public source repository. It is a project record, not a copy of private server configuration.

## What the system does

- A Windows .NET 8 WPF client lets players discover, install, update, and submit SCFA maps and MODs.
- Tencent Cloud COS stores published game content. A separate account service handles player sign-in and related API calls.
- The client has a review screen for authorized administrators. A complete submission-to-publication workflow still requires server-side work.

## Confirmed state

- The client source in this repository builds and has regression checks. The latest source milestone is dev43.
- The cloud map/MOD lists now let each signed-in account mark content as disliked. One-click sync and cloud batch installation skip marked entries before modifying game files; manual single-item installation remains available. A second click restores automatic sync. The preference is stored locally per account and server endpoint, with a separate offline scope. It does not roam between computers because no account preference API has been verified or implemented on the server.
- Local map and MOD lists now have a per-row delete action. It targets the selected local game folder, creates a backup, and compares the source directory fingerprint with the backup before removal. A cloud item deleted locally can be installed again by a later full sync; this behavior is stated in the confirmation dialog.
- Account sign-in and several authenticated client functions were exercised successfully during development.
- The client can read the published COS catalog and has been checked against real local map and MOD directories.
- The sync center avoids repeating an installation when the local and cloud versions match but the catalog has no content fingerprint. When a fingerprint exists, it still checks the full installed directory and repairs a mismatch after backing up the local copy.
- Catalog entries with the same declared destination folder are rejected before installation. Package metadata, version, and content are verified before replacing a local copy; the installer checks the local version again immediately before replacement.
- If several local folders match one catalog item, sync can recognize an already installed copy only when its version and complete content fingerprint match the catalog. It leaves every local folder intact and reports the duplicate. Without that proof it stops the automatic action.
- A read-only catalog audit on 2026-09-25 found two legacy map records with inconsistent version metadata: `6_fields_of_isis` is labeled `13` and `xxx_survival` is labeled `2026.08.15`, while both published ZIPs contain scenario version `3`. The ZIP file hashes match their catalog records, and their extracted content matches the corresponding audited local folders. This is a catalog metadata issue, not a damaged download. The client must keep blocking a version-mismatched replacement.
- The client now accepts an optional `game_version` field for catalog items. It compares and validates the version inside map/MOD files against `game_version`, while keeping `version` as the visible release label. Older catalog items still use `version` for both meanings.
- On 2026-09-25, the corrected `scfa/manifest/latest.json` was published to COS. A fresh public download matched SHA256 `630AB534C5E8064EB32B7841A61F303C234B10E987FBBB00DF821BE5ABB58194` and contained all 18 maps. The two corrected records retain their release labels and now include `game_version: "3"` plus verified `content_sha256` values: `6_fields_of_isis` = `7ced89b270736347849029408f8917d55f4fea0177925e28b25298ba4ed98926`; `xxx_survival` = `774a02a00ab4fd6196a582968e596c7175b86cc2415ee275d94874babd331dbb`. The prior catalog is saved under the local `_源码备份` directory.
- On 2026-09-25, an isolated live-COS sync installed the two corrected map ZIPs and one small MOD ZIP, then skipped all three on a repeat run without a new backup. An injected same-version map edit was backed up and repaired; an injected newer map version was preserved. No game directory was used for this test.
- A read-only scan of `E:\SCFA\maps` and `E:\SCFA\mods` found 80 valid local maps and 7 valid local MODs. All 17 unambiguous published maps and all 6 published MODs matched their catalog directory fingerprints. The remaining map, Saltrock Colony, has multiple local copies; one copy matched the published version and content fingerprint exactly, so sync would skip while preserving all copies. No E: game content was changed.
- On 2026-09-25 at 10:58:55 CST, the signed-in packaged client `V4.0.0-dev41` ran the full sync in its Sync Center. The UI reported 0 installed/updated, 24 skipped, and 0 failed. This matches the read-only E: audit: all 18 published maps and 6 published MODs were left in place.
- A read-only production check on 2026-09-25 found account service version 8 healthy, ordinary submission POST registered, and the signed-in administrator able to read their own submission list, user list, and audit records. The same administrator has `review.read` and `review.approve`, but `GET /v1/admin/submissions` returns 404. Seven alternate read paths did not expose a review queue. Review and publication are therefore not end-to-end verified.
- The owner supplied `SCFA账号API_V1_腾讯云部署包.zip` on 2026-09-25. Its SHA-256 is `A3045ED21D25460DD4F9F847F4B24D86330DCFA770A46AFDA08AC4B5B0BF33DE`. The archive has one Linux executable plus a README, systemd unit, Nginx example, and environment example. It has no Go source or `go.mod`. Its README identifies the earlier `V3.0.0-dev17` account API stage and lists only registration, login, logout, identity, and read-only admin users. A binary string check found no submission routes in this archive. A private copy is stored under `_源码备份/server_package_20260925`.
- The current repository does not contain the account-service source. This old deployment binary must not replace the newer production service; doing so could remove the existing submission and administration features.
- The owner cannot locate the current server source and clarified the intended review policy: submissions target the Steam release of Forged Alliance, must be checked for FAF-only dependencies, and require an administrator's final decision. The former third-party review site remains unidentified. See `SUBMISSION_REVIEW_PLAN.md` for the proposed rebuild and the distinction between malware scanning and game compatibility checks.
- On 2026-09-26, the owner supplied a download of the current Linux `scfa-api` binary (SHA-256 `61038BBB5A6E79792C54F4ABD29C5162D93836F1D1431F6AFEDDD72D69A5C5F8`), saved privately under `_源码备份/server_binary_20260925`. Offline inspection found `POST /v1/submissions`, `GET /v1/submissions/mine`, `DELETE /v1/submissions/{id}`, and `GET /v1/admin/submission-storage`, but no `/v1/admin/submissions` or `/review` route. The binary contains COS object-path literals `/submissions/index.json` and `/submissions/files/`; their actual data format and access policy still need confirmation. A signed-in read-only request to the storage-status endpoint returned `ready: true` and showed that bucket, region, and root match the client's COS configuration without disclosing their values.
- A 2026-09-26 screenshot of the configured COS root showed `launcher/`, `manifest/`, `maps/`, `mods/`, and `thumbnails/`, with no visible `submissions/` prefix. The complete read-only account audit response contained 178 records at the time of the check; the only `submission.*` event was one `submission.storage.configure`, and there were no review or publish events. This is evidence that no player submission was recorded in the current audit; it is not a substitute for a protected server/COS backup before routing changes.

## Unknowns: do not guess

- The owner recalls a third-party site or service for reviewing player submissions. Its name, role, and integration details have not been recovered. Do not claim a specific vendor was used.
- The source and exact build process for the deployed account-service executable are not yet available. Local source and GitHub inventory found no backend source. The supplied archive was inspected and contains only an old executable and deployment examples. The current binary was obtained and audited offline, but its source, actual COS submission-index schema, and private server configuration have not been obtained or verified.
- No production review or publication action has been validated. Do not treat a client screen or a permission name as proof of a working server workflow.

## Recommended next milestone

The map/MOD sync milestone is verified with the signed-in client and current COS catalog. For future content releases, keep `version`, optional `game_version`, ZIP SHA-256, and full directory fingerprint consistent, then verify a small installation and repeat sync before general publication. A catalog item without a content fingerprint cannot prove that an edited same-version local folder matches the cloud package; automatic sync deliberately leaves that folder alone.

Then preserve the existing account service and published COS objects while implementing a testable submission and review service. The intended flow is: authenticated submission, isolated pending content, package validation, authorized human decision, publication, and recorded history. Confirm the integration against a test deployment before changing production routing or published content.

The current backend source could not be found. Before routing changes, make a protected copy of `/var/lib/scfa-api`, the deployed binary, and effective service/Nginx configuration. Keep `/etc/scfa-api.env`, account data, tokens, and COS credentials out of this public repository. Build and test a separate review service that preserves the existing account API and checks for any COS submission records before cutover; never deploy the old archive over production. Before any production switch, verify login, existing account data, submission listing, review decisions, COS publication, rollback, and a fresh client sync in a test environment.

## Moving to another computer

1. Clone `https://github.com/wager-code/FACN` into the location chosen for the new computer, and open that checkout as a local Codex project.
2. Ask Codex to read `AGENTS.md` and this handoff file, then inspect the current code and tests before changing anything.
3. Install a compatible Windows .NET SDK and run the solution build and relevant regression checks.
4. Restore local game files, private configuration, source backups, and any server or COS backups separately. They are not part of this public repository.
5. Re-check live service behavior before relying on old observations. Record new evidence and decisions here after each major milestone.

GitHub holds the portable client source and safe project record. Private credentials and server data require separate, protected backups. Chat history and local AI memory are useful context but are not the project record of truth.
