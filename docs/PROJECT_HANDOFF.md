# SCFA Content Center project handoff

Updated: 2026-09-30  
Current source baseline: `V4.0.0-dev61`  
Current production publication gateway: `gateway-v61`

This file records the **verified current project/production state and the next handoff point**. Old dev-by-dev history remains available in Git history and should not be treated as current requirements.

## Architecture

- `src/SCFA.ContentCenter/`: Windows .NET 8 WPF client.
- Tencent Cloud COS stores published map/MOD packages, manifests, and thumbnails.
- The account service is separate from this repository and provides authentication, identity, user management, and audit APIs.
- `server/SCFA.PublicationGateway/`: independent .NET 8 administrator publication gateway.
- The publication gateway verifies the signed-in administrator through the account service and performs privileged COS publication operations.
- Long-term COS write credentials must never be distributed to player clients.

## Current product state

### Player client

The main player workflow is implemented and regression-covered:

- account registration/login/session restore and offline mode;
- cloud map/MOD browsing, search, previews, and enlarged previews;
- local map/MOD scanning and real game-name/version detection;
- install/update/repair with package and content verification;
- one-click synchronization with cancel support;
- no automatic downgrade of newer local content;
- duplicate/conflicting local content protection;
- verified backup before destructive replacement or local deletion;
- rollback/history restore;
- per-account local “dislike” preference that makes automatic sync skip selected content.

The “dislike” preference is still local to each computer and is not synchronized through the account service.

### Administrator publication

The client and publication gateway currently implement:

- separate “发布地图” and “发布 MOD” administrator entries;
- local publication preparation and package validation;
- release version separated from the game-internal version;
- package/content SHA-256 checks;
- short-lived/object-scoped COS upload authorization;
- random staging paths;
- server-side package/manifest validation;
- manifest backup, commit, and post-write verification;
- super-admin COS credential rotation after live credential verification;
- safe downlisting without deleting the package/thumbnail objects;
- administrator publication archive reading;
- restoring an archived/downlisted version as the current published version.

A real map publication has already succeeded from inside the software. A complete real MOD publication → player install → repeat sync → update cycle is still pending.

## Verified production state — 2026-09-30

The production server was inspected before further deployment work. The following facts were verified:

- `scfa-publication.service` is active and runs `/opt/scfa-publication/SCFA.PublicationGateway`.
- The installed release marker is `gateway-v61`.
- The running process and on-disk gateway executable have the same SHA-256:
  `15f3bfa453f075ce29791806a89ab39299661243f2353cdba8f51c00838e7e50`.
- `/publication-healthz` returns HTTP 200.
- Anonymous access to the administrator archive API is rejected with HTTP 401.
- The production account service and updater service are active.
- No OOM event was found in the inspected seven-day kernel-log window.
- Disk capacity is healthy; no OS reinstall is required for the investigated deployment/download problem.
- `scfa-gateway-release-check.timer` is currently **disabled** and should remain disabled until a reviewed updater flow replaces the current production auto-update path.

### Production backup status

Before updater testing, a consistent production backup was created and verified. It includes:

- account data and audit data;
- publication history/tickets/COS credential ciphertext;
- the publication master-key environment file;
- SCFA service environment files;
- Nginx publication config and TLS material;
- systemd service/timer units;
- current API/updater/publication binaries;
- the pre-existing gateway rollback binary.

The backup was also copied off the server and checksum-verified. **Do not upload that backup, private keys, environment files, or credential material to this public repository.**

### Gateway updater v2 validation

A production-side v2 updater prototype currently exists as:

- `/opt/scfa-publication/ops/update-from-release-v2.sh`
- `/opt/scfa-publication/ops/check-gateway-release-v2.sh`

Important: these v2 scripts are **not yet the systemd production entry point and are not yet source-controlled in this repository**.

Validated behavior:

- `--report-only` correctly reported `CURRENT=gateway-v61` and `LATEST=gateway-v61`.
- Full Release package validation downloaded the real `41,115,979` byte gateway package.
- Release package SHA-256 verified as `81b55fef432e2a32a2a02f5eef75f1a1b77aa896ace8a0f089f0f2de4f1a79dd`.
- Four-part progress reporting worked and exposed a slower individual route instead of appearing frozen.
- A second `--verify-only` run reused all four cached parts without re-downloading the package.
- A same-version `gateway-v61` install rehearsal completed successfully:
  backup → staged switch → service restart → health/auth checks → runtime SHA-256 verification.
- The final independent acceptance check again returned service active, health 200, anonymous admin 401, and matching runtime/disk SHA-256.
- A deliberate forced-failure rollback drill was **not** performed on the live server.

The current systemd release-check service still points to the older production scripts. Do not enable the timer merely because the v2 sidecar test succeeded.

## Source issues found during the deployment audit

These are source-code tasks for Codex, not OS-reinstall tasks:

