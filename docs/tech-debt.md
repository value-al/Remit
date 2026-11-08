# Tech debt — Remit

Known debt and gaps against the value-al AI Development Protocol. Agents keep this current:
add a line when you find debt, remove it when it's paid. The product's own open items (from
the threat model and the ADRs) are listed at the end of `README.md` and are not repeated here.

## Open protocol question

| Question | Why it matters | Until the owner decides |
|---|---|---|
| **Public repo: agents may not run here until the owner decides.** The protocol's non-negotiable says agents run only in private repos; Remit is public on purpose (open-source reference). | The taskrunner must not pick up issues here; anything an agent pushes is public at once | No unattended agents. The merge bot is installed: it runs on `pull_request_target`, never checks out PR code, and still needs `human-approved` for every PR at L0. Options: keep it human-only, run agents in a private mirror and publish merged `main`, or write an exception into the protocol |

## Code

| Item | Why it matters | Fix |
|---|---|---|
| `EfWithdrawalRepository` lives in `Funding/Withdrawals/`, while `EfDepositRepository` is in `Persistence/` | Withdrawals and Persistence depend on each other; the architecture test allows it by name | Move it to `Persistence/PostgresStores.cs` and drop the exception in `ArchitectureRules.cs` |
| Funding's `launchSettings.json` uses port 5038; `README.md`, the console and `project.yml` say 5000 | `dotnet run` locally does not match the docs | Set the `http` profile to `http://localhost:5000` |
| Funding's `appsettings.Development.json` has no `Ledger:BaseUrl` | Locally, withdrawals check the in-memory balance stub, not the running Ledger | Add `"Ledger": { "BaseUrl": "http://localhost:5100" }` (or document why not) |
| `README.md` embeds the overview picture twice; `docs/architecture/c4-context.md` repeats its first paragraph | Reads as a mistake on a public showcase | Delete the duplicates |
| Unit and Testcontainers tests are not separated (no trait or project split) | `dotnet test` needs Docker; there is no fast unit-only command for the pre-push hook | Tag the PostgreSQL/RabbitMQ tests `[Trait("Category", "Integration")]` in new tests, or split projects |
| GitHub Actions pinned to tags (`@v4`, `@v2`, `@v7`), not commit SHAs (Semgrep warning in `ci.yml`, `deploy.yml`) | A moved tag could run changed code in CI | Pin each action to its commit SHA |
| `deploy.yml` interpolates `${{ inputs.name }}` and step outputs into `run:` scripts (Semgrep ERROR) | Shell injection in a job holding an Azure OIDC token | Issue #2; the `scan` job fails until it is fixed |

## Protocol gaps

| Gap | Needed for | Plan |
|---|---|---|
| No staging environment | Hosting contract: staging deploys every merge | Decide whether the public sandbox becomes staging, or add one |
| Hosting contract / Coolify: the sandbox is deployed by the value.al Azure Pipeline on the host; AKS by a manual `deploy.yml` with Azure OIDC secrets in GitHub | Hosting contract: the platform deploys `main` and holds the secrets; only a deploy webhook token in GitHub | Move the sandbox to Coolify on Hetzner; write the AKS path as a stack-profile exception (the `dotnet` profile names Coolify) or retire it |
| No commit SHA endpoint (`/health/live`, `/health/ready` don't report it) | Deploy workflow waits for the merged commit | Add the SHA as a build arg and return it from `/version` |
| No holdout scenarios, no browser E2E (Playwright) | QA agent | Comes with the scenarios repo; the console at `/console` is the natural surface |
| Mutation testing configured (`stryker-config.json`) but no score target | L2 gate | First full run: see `project.yml`; set the target from the pilot |
| No pre-push hook (`.githooks/`) and no gitleaks pre-commit hook | Checks before push, secret scanning before it reaches GitHub | Add `.githooks/pre-push` + `checks` as in the pilot once a fast unit-only command exists |
| `AGENTS_PAUSED` repository variable not set | Kill switch (the merge bot treats unset as not paused) | Set `AGENTS_PAUSED=false` when the platform's `Stop-Agents.ps1` covers this repo |
| No machine account fork, GitHub App, taskrunner or reviewer agent | `reviewer@value-al-bot` is a required check, so the merge bot cannot merge yet | Comes with the protocol platform; until then the owner merges |
