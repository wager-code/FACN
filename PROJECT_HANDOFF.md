# SCFA Content Center project handoff

Updated: 2026-09-25. This file is intentionally safe for the public source repository. It is a project record, not a copy of private server configuration.

## What the system does

- A Windows .NET 8 WPF client lets players discover, install, update, and submit SCFA maps and MODs.
- Tencent Cloud COS stores published game content. A separate account service handles player sign-in and related API calls.
- The client has a review screen for authorized administrators. A complete submission-to-publication workflow still requires server-side work.

## Confirmed state

- The client source in this repository builds and has regression checks. The latest published source milestone is dev38.
- Account sign-in and several authenticated client functions were exercised successfully during development.
- The client can read the published COS catalog and has been checked against real local map and MOD directories.
- The deployed service does not currently fulfill the client's review-list request. Review and publication are therefore not end-to-end verified.
- The current repository does not contain the account-service source.

## Unknowns: do not guess

- The owner recalls a third-party site or service for reviewing player submissions. Its name, role, and integration details have not been recovered. Do not claim a specific vendor was used.
- The source and exact build process for the deployed account-service executable are not yet available.
- No production review or publication action has been validated. Do not treat a client screen or a permission name as proof of a working server workflow.

## Recommended next milestone

Preserve the existing account service and published COS objects while implementing a testable submission and review service. The intended flow is: authenticated submission, isolated pending content, package validation, authorized human decision, publication, and recorded history. Confirm the integration against a test deployment before changing production routing or published content.

## Moving to another computer

1. Clone `https://github.com/wager-code/FACN` into the location chosen for the new computer, and open that checkout as a local Codex project.
2. Ask Codex to read `AGENTS.md` and this handoff file, then inspect the current code and tests before changing anything.
3. Install a compatible Windows .NET SDK and run the solution build and relevant regression checks.
4. Restore local game files, private configuration, source backups, and any server or COS backups separately. They are not part of this public repository.
5. Re-check live service behavior before relying on old observations. Record new evidence and decisions here after each major milestone.

GitHub holds the portable client source and safe project record. Private credentials and server data require separate, protected backups. Chat history and local AI memory are useful context but are not the project record of truth.
