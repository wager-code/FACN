# SCFA Content Center: agent guidance

This repository contains the Windows .NET 8 WPF client and the independent publication gateway for SCFA Content Center.

## Source of truth order

Before changing behavior, read these in order:

1. `PRODUCT_REQUIREMENTS.md` — confirmed owner requirements, scope, and current completion status.
2. `CODEBASE_GUIDE.md` — feature-to-file map; use it before creating a new module.
3. `PROJECT_HANDOFF.md` — verified current state, production facts, and unknowns.
4. `ROADMAP.md` — sequencing and future milestones; candidate ideas are not requirements.
5. `BUILD_VALIDATION.md` — concrete build/regression evidence.
6. `ADMIN_PUBLISH_PLAN.md` and `ADMIN_PUBLISH_API.md` — publication architecture and API contract when working on administrator publishing.

`README.md` is a user-facing overview, not the authoritative implementation status.

Do not reconstruct current requirements from old commits, removed V3/Win32 reference notes, or abandoned submission/review flows. Player submissions and review were retired in dev52.

## Repository layout

- `src/SCFA.ContentCenter/` — Windows .NET 8 WPF client.
- `server/SCFA.PublicationGateway/` — independent administrator publication gateway.
- `server/SCFA.PublicationGateway.RegressionTests/` — gateway regression coverage.
- `tests/SCFA.ContentCenter.RegressionTests/` — client regression and WPF smoke coverage.
- `.github/workflows/` — Windows build and publication-gateway release workflows.
- `ROADMAP.md` — planned development sequence.
- `CODEBASE_GUIDE.md` — architecture and feature/file navigation.

## Working agreements

- Work in the source checkout selected by the user. Preserve existing user files and make a source backup before a major local change.
- Keep build artifacts, local backups, real content packages, credentials, tokens, private server configuration, and one-off visual QA artifacts out of the public repository.
- Use the existing solution and regression scripts for relevant verification. Report exactly what was tested and what remains unverified.
- Keep `PROJECT_HANDOFF.md` current after major milestones. Separate verified facts, plans, and unknowns.
- Keep `PRODUCT_REQUIREMENTS.md` aligned with confirmed owner decisions.
- Prefer extending existing services/view models over creating parallel replacement modules unless there is a clear migration plan.
- Before adding a new model/service/helper, search for an existing equivalent and reuse or refactor it when practical.
- Remove obsolete code only after checking references and regression coverage.
- The account service, COS, Windows client, and publication gateway are separate concerns. Check actual server/deployed behavior before changing client API assumptions.
