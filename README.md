# ghpmv — GitHub Projects Migrator

`ghpmv` is a CLI that migrates **GitHub Projects V2** between organizations, including Views and Workflows.

Most existing tools (e.g. [timrogers/gh-migrate-project](https://github.com/timrogers/gh-migrate-project)) migrate fields, items and field values. `ghpmv` also uses the GraphQL View mutations for names, layouts, filters and ordered visible fields. Advanced View settings and Workflows can be completed with an **opt-in browser automation module** (Playwright + your own signed-in session):

| Capability | gh-migrate-project | ghpmv |
|---|---|---|
| Fields / items / field values | ✅ | ✅ |
| Draft issues (with author note) | ✅ | ✅ |
| Iteration fields incl. completed iterations | ➖ | ✅ |
| Item order & archived state | ➖ | ✅ |
| Status Updates and organization Project template state | ❌ | ✅ |
| **Views (all layouts, filters, grouping, slicing, roadmap)** | ❌ | ✅ (GraphQL + optional browser automation) |
| **Workflows (auto-add, auto-archive, item state automations)** | ❌ | ✅ (opt-in browser automation) |
| Post-migration verification (`ghpmv verify`) | ❌ | ✅ |

## Migration flow

The following diagram shows how GitHub Enterprise Importer and the main `ghpmv` subcommands work together:

![GitHub Enterprise Importer and ghpmv migration flow](docs/ghpmv-migration-flow.svg)

## Installation

Requires no runtime for the self-contained builds; the portable build and the global tool require the [.NET 10 runtime/SDK](https://dotnet.microsoft.com/download).

### Option 1: Self-contained archive (no .NET required)

Download the archive for your platform from [Releases](https://github.com/SIkebe/ghpmv/releases), verify it against `SHA256SUMS.txt`, extract it and run `ghpmv` (`ghpmv.exe` on Windows):

- `ghpmv-vX.Y.Z-win-x64.zip`
- `ghpmv-vX.Y.Z-win-arm64.zip`
- `ghpmv-vX.Y.Z-linux-x64.tar.gz`

### Option 2: Framework-dependent archive (portable, needs .NET 10)

Download `ghpmv-vX.Y.Z-portable.zip`, extract, and run:

```
dotnet ghpmv.dll --version
```

### Option 3: .NET global tool

```
dotnet tool install -g ghpmv
ghpmv --version
```

> NuGet.org publishing may lag behind GitHub Releases; the release assets are always the source of truth.

## Quick start

```bash
# 1. Export the source project to a JSON snapshot
ghpmv export --org source-org --project 7 --out ./snapshot --token $SOURCE_TOKEN

# 2. Import the snapshot into the target organization
ghpmv import --org target-org --in ./snapshot --token $TARGET_TOKEN \
  --repo-mapping repos.csv --user-mapping users.csv --org-mapping orgs.csv \
  --team-mapping teams.csv

# 3. Verify the migrated project against the snapshot
ghpmv verify --org target-org --project 12 --in ./snapshot --token $TARGET_TOKEN \
  --repo-mapping repos.csv --user-mapping users.csv --org-mapping orgs.csv \
  --team-mapping teams.csv
```

Tokens are resolved from `--token`, then the `GITHUB_TOKEN` / `GHPMV_TOKEN` environment variables.

`verify` reports an overall result and a result for Project, Field, Item, StatusUpdate, View, Workflow, Collaborator, LinkedRepository, and TeamLink:

| Result | Meaning |
|---|---|
| `Match` | Every available category was verified with no material difference. |
| `Mismatch` | At least one migration-owned value differs. |
| `PartialMatch` | No errors, but a non-fatal warning exists (for example, target-only data). |
| `NotVerified` | Required source or target data was not captured, so full equality cannot be established. |
| `NotApplicable` | The category does not apply, such as Team links on a user-owned Project. |

`Mismatch` and `NotVerified` always produce exit code 1. `--fail-on-warning` also fails when warnings exist. Use `--report-json <path>` for the same overall/category results and counts in machine-readable form. `--categories View,Workflow` restricts comparison and target read-back to selected categories; browser automation then opens only the corresponding UI surfaces. Without browser automation, GraphQL-readable View settings are still compared, but UI-only View/Workflow settings and explicit collaborators are reported as `NotVerified`; use `--enable-browser-automation` when verification must prove those areas too. Explicit collaborator capture also requires the browser/token user to access the Project's **Settings → Manage access** page; GitHub requires a project admin or organization owner to [manage access to an organization Project](https://docs.github.com/en/issues/planning-and-tracking-with-projects/managing-your-project/managing-access-to-your-projects).

| Category | Verification coverage |
|---|---|
| Project | Description, README, visibility, closed state, and organization template state. A changed title is informational because import supports title overrides. |
| Field | Field presence/type, select option order/name/color/description, Issue Field description/visibility/linkage, Iteration default duration/start weekday, and individual iteration dates/durations. Missing Iteration configuration is normalized to the uninitialized 0/0 defaults. |
| Item | Counts/types, issue and pull request identity, draft body, field values (including Project and Issue Field multi-select values), active-item order, and archived state. Archived-item order is excluded because GitHub cannot restore it. |
| StatusUpdate | History order, body (including the imported attribution note), status, start date, and target date. |
| View | Name/layout plus GraphQL filter, visible fields/order, grouping, and sorting. Browser mode adds slice, swimlanes, field sums, and roadmap dates/zoom/markers. |
| Workflow | Name/enabled state. Browser mode adds content types, status, filter, and repository. |
| Collaborator | Browser-captured explicit user/team collaborators and roles. Inherited and base-role access is excluded. |
| LinkedRepository | Linked repository identities after repository mapping. |
| TeamLink | Linked Team identities after Team mapping. Missing links are errors; target-only links are warnings and are not removed. This is separate from explicit Team collaborators and roles. |

Insights charts, item/field-value history, and inherited/base-role access are not verified.

### Token permissions

This section covers the normal migration commands: `export`, `import`, and `verify`. You do **not** need permission to create repositories, Issues, or pull requests for a normal Project migration; move or create the target repositories separately before running `ghpmv import`.

Use separate source and target tokens when the resource owners or accounts differ. The source token only needs the `export` permissions. The target token needs the union of the `import` and `verify` permissions.

#### Classic PATs

GitHub documents `read:project` for Project queries and `project` for queries and mutations. The classic `repo` scope is only needed when the migration must access private repository content; it grants broad read/write repository access, so prefer fine-grained PATs when practical. See [Using the API to manage Projects](https://docs.github.com/en/issues/planning-and-tracking-with-projects/automating-your-project/using-the-api-to-manage-projects#authentication) and [Scopes for OAuth apps](https://docs.github.com/en/apps/oauth-apps/building-oauth-apps/scopes-for-oauth-apps).

| Command | Classic PAT scopes |
|---|---|
| `ghpmv export` | `read:project`. Organization-owned Projects also need `read:org` to read linked Teams and organization Issue Field definitions. Add `repo` when the source Project contains items or linked repositories from private repositories. |
| `ghpmv import` | `project`. Organization-owned Projects also need `read:org` for Team/organization resolution; use `admin:org` instead when the snapshot contains Issue Fields because import creates or updates organization Issue Field definitions. Add `repo` when resolving private target repositories or writing Issue Field values (`public_repo` is sufficient when every affected repository is public). |
| `ghpmv verify` | `read:project`. Organization-owned Projects also need `read:org` to read linked Teams and organization Issue Fields. Add `repo` when the target Project contains items or linked repositories from private repositories. |

Authorize the token for organizations or enterprises that require SSO, including SAML- or OIDC-backed environments.

#### Fine-grained PATs

Use fine-grained PATs only for **organization-owned** Projects. GitHub lists access to Projects owned by a user account as a current [fine-grained PAT limitation](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens#fine-grained-personal-access-token-limitations); use a classic PAT for `--owner-type user`.

Create each fine-grained token for the organization that owns the source or target Project. Grant repository access to every repository that can appear as a Project item or linked repository; selecting all repositories for that resource owner is the simplest option during a migration. See GitHub's [Permissions required for fine-grained personal access tokens](https://docs.github.com/en/rest/authentication/permissions-required-for-fine-grained-personal-access-tokens).

| Command | Fine-grained PAT permissions |
|---|---|
| `ghpmv export` | **Organization permissions → Projects: Read-only** and **Members: Read-only** when linked Teams are present. Add **Organization permissions → Issue Fields: Read-only** when the Project contains organization Issue Fields. **Repository permissions → Metadata: Read-only**, plus **Issues: Read-only** and **Pull requests: Read-only** for private repositories that contain project items. |
| `ghpmv import` | **Organization permissions → Projects: Read and write** and **Members: Read-only** to resolve and link Teams. When the snapshot contains organization Issue Fields, add **Organization permissions → Issue Fields: Read and write** for their definitions and **Repository permissions → Issues: Read and write** for their values. **Repository permissions → Metadata: Read-only**; add **Contents: Read and write** for linked repositories, plus **Issues: Read-only** and **Pull requests: Read-only** for private repositories referenced by `--repo-mapping` or auto-add workflows. |
| `ghpmv verify` | Same as `ghpmv export` for the target project. |

GitHub supports [pre-filled fine-grained PAT creation URLs](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens#pre-filling-fine-grained-personal-access-token-details-using-url-parameters). Replace `SOURCE_ORG` or `TARGET_ORG` before opening these templates:

```text
# Export an organization Project
https://github.com/settings/personal-access-tokens/new?name=ghpmv-source-export&description=Export+an+organization+Project+with+ghpmv&target_name=SOURCE_ORG&expires_in=30&organization_projects=read&metadata=read

# Import and verify an organization Project
https://github.com/settings/personal-access-tokens/new?name=ghpmv-target-import&description=Import+and+verify+an+organization+Project+with+ghpmv&target_name=TARGET_ORG&expires_in=30&organization_projects=write&metadata=read
```

Append only the permissions needed for the selected migration path: `&issues=read&pull_requests=read` for private repository items, `&issue_fields=read` for exporting or verifying organization Issue Fields, `&issue_fields=write&issues=write` when importing their definitions and values, `&contents=write` when importing linked repositories, and `&members=read` when reading or resolving linked Teams or Team collaborators. GitHub's [fine-grained PAT permission matrix](https://docs.github.com/en/rest/authentication/permissions-required-for-fine-grained-personal-access-tokens#organization-permissions-for-issue-fields) identifies **Issue Fields** as a separate organization permission. The URL cannot select **Repository access**; after opening it, select every repository used by Project items, linked repositories, or Workflows. Review the pre-filled values and adjust the expiration if required by organization policy before generating the token.

GitHub does not publish fine-grained PAT requirements for each Projects GraphQL mutation. The **Contents: Read and write** requirement for `linkProjectV2ToRepository` is based on ghpmv's live GitHub testing: read-only Contents access was insufficient. GitHub separately documents a Contents permission for GitHub App installation tokens when `createProjectV2` links a repository, but that guidance is not PAT-specific and does not document `linkProjectV2ToRepository`.

GitHub permissions are still enforced in addition to token permissions: the token owner must be allowed to read the source project and referenced repositories, and must be allowed to create or edit the target project. [Changing an organization Project's visibility](https://docs.github.com/en/issues/planning-and-tracking-with-projects/managing-your-project/managing-visibility-of-your-projects) requires an organization owner or project admin, and an organization can restrict the operation to owners; import skips the visibility mutation when the target already matches the snapshot.

When a snapshot contains organization Issue Fields, the authenticated target user must be an organization administrator in addition to having `admin:org` or fine-grained **Issue Fields: Read and write**. GitHub documents this role requirement for [creating organization Issue Fields](https://docs.github.com/en/rest/orgs/issue-fields#create-issue-field-for-an-organization); GEI Migrator access and token scopes do not replace it. [Linking a Project to a Team](https://docs.github.com/en/issues/planning-and-tracking-with-projects/managing-your-project/adding-your-project-to-a-team) requires an organization owner, or a Team maintainer who also has admin access to the Project; import preflights both sides before writing. Importing collaborators also requires permission to manage the target Project, and linked/private repository data still requires access to each mapped target repository.

> `ghpmv setup --fixture` is a maintainer/test command, not a migration prerequisite. It creates a demo repository, Issues, a pull request, and a Project, so it intentionally needs broader permissions. See [Fixture credentials](#fixture-credentials-maintainers-only).

`--repo-mapping` / `--user-mapping` / `--org-mapping` / `--team-mapping` map repositories, user logins, organizations, and linked Teams across deployments. Repository, organization, and Team mappings use the `source,target` header; Team rows use `organization/slug` on both sides. A blank Team target keeps the slug in the import destination organization; fill the target for a renamed Team. User mappings use GitHub Enterprise Importer's mannequin reclaim header (`mannequin-user,mannequin-id,target-user`); the mannequin ID is ignored. `ghpmv export` generates `repository-mappings.csv`, `organization-mappings.csv`, `team-mappings.csv` when links exist, and `user-mappings.csv` when users are present. Existing files are never overwritten. Team preflight runs before any Project write and rejects malformed or missing targets, many-to-one mappings, unreadable or non-administerable Teams, and existing Projects the token cannot update. During browser-assisted import, filter values are mapped structurally as before. Pass the same mappings to `ghpmv verify`.

Run `ghpmv requirements --in <snapshot-directory>` after export to calculate target capabilities without a token; pass `--owner-type user` for a user-owned target so organization-only Team requirements are reported as not applicable. It reports organization, Project, and Team administrator gates plus per-repository read/write/linked/Auto-add requirements from `snapshot.json`. `ghpmv import` repeats the analysis and performs read-only preflight before any Project, item, status, or browser write: organization Issue Field capability must return the expected validation-only response, every required repository must be mapped and visible, write operations require a writable repository role, and linked/Auto-add repositories must belong to the target owner.

### More import/export options

```bash
# Export ALL projects of the org at once (one snapshot per project under <out>/<number>/)
ghpmv export --org source-org --out ./snapshots            # add --include-closed to include closed projects
ghpmv import --org target-org --in ./snapshots/7           # then import each snapshot individually

# Import into an EXISTING project (fields/items are merged; the project keeps its title)
ghpmv import --org target-org --in ./snapshot --project-number 42 \
  --repo-mapping ./snapshot/repository-mappings.csv

# Create the project under a different title
ghpmv import --org target-org --in ./snapshot --project-title "Roadmap (migrated)"
```

`--project-number` is mutually exclusive with `--on-conflict` and `--project-title`.

When a project with the same title already exists, `--on-conflict` controls the entire import:

| Value | Result | Existing project changes |
|---|---|---|
| `fail` (default) | Exits with an error | None |
| `skip` | Exits successfully with `result=skipped` | None; items, fields, metadata, collaborators, linked repositories, views, and workflows are not imported |
| `update` | Exits successfully with `result=updated` | Applies the snapshot, including items and browser-assisted views/workflows when enabled |

Creating a new project emits `result=created`. The result line also includes the target project number for machine-readable automation, for example `result=skipped project=42`.

### Recovering from an ambiguous mutation result

Uninitialized Iteration fields (duration `0`, start day `0`, and no active or completed iterations) are recreated without explicit configuration, preserving their unset state rather than inventing a schedule. Initialized fields with an empty iteration list retain their configured start weekday. Other nonpositive durations or invalid start weekdays fail validation before any Project write. This fix does not discard pending operations from earlier failed imports; the reconciliation rules below still apply.

Read-only GraphQL queries and explicitly idempotent updates are retried after transient network or server failures. Resource-creation mutations are not: if GitHub may have accepted a mutation but its response was lost, `ghpmv` exits with `Mutation result is ambiguous` instead of risking a duplicate. The error includes the operation, target, and a non-secret client mutation ID; mutation variables and tokens are never included.

Inspect the named target operation in GitHub before retrying. Rerun with the same snapshot directory so `project-import-log.json` and `import-log.json` can reconcile pending work. Project, custom-field, organization Issue Field, Draft, and Issue/PR item creation atomically records an operation and matching target baseline before sending. On resume, `ghpmv` polls for and adopts exactly one new match; no match or multiple matches stop the import for manual reconciliation instead of resending. Project-to-Issue-Field linking is idempotent: a pending link is resent with its recorded client mutation ID and cleared after a definitive success. Resume stops for manual reconciliation if the recorded project or Issue Field no longer matches the current target.

When an import fails, `ghpmv` writes `import-error.json` in the snapshot directory and prints its path to stderr. The report contains the requested and actual target Project context, the primary failure stage, separately identified cleanup failures, the latest 200 progress messages, exception and inner-exception stack traces, GraphQL error types, and HTTP status codes. An additive `context` on the report, exception/cleanup entries and progress entries identifies the source and destination Projects, the migration element (and parent item for item-field replay), and the operation where known. The same identity is printed directly with the error on stderr; it is not inferred from the last progress message. Ambiguous create results also include the locally generated operation name, client mutation ID, attempt time, target, and recovery guidance. Raw GraphQL error responses, tokens, and mutation variables are not written to the report. A later failure in the same snapshot directory replaces the previous report; only a clean completed import removes it, while a skipped or warning-completed import leaves the previous report available for investigation.

For example, synthetic iteration-field failure diagnostics may include:

```text
source: source-org / Project 12 "Demo project"
target: target-org / Project 34 "Demo project"
element: Field "Demo sprint" (ITERATION)
operation: createProjectV2Field
httpStatus: 200 OK
errorCode: UNPROCESSABLE
requestId: ABCD:1234
```

New exports include optional `source` provenance (owner, owner type, API host, project number and title) in `snapshot.json`. Older snapshots remain readable; their source owner/number/host are unknown, not guessed from repository mappings. Target numbers/IDs are only reported as resolved once known; an explicitly requested number remains separately available. Existing-project titles are omitted when they have not been read. New fields/items have no target ID until creation succeeds. Issue/PR identity uses repository and number, not content titles. Bulk collaborator and Issue Field writes identify the batch rather than claiming one member failed. Status updates use their snapshot sequence, never their body. Item resume logs retain compatible error strings and optional `fieldValuesErrorContext`, `positionErrorContext`, `archiveErrorContext`, and `fieldValueFailures`; successful retries clear the corresponding diagnostics.

Diagnostic provenance is excluded from resume fingerprints, so adding it does not invalidate older resume state. `--project-title` changes the intended destination title, not the recorded source title. Errors before snapshot loading retain only the known destination and stage. Command-line parser errors (such as an invalid URL option) occur before import starts and do not create a report; URL validation messages do not echo the supplied URL.

Exception entries also include `failureReason`, `requestId` (the `X-GitHub-Request-Id` response header), and `graphQlErrors` with per-error types, safe message summaries, and paths. HTTP 200 is recorded when the HTTP request succeeds but GraphQL reports an error. For example, `graphql-error-with-mutation-payload` means an error arrived alongside the mutation's response key, even if its value was null; it does not prove whether creation succeeded. `missing-mutation-result`, `malformed-response`, and `transport-failure` distinguish other ambiguous outcomes without changing retry behavior. Request metadata belongs to the failing attempt, not an earlier retried response.

Server messages are converted to fixed summaries for recognized internal errors, temporary conflicts, inaccessible resources, and unresolved resources; unrecognized messages are explicitly marked as redacted. Unknown error types and path segments not present as identifiers in the query document are redacted, and request IDs are restricted to hexadecimal, colon, and hyphen characters. The query scanner consumes escaped block-string delimiters and treats unterminated strings as opaque rather than collecting their contents as identifiers. Query text, input values, arbitrary server extensions, and raw response bodies are never added to these diagnostic fields. Use the request ID and timestamp to correlate a redacted error with GitHub support; retain the snapshot and operation logs for recovery.

#### Sensitive API diagnostics (explicit opt-in)

By default, failed GraphQL and REST response bodies are not logged. API exception messages, retry progress, warnings, `import-error.json`, and error fields in resume logs use safe summaries, HTTP status, allowlisted error codes, sanitized request IDs, operation kind, and retry counts where available. Issue Fields permission preflight reports boolean validation facts rather than response text or arbitrary permission headers. Iteration failures retain validation facts and active/completed counts; separate identity context deliberately retains the field name/type, but never iteration values or request inputs. Internal raw GraphQL errors remain in memory only for existing retry/permission classification.

`export`, `import`, `verify`, and API operations under `setup` accept **`--allow-sensitive-diagnostics`**. It applies to **that invocation only**; there is no saved-config or environment-variable fallback. It does not enable browser tracing or general verbose logging.

- On the first failed API response, stderr prints a **SENSITIVE** warning and the absolute destination **before** creating/writing `ghpmv-sensitive-api-<unique-id>.jsonl` in the **current working directory**, not the snapshot directory. No file is created if there are no failed responses. Each invocation uses a new filename and exclusive creation; existing files are never overwritten.
- Only non-success HTTP responses (including REST 404 and validation-probe 422 responses) and HTTP 200 GraphQL error responses are captured, including retried attempts. HTTP-success bodies without GraphQL errors are not captured, even if malformed or missing an expected result. A GraphQL error response may also contain partial data; the entire bounded response is sensitive.
- Each JSONL record contains timestamp, a fixed operation-kind label, numeric status, sanitized request ID, retry count, and response body. Bodies are limited to **65,536 UTF-16 characters per response**, with `originalBodyCharacters` and explicit `truncated` flags. The number of records is not capped; monitor disk space on long runs. Writes are serialized and flushed per response, then the invocation disposes the file. Cancellation does not interrupt a record already being written.
- Files are created with owner read/write permissions on Unix. On Windows they inherit the working directory's ACL: **run from a private, access-controlled directory**, not a shared directory. Files are not encrypted and are not automatically deleted. The repository ignores their filename pattern, but that does not protect copies or files outside this repository.
- I/O failures produce an explicit safe error; raw bodies never fall back to stderr or ordinary logs. A partial file may remain. If a diagnostic write fails after a create request, the result remains ambiguous so pending migration operations are retained; inspect target state before retrying.

Request bodies, GraphQL query documents/variables, Authorization headers, PATs, cookies, and browser storage-state are **not collected as diagnostic inputs**. However, failed server bodies can themselves echo secrets or private data. **Opt-in files are not safe to share without review**; redact them manually and delete them when no longer needed.

This is an **API failure diagnostic boundary, not an anonymity guarantee**. Default migration diagnostics deliberately retain organization/user/repository names, project titles, Draft/Field/View/Workflow names, numbers and known IDs. Do not treat them as safe for public sharing. Identity text is bounded to 256 characters and control/format characters are escaped to prevent forged log lines; diagnostic target URLs drop user information, query strings and fragments. Migration browser failure summaries suppress raw Playwright exception/call-log text and retain scoped identity instead. This does not enable or sanitize browser traces or other commands' output. Snapshots, mappings, verification differences and required pending-operation/resume payloads may still contain business content needed for migration. Review all artifacts before sharing. The sensitive-response opt-in remains isolated to its own file and stdout result formats are unchanged.

Status Update creation is stricter because GitHub exposes neither an idempotency key nor a deterministic lookup key. Only a target Status Update node ID returned by the create mutation and persisted in `import-log.json` is accepted as completion. If the result is ambiguous before that ID is persisted, the pending entry remains durable and reruns fail with actionable manual-reconciliation instructions; body, status, and dates are never used to claim an existing or concurrent update.

If the target project was created before the interruption, resume with `--on-conflict update`; when the original import targeted an existing project, pass the same `--project-number`. The default `--on-conflict fail` and `skip` modes intentionally do not modify an existing project and therefore cannot continue pending field or item reconciliation.

### User-owned projects

`export` / `import` / `verify` accept `--owner-type user` to migrate projects owned by a user account instead of an organization (URLs use the `/users/<login>/projects/<n>` form):

```bash
ghpmv export --org monalisa   --owner-type user --project 4 --out ./snapshot
ghpmv import --org octocat    --owner-type user --in ./snapshot
ghpmv verify --org octocat    --owner-type user --project 2 --in ./snapshot
```

Project-to-Team links apply only to organization-owned Projects. For user-owned Projects, export writes an empty Team-link list, import performs no Team-link operation, and verify reports TeamLink as `NotApplicable`.

### Full-fidelity Views & Workflows (opt-in browser automation)

View names, layouts, filters and ordered visible fields are imported through GraphQL without browser automation. Grouping, sorting, slicing, field sums, Roadmap display settings and Workflows still require the Projects web UI, so `ghpmv` can supplement the API import with Playwright using **your own browser session**. This is strictly **opt-in**:

```bash
# One-time setup
ghpmv setup --browsers            # installs the Playwright Chromium browser
ghpmv login --expected-login octocat  # fresh interactive sign-in; session saved locally

# Then add --enable-browser-automation to export/import/verify
ghpmv export --org source-org --project 7 --out ./snapshot --enable-browser-automation
ghpmv import --org target-org --in ./snapshot --enable-browser-automation
ghpmv verify --org target-org --project 12 --in ./snapshot --enable-browser-automation
```

Export and verify read exact linked-field identity from GraphQL
`ProjectV2FieldCommon.isIssueField` and the applicable field's `issueField` definition.
This preserves hidden, unset, or same-name fields without browser automation. If the public
field connection fails, `ghpmv` exits without writing or comparing a partial snapshot.

### Cross-account migration (e.g. non-EMU source → EMU target)

Use named browser profiles when the source and target require different accounts:

```bash
ghpmv login --profile source --expected-login SOURCE_LOGIN
ghpmv login --profile target --expected-login TARGET_LOGIN --base-url https://TENANT.ghe.com

ghpmv export --org source-org --project 7 --out ./snapshot \
  --token $SOURCE_TOKEN --enable-browser-automation --browser-profile source

ghpmv import --org target-org --in ./snapshot \
  --token $TARGET_TOKEN --target-base-url https://api.TENANT.ghe.com \
  --browser-base-url https://TENANT.ghe.com \
  --repo-mapping ./snapshot/repository-mappings.csv \
  --user-mapping ./snapshot/user-mappings.csv \
  --org-mapping ./snapshot/organization-mappings.csv \
  --enable-browser-automation --browser-profile target

ghpmv verify --org target-org --project 12 --in ./snapshot \
  --token $TARGET_TOKEN --target-base-url https://api.TENANT.ghe.com \
  --browser-base-url https://TENANT.ghe.com \
  --repo-mapping ./snapshot/repository-mappings.csv \
  --user-mapping ./snapshot/user-mappings.csv \
  --org-mapping ./snapshot/organization-mappings.csv \
  --enable-browser-automation --browser-profile target
```

`ghpmv login` always starts with a fresh browser context instead of loading the profile's
existing cookies. Use `--expected-login` to guard against SSO or another login flow selecting
the wrong account; a mismatch fails without overwriting the profile state.

For GHEC with data residency, point `ghpmv export --base-url` (source) or `ghpmv import`/`ghpmv verify` `--target-base-url` (target) at the tenant API endpoint, e.g. `https://api.TENANT.ghe.com` (a trailing `/graphql` is added automatically). Browser-enabled export/import/verify derives `https://TENANT.ghe.com` from that API URL; `--browser-base-url` can set it explicitly and is rejected when it names a different deployment. `setup --fixture-ui` applies the same derivation and validation to `--api-base-url`. Before browser reads or writes, `ghpmv` also verifies that the selected browser profile is signed in on that host as the same login used by the API token. Cloud API and browser origins must use HTTPS; HTTP is accepted only for loopback test origins. GHEC with data residency is designed to work but requires the manual tenant validation described below.

### Proxies

`ghpmv` uses the standard .NET `HttpClient`, which honors the `HTTPS_PROXY` / `HTTP_PROXY` (and `NO_PROXY`) environment variables by default — no extra configuration is needed behind a corporate proxy.

## Supported environments

| Source | Target | Status |
|---|---|---|
| GitHub.com (non-EMU) | GitHub.com (non-EMU) | ✅ Supported |
| GitHub.com (non-EMU) | GitHub.com (EMU) | ✅ Supported (user mapping to `_shortcode` logins) |
| GitHub.com (EMU) | GitHub.com (EMU / non-EMU) | ✅ Supported |
| GitHub.com | GHEC with data residency (`*.ghe.com`) | ⚠️ Designed to work, **not yet verified** |
| GitHub Enterprise Server (GHES) | any | ❌ Not supported |

Organization projects and user-owned projects (`--owner-type user`) are both supported.

## What ghpmv can migrate today

`ghpmv` migrates Projects V2 configuration and membership after repositories, issues, and pull requests have been moved with GitHub Enterprise Importer or another migration tool. It covers fields, items, values, ordering, archived state, linked repositories, API-backed View migration, and opt-in browser enrichment for advanced View settings and Workflows.

See [Migration scope and limitations](docs/MIGRATION_SCOPE.md) for the complete support matrix, prerequisites, unsupported areas, and browser automation constraints.

## Update check

`export` / `import` / `verify` asynchronously check GitHub Releases for a newer version (2-second timeout; failures are silently ignored; **no telemetry is ever sent**). Opt out with `--no-update-check` or by setting the `GHPMV_NO_UPDATE_CHECK` environment variable.

## Current limitations

The most important constraints are that `ghpmv` does not migrate repositories or Issue / PR metadata, GHES is not supported, and UI automation is opt-in and best effort. See [Migration scope and limitations](docs/MIGRATION_SCOPE.md) for full details.

## Development docs

- [Migration scope and limitations](docs/MIGRATION_SCOPE.md) contains the detailed support matrix, prerequisites, and platform constraints.
- [Test strategy](docs/TEST_STRATEGY.md) is a Japanese summary of the automated, browser, CI, packaging and manual release validation layers.
- [Manual test plan](docs/MANUAL_TEST_PLAN.md) walks through the GEI + `ghpmv` end-to-end migration validation flow.

`TeamLinkRoundTripTests` creates and deletes disposable Teams in both `GHPMV_TEST_ORG` and `GHPMV_TEST_TARGET_ORG`. A credentialed integration run therefore requires a token whose owner can create and delete Teams in both organizations (normally an organization owner, or a member allowed to create Teams with sufficient Team administration rights), in addition to the Project permissions described above. The REST Team endpoints require `admin:org` on a classic PAT or **Organization permissions -> Members: Read and write** on a fine-grained PAT. Because the suite uses one `GHPMV_TEST_TOKEN` for both organizations, the normal cross-organization setup uses a classic PAT whose owner and scope cover both; a fine-grained PAT can only be used when its resource-owner scope covers every organization the test modifies.

### Fixture credentials (maintainers only)

This section applies only when creating disposable demo/test resources with `ghpmv setup --fixture`. Normal users migrating an existing Project do not run this command.

The fully automated fixture path creates a private organization repository, its initial contents, Issues, a pull request, a Project, and an organization Issue Field. GitHub's [fine-grained PAT permission matrix](https://docs.github.com/en/rest/authentication/permissions-required-for-fine-grained-personal-access-tokens#repository-permissions-for-administration) lists **Administration: Read and write** for `POST /orgs/{org}/repos`; the same matrix lists [**Issue Fields: Read and write**](https://docs.github.com/en/rest/authentication/permissions-required-for-fine-grained-personal-access-tokens#organization-permissions-for-issue-fields) for creating and managing organization Issue Fields. The fixture also needs resource owner set to the organization, repository access set to **All repositories**, **Contents: Read and write**, **Issues: Read and write**, **Pull requests: Read and write**, and **Organization permissions → Projects: Read and write**.

Replace `FIXTURE_ORG` before opening this pre-filled fixture token template, then manually select **All repositories**:

```text
https://github.com/settings/personal-access-tokens/new?name=ghpmv-fixture&description=Create+a+disposable+ghpmv+fixture&target_name=FIXTURE_ORG&expires_in=30&administration=write&contents=write&issues=write&pull_requests=write&organization_projects=write&issue_fields=write&metadata=read
```

Those token settings do not override the token owner's organization role, the organization's [repository-creation policy](https://docs.github.com/en/organizations/managing-organization-settings/restricting-repository-creation-in-your-organization), [PAT policy](https://docs.github.com/en/organizations/managing-programmatic-access-to-your-organization/setting-a-personal-access-token-policy-for-your-organization), token approval, or SSO authorization. The authenticated user must be an organization administrator because the fixture creates an organization Issue Field; a classic PAT with `admin:org` does not replace that role. Preflight both endpoints before fixture creation; for the most reliable fully automated path, use an administrator-owned classic PAT with `repo`, `project`, and `admin:org`. If **Administration** or **All repositories** access cannot be granted, have the same administrator create the empty organization repository separately and use a selected-repository fine-grained PAT with the remaining fixture permissions. See the [manual test plan](docs/MANUAL_TEST_PLAN.md#fixture-token-permissions) for the complete fixture workflow.

## License

[MIT](LICENSE) © SIkebe
