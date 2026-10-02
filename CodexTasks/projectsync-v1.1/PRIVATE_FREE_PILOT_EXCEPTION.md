# Private + GitHub Free pilot exception

Recorded 2026-10-01 after the repository owner chose to keep `fjnmgnkai/projectsynctest` private without a paid GitHub plan and to personally manage Git and merges for a small team.

This is an explicit exception to the v1.1 GitHub-side Quality Gate requirement, not a claim that the canonical requirement has been met. The canonical specification remains the target for all other behavior.

## What ProjectSync must enforce

- Member workflows treat `main` as read-only. A new Task observes current remote `main`, then writes only to its own `task/...` ref.
- Save, retry, and submission must compare the requested Task branch with the branch in coordination state. No direct `main` commit or push command is available through the normal tool path.
- The administrator reviews the Submitted SHA, Base Main SHA, conflict resolution, Candidate, and checks before a squash merge. Stale or failed validation is a ProjectSync-side stop condition.
- The UI must not describe an unprotected Private + Free repository as GitHub-enforced or fully compliant with v1.1.

## Residual risk accepted for a limited pilot

GitHub Free does not provide Branch Protection or Rulesets for this private repository. A collaborator with repository write access can bypass ProjectSync through GitHub or Git and change `main` or merge a PR without the ProjectSync checks. The repository owner can also make a manual mistake. ProjectSync-side guards and a review checklist reduce accidental errors but cannot remove this GitHub-side bypass.

The owner plans to perform Git management and merges. This is a trust-based, small-team pilot, not the v1.1 GitHub Quality Gate. Acceptance test AT-60 remains **not satisfiable on the current plan** and must not be marked passed. A paid plan with private-repository protection, or a newly approved specification change, is required before claiming the original GitHub-enforced property.

## Current implementation boundary

The desktop remains fail-closed: Task start/save/submit buttons are disabled until trusted coordination, remote reconciliation, Unity bridge, and administrator validation paths are connected and verified. The local Git CLI adapter and its tests do not themselves authorize team operation, merge, VRChat build, or upload.
