# SCFA Content Center project handoff

Updated: 2026-09-28  
Current source baseline: `V4.0.0-dev61`

This file records only the current project state. Old dev-by-dev history remains available in Git history and should not be treated as current requirements.

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

The owner reported that a real map was successfully published from the software. A complete real MOD publication → player install → repeat sync cycle is still pending. The dev60 downlisting flow and dev61 archive/restore flow also still require production deployment/real-account acceptance testing.

## Security and data-safety rules

- Never place COS `SecretId`/`SecretKey`, account tokens, real packages, or private server configuration in this public repository.
- Player clients read published content without receiving long-term COS write credentials.
- Automatic sync must not guess which duplicate local folder to overwrite.
- Package metadata, release/game versions, hashes, destination paths, and extracted content must be validated before replacement.
- Destructive local operations require a verified backup and must stay inside the configured Maps/Mods roots.
- Publication must re-check the live manifest before commit and verify the published manifest after commit.
- Production should keep the publication gateway single-writer or otherwise prevent uncoordinated external manifest writes until atomic cross-writer protection is implemented.

## Known gaps / do not claim complete

1. Full real MOD publication acceptance: publish, install on a player client, repeat sync, update, and rollback.
2. Deploy/upgrade the production gateway to the dev61 archive/restore implementation and verify downlist/restore with a real administrator account.
3. Cross-device synchronization for the per-account “dislike” preference.
4. Steam-vs-FAF semantic compatibility remains partly manual; current checks cover structure, paths, versions, hashes, and package safety, not every script/API semantic difference.
5. The client update channel still needs acceptance with a real published client package.
6. The current account-service source/build process is not present in this repository; do not replace or redesign the deployed account service based on guesses.

## Development rules

- Read `PRODUCT_REQUIREMENTS.md` before feature work.
- Use this handoff for verified current state and unknowns.
- Use `BUILD_VALIDATION.md` for the current test/release gate.
- Use `ADMIN_PUBLISH_PLAN.md` and `ADMIN_PUBLISH_API.md` for publication architecture.
- Do not revive player submissions/review; they were removed from product scope in dev52.
- Prefer refactoring/reusing existing services and models over adding parallel replacements.
- When a milestone changes product state, update the requirements/handoff instead of appending another large chronological history.

## Moving to another computer

1. Clone the repository and open `SCFA.ContentCenter.sln`.
2. Read `AGENTS.md`, `PRODUCT_REQUIREMENTS.md`, this file, and `BUILD_VALIDATION.md`.
3. Restore private configuration, game content, source backups, and server/COS backups separately; they are intentionally not stored in GitHub.
4. Run the build and regression gates before changing behavior.
5. Re-check live service behavior before relying on an old production observation.
