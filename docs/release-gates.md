# Release gates

The ordinary CI checks are required before merge: `dotnet-build`, `python-targeted-tests`, `migration-validation`, and `desktop-resource-check`.

`optional-package-installer` runs only from workflow dispatch and is not a required pull-request check.

The following evidence is deliberately outside commit CI and must be recorded as READY before merging an MVP release branch or creating a tag:

- 5-minute end-to-end recording;
- server-offline recovery;
- Recorder crash recovery;
- worker crash recovery;
- 30-minute and 2-hour endurance;
- backup/restore acceptance;
- RBAC isolation.

Set `MVP_V1_READY=true` only after all eight records are available. Create a release tag only after the protected `main` merge is green.

If branch protection cannot be changed by automation, open `Settings` → `Rules` → `Rulesets` → `New branch ruleset` for `main` (or `Settings` → `Branches` on repositories using legacy protection). Require a pull request, require the four named checks above, require the branch to be up to date, and disallow force pushes and deletions.
