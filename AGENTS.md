# SCFA Content Center: agent guidance

This repository contains the Windows .NET 8 WPF client and the independent publication gateway for SCFA Content Center.

## Mandatory startup

Before changing source code, read:

1. `docs/00_START_HERE.md`
2. `docs/PRODUCT_REQUIREMENTS.md`
3. `docs/ROADMAP.md`
4. `docs/CODEBASE_GUIDE.md`
5. `docs/PROJECT_HANDOFF.md`
6. `docs/BUILD_VALIDATION.md`
7. `docs/OPERATION_LOG.md` — recent important work, failures, incomplete items, and handoffs

For administrator publication work, also read `docs/ADMIN_PUBLISH_PLAN.md` and `docs/ADMIN_PUBLISH_API.md`.

For any server/deployment task, read `docs/DEPLOYMENT_POLICY.md` and `docs/TROUBLESHOOTING.md`.

`README.md` is user-facing and is not the authoritative implementation status.

## Operation log requirement

After a significant development, refactor, deployment preparation, production incident, rollback, release, or project-structure change, update `docs/OPERATION_LOG.md`.

Each important entry must include:

- date/time;
- actor;
- target/version;
- what was done;
- result/status;
- what completed;
- what remains incomplete or unverified;
- errors/abnormalities;
- next step;
- related commit/PR/release when available.

Do not create one entry for every tiny edit. Group one coherent task into one useful record.

Never place passwords, tokens, SecretId/SecretKey, master keys, private keys, or other secret values in the operation log.

## Production-server boundary

Codex owns source-code work: development, refactoring, tests, local build, CI, release preparation, deployment notes, and handoff material.

Do not directly perform production-server mutations when work reaches SSH, systemd, Nginx, firewall/security-group ports, TLS certificates, production environment variables/secrets, Tencent COS production permissions/credentials, production data migration/deletion, or a live Publication Gateway upgrade.

At that boundary:

1. stop production mutations;
2. prepare the handoff template from `docs/DEPLOYMENT_POLICY.md`;
3. tell the user to send the handoff and actual server logs/status to the ChatGPT server-deployment conversation;
4. continue owning source-code fixes if deployment diagnosis later identifies a code defect.

Do not workaround 403/404/502 errors by repeatedly changing product code before identifying the failing layer.

## Repository layout

- `src/SCFA.ContentCenter/` — Windows .NET 8 WPF client.
- `server/SCFA.PublicationGateway/` — independent administrator publication gateway.
- `server/SCFA.PublicationGateway.RegressionTests/` — gateway regression coverage.
- `tests/SCFA.ContentCenter.RegressionTests/` — client regression and WPF smoke coverage.
- `.github/workflows/` — build/release workflows.
- `docs/` — authoritative project manual.

## Working agreements

- Do not reconstruct requirements from old commits, removed V3/Win32 notes, abandoned player submission/review flows, or chat history.
- Player submissions and review were retired in dev52.
- Keep build artifacts, local backups, real packages, credentials, tokens, private server configuration, and one-off QA artifacts out of the public repository.
- Use existing regression/build gates and report what was actually verified.
- Keep `docs/PROJECT_HANDOFF.md` current after major milestones.
- Keep `docs/PRODUCT_REQUIREMENTS.md` aligned with confirmed owner decisions.
- Prefer extending/refactoring existing services and view models over parallel replacements.
- Search for existing models/services/helpers before adding another.
- Remove obsolete code only after checking references and regression coverage.
- Account service, COS, Windows client, Nginx, and Publication Gateway are separate concerns; identify the failing layer before changing API assumptions.
