# SCFA Content Center: agent guidance

This repository contains the Windows .NET 8 WPF client for SCFA Content Center. Read `PRODUCT_REQUIREMENTS.md` before feature work and `PROJECT_HANDOFF.md` for verified state and unknowns. Player submissions and review were removed in dev52.

## Working agreements

- Work in the source checkout selected by the user. Preserve existing user files and make a source backup before a major change.
- After a major, verified improvement, update the public source on GitHub. Keep build artifacts, local backups, real content packages, credentials, tokens, and private operational details out of the public repository.
- Use the existing solution and regression script for relevant verification. Report what was tested and what remains unverified.
- Keep `PROJECT_HANDOFF.md` current after each major milestone. Label facts, plans, and unknowns distinctly; never reconstruct a missing past decision from a guess.
- Keep `PRODUCT_REQUIREMENTS.md` aligned with confirmed owner decisions. Keep the README brief; put implementation history in handoff and validation documents.
- The account service and COS are separate from this client repository. Check actual server code or deployed behavior before changing client API assumptions.

## Project references

- `SCFA.ContentCenter.sln`: Windows client solution.
- `run-regression-tests.ps1`: project regression checks.
- `PROJECT_HANDOFF.md`: portable project state and recovery notes.
- `PRODUCT_REQUIREMENTS.md`: confirmed product requirements, scope, and current completion status.