1. **Client update file-handle bug — P0 source fix complete on 2026-09-30**
   - `UpdateService.DownloadAsync` now flushes and disposes the exclusive output stream before SHA-256 and PE validation.
   - A Windows regression reproduced the original sharing violation before the fix and passed afterward.
   - Hash mismatch rejection and staging-file cleanup are covered. Acceptance with a real published client package remains pending.

2. **COS request timeout policy — P0 source fix complete on 2026-09-30**
   - `CosTransport` now applies linked per-operation deadlines: 45 seconds for metadata, 5 minutes for small objects, and 12 hours for large transfers.
   - The deadline covers response headers, error bodies, and streamed content; tests cover stalls, caller cancellation, large transfers, and safe HTTP 504 mapping.
   - The shared HTTP client intentionally has no competing global deadline. Production COS acceptance remains pending.

3. **Publication lock scope — P0/P1**
   - `PublicationCoordinator` serializes commit/unpublish/restore through one `SemaphoreSlim`, which protects manifest consistency.
   - Do **not** simply remove serialization.
   - Review whether long COS download/validation work can happen outside the final manifest-mutation critical section so unrelated administrator operations are not blocked longer than necessary.
   - Preserve the re-read/conflict check immediately before the manifest write.

4. **Health/version evidence — P1**
   - `/publication-healthz` currently proves liveness but does not expose the gateway release/commit.
   - Add a safe version/build identifier so future deployment validation can prove which binary is running without relying only on server-side marker files.

5. **Updater v2 source control — P0 before auto-update**
   - Convert the validated production-side v2 behavior into reviewed repository-managed deployment tooling.
   - Keep report-only, full-size validation, progress reporting, cache reuse, package SHA-256, staging, backup, atomic switch, runtime SHA verification, and verified rollback.
   - Only after source review/testing and production handoff should the systemd release-check service/timer be changed.

## Next action for Codex

Unless the owner gives a newer priority, start in this order:

1. Completed on 2026-09-30: Windows client updater file-handle/SHA-256 fix and regression coverage.
2. Completed on 2026-09-30: bounded COS request deadlines, cancellation behavior, and regression coverage.
3. Review/narrow publication lock scope while preserving manifest serialization/conflict protection.
4. Add safe gateway version/build information to the health response and tests.
5. Bring the validated updater-v2 behavior into the repository as reviewed deployment tooling; do **not** directly change the live systemd timer from Codex.
6. Run the full client + gateway validation gates.
7. Prepare a production deployment handoff per `DEPLOYMENT_POLICY.md`.
8. After those source tasks, continue the remaining V4.0 production acceptance: real MOD publish/install/repeat-sync/update, downlist/archive/restore with a real administrator, and real client-update-channel acceptance.

## Security and data-safety rules

- Never place COS `SecretId`/`SecretKey`, account tokens, real packages, backups, environment files, master keys, TLS private keys, or private server configuration in this public repository.
- Player clients read published content without receiving long-term COS write credentials.
- Automatic sync must not guess which duplicate local folder to overwrite.
- Package metadata, release/game versions, hashes, destination paths, and extracted content must be validated before replacement.
- Destructive local operations require a verified backup and must stay inside the configured Maps/Mods roots.
- Publication must re-check the live manifest before commit and verify the published manifest after commit.
- Production must prevent uncoordinated external manifest writes until a stronger cross-writer atomic strategy exists.

## Known gaps / do not claim complete

1. Full real MOD publication acceptance: publish, install on a player client, repeat sync, update, and rollback.
2. Real-admin downlist → archive → restore acceptance is still pending even though `gateway-v61` is deployed and healthy.
3. Cross-device synchronization for the per-account “dislike” preference.
4. Steam-vs-FAF semantic compatibility remains partly manual.
5. The client update channel still needs acceptance with a real published client package.
6. The forced-failure rollback path of the updater-v2 prototype has not been deliberately exercised on production.
7. The current account-service source/build process is not present in this repository; do not redesign the deployed account service based on guesses.

## Development rules

- Read `PRODUCT_REQUIREMENTS.md` before feature work.
- Use this handoff for verified current state and unknowns.
- Use `ROADMAP.md` for priority and `BUILD_VALIDATION.md` for the release gate.
- Use `ADMIN_PUBLISH_PLAN.md` and `ADMIN_PUBLISH_API.md` for publication architecture.
- Do not revive player submissions/review; they were removed from product scope in dev52.
- Prefer refactoring/reusing existing services and models over adding parallel replacements.
- When a milestone changes product/production state, update this file and `OPERATION_LOG.md`.

## Moving to another computer

1. Clone the repository and open `SCFA.ContentCenter.sln`.
2. Read `AGENTS.md`, `docs/00_START_HERE.md`, `PRODUCT_REQUIREMENTS.md`, `ROADMAP.md`, this file, `BUILD_VALIDATION.md`, and the latest `OPERATION_LOG.md` entries.
3. Restore private configuration, game content, source backups, and server/COS backups separately; they are intentionally not stored in GitHub.
4. Run the build and regression gates before changing behavior.
5. Re-check live service behavior before relying on old production observations.
