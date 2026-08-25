# Migration scope and limitations

`ghpmv` migrates GitHub Projects V2 configuration and project membership after the underlying repositories, issues, and pull requests have already been moved or made visible to the target account, typically with GitHub Enterprise Importer (GEI).

![GitHub Enterprise Importer and ghpmv migration flow](ghpmv-migration-flow.svg)

Issue and pull request content and metadata, including labels, milestones, assignees, review state, relationships, comments, and history, are not rewritten by `ghpmv`. Project views show whatever exists on the target issues and pull requests.

## Supported project structure

| Area | Supported? | Notes |
|---|---:|---|
| Organization-owned projects | ✅ | Default mode. |
| User-owned projects | ✅ | Use `--owner-type user` with a classic PAT. GitHub lists user-owned Projects as a current [fine-grained PAT limitation](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens#fine-grained-personal-access-token-limitations). |
| Existing target project | ✅ | Use `ghpmv import --project-number <n>` to merge into a project you created beforehand, for example from a template. |
| Project title override | ✅ | Use `--project-title` when creating the target project. |
| Project description / README / public / closed state | ✅ | Migrated through the GraphQL API. |
| Linked repositories | ✅ | Exported and re-linked during import when the target repository can be resolved through `--repo-mapping`. |
| Project collaborators | ✅ with browser automation / API import-only | GitHub exposes a write API but no read API for project collaborators. With `--enable-browser-automation`, `ghpmv` exports explicitly listed project collaborators from Settings → Manage access and imports them through the API. Inherited/base-role access is outside `ghpmv`'s scope and is expected to come from GEI, organization/team/repository settings, or enterprise policy. |
| Project status updates | ✅ | Body, status, start/target dates, and history order are migrated. Because GitHub cannot preserve the original author or creation time, `ghpmv` prepends them to the recreated body. |
| Project-to-Team links | ✅ for organization-owned Projects | Exported through `ProjectV2.teams`, stored as `organization/slug`, resolved before any write, and recreated with `linkProjectV2ToTeam`. Explicit Team collaborators remain separate. Target-only links are reported but not removed. User-owned Projects use empty/no-op/not-applicable behavior. |
| Project templates | ✅ for organization-owned Projects | `ProjectV2.template` is exported and applied only after Items, Status Updates, Views, Workflows, and other writers finish. Existing template targets are temporarily unmarked when a blocked write is required; user-owned Projects reject `template: true` before any write. |

## Fields and field values

| Area | Supported? | Notes |
|---|---:|---|
| Text fields and values | ✅ | Includes Unicode, emoji and special characters. |
| Number fields and values | ✅ | Includes decimals, negative values and zero. |
| Date fields and values | ✅ | |
| Single-select fields and values | ✅ | Option name, color and description are migrated. |
| Custom Text / Number / Single-select field defaults | ✅ with browser automation | Public REST/GraphQL APIs do not expose defaults. Browser-assisted import first neutralizes captured target defaults, imports every source item, then applies configured or cleared source defaults; Single-select defaults are remapped by option name. This prevents existing defaults and skipped-item resumes from populating source fields that were unset. Built-in Status is excluded and remains governed by Project workflows. API-only import cannot guarantee an existing target default when Single-select option reconciliation removes its option. Date defaults are not supported by GitHub. |
| Multi-select Project fields and values | ✅ | Option name, color and description are migrated. Selected options are remapped by name and restored through GraphQL. |
| Organization Issue Fields, including multi-select | ✅ | GraphQL exposes `isIssueField` and the linked `issueField` definition directly on Project fields, so API-only export preserves hidden or unset linked fields and distinguishes same-name ordinary fields. Existing same-name target Issue Fields are updated to match the snapshot because they are organization-wide. User-owned Projects cannot host organization Issue Fields. |
| Iteration fields and values | ✅ | Active and completed iterations are migrated. Completed iterations are recreated by using past dates. |
| Built-in fields such as Title / Assignees / Repository / Labels / Milestone | ✅ for project/view configuration | Built-in fields are not recreated as custom fields. `ghpmv` preserves their use in project views where GitHub exposes them, but Issue/PR metadata values come from the target issues and pull requests, usually migrated by GEI. Draft issue title and draft assignees are handled separately. |
| Field value history | ❌ | GitHub has no API to write historical field changes. Only current values are migrated. |

GitHub added `isIssueField` and `issueField` to the Project field model on
[2026-08-12](https://docs.github.com/en/graphql/overview/changelog/2026#schema-changes-for-2026-08-12).
`ghpmv` reads this identity and the linked definition directly from the Project field
connection. Item values are still read from `ProjectV2ItemIssueFieldValue`, but field
discovery no longer depends on an item having a value or on browser page data.

## Project items

| Area | Supported? | Notes |
|---|---:|---|
| Draft issues | ✅ | Original author and timestamp are added as a note at the top of the body. |
| Draft issue assignees | ✅ | Use `--user-mapping` when logins differ, especially for EMU targets. Unmapped users are dropped with a warning. |
| Issues | ✅ | Re-linked through `--repo-mapping`. The target account must be able to see the target repository and item. The target repository is expected to contain the same issue number as the source, which is the normal GEI outcome. |
| Pull requests | ✅ | Same repository mapping, visibility and same-number requirements as issues. |
| Item field values | ✅ | Text, number, date, single-select, Project and Issue Field multi-select, iteration and Status values are restored. |
| Item order | ✅ | Restored for non-archived items in the project-level order exposed by GitHub. |
| Archived items | ✅ | Archived state is restored after values are applied. Archived item position is not restored because GitHub does not allow moving archived items. |
| Redacted / inaccessible items | ❌ | If GitHub hides an item from the exporting user, `ghpmv` cannot migrate it. |

## Views

View names, layouts, filters, and ordered visible fields are imported through the public [`createProjectV2View` and `updateProjectV2View` GraphQL mutations](https://docs.github.com/en/graphql/reference/projects#createprojectv2view). Full-fidelity Views still require `--enable-browser-automation` for settings that the mutation inputs do not expose.

| Area | Supported? | Notes |
|---|---:|---|
| Table views | ✅ / best effort | Name, layout, filter, and visible-field order use GraphQL. Group by, the first sort key, Slice by, and grouped-view Field sum use browser enrichment. Field sums preserve Count, multiple Number fields, and an empty selection. Additional sort keys are exported but only the first is applied. |
| Board views | ✅ | Column by, Swimlanes and Field sum are tested. |
| Roadmap views | ✅ | Date fields, zoom level, markers, title truncation, date-field display, and grouped-view Field sum are tested. |
| View API settings | ✅ | Name, layout, filter, and ordered visible fields are migrated without browser automation. |
| View UI-only settings | ✅ | Grouping, sorting, slicing, field sums, and Roadmap settings are exported/imported by browser automation where the UI exposes them. Roadmap checkbox failures identify the property and View. |
| View tab order | ✅ with browser automation | The public GraphQL `POSITION` order can differ from the saved-tab order shown by GitHub. Browser-assisted export/verify read tab `href` values in DOM order, and browser-assisted import applies the minimum drag-and-drop moves after all View settings. API-only export leaves tab order uncaptured, API-only import warns when a snapshot contains it, and API-only verify marks it not verified. |
| Insights charts | ❌ | Out of scope for v1. They require a separate UI automation design. |

## Workflows

Workflows require `--enable-browser-automation` because GitHub has no public API to create or update them.

| Area | Supported? | Notes |
|---|---:|---|
| Built-in item-state workflows | ✅ | Examples: item closed, pull request merged, item added to project. Status bindings are reapplied after field migration. |
| Auto-add workflows | ✅ / best effort | Repository and filter settings are migrated. The importer handles up to 20 instances; a lower target-plan limit is surfaced as a browser-import warning when GitHub rejects an additional workflow. |
| Auto-archive workflows | ✅ / best effort | Supported where the current GitHub UI exposes the filter in a stable way. |
| Duplicated Auto-add workflows | ✅ | Tested with two Auto-add workflows. |
| Disabled workflows | ✅ | Saved disabled workflows are migrated by saving once and toggling off when needed. |
| Unsaved default workflows that have never been configured | ⚠️ | GitHub shows some default workflows in the sidebar before they exist in GraphQL. `ghpmv` can configure or skip them, but they are not part of the GraphQL-only snapshot until saved. |

## Verification and safety

| Area | Supported? | Notes |
|---|---:|---|
| `ghpmv verify` | ✅ | Compares the target project against the snapshot. GraphQL-readable View settings are checked by default and whenever the `View` category is selected; `--enable-browser-automation` additionally re-reads saved-tab order, UI-only View / Workflow settings, and explicit collaborators. `--categories` limits comparison, API follow-up queries, and browser reads to selected categories. Supports category statuses, warning exit policy, and JSON reports. |
| Resume after interruption | ✅ | Item and status-update import write target node IDs to `import-log.json` immediately. Reruns use those IDs rather than content matching, so legitimate repeated status bodies are preserved without duplication. An ambiguous Status Update create that did not persist its target ID keeps durable pending state and requires manual reconciliation instead of claiming a body/status/date match. |
| Mapping CSV templates | ✅ | `export` writes repository, organization, Team, and user mapping templates without overwriting existing files. Team rows use `organization/slug` on both sides. |
| Bulk export | ✅ | Omit `--project` to export every project owned by the organization/user into `<out>/<number>/`. |
| Update check opt-out | ✅ | Use `--no-update-check` or `GHPMV_NO_UPDATE_CHECK`. No telemetry is sent. |

## Migration prerequisites

1. **Prepare migration tokens.** Follow the command-specific [Token permissions](../README.md#token-permissions). `ghpmv setup --fixture` and its broader test-fixture permissions are not required to migrate an existing Project.
2. **Move or create the target repositories first.** Use GitHub Enterprise Importer or another migration tool for repository contents, issues, pull requests, and their metadata. `ghpmv` resolves linked items by repository mapping plus the same issue or pull request number.
3. **Generate a snapshot** with `ghpmv export`.
4. **Fill in `repository-mappings.csv`.** Every Issue / PR Project item needs a source repository mapped to a target repository visible to the target token.
5. **Fill in `organization-mappings.csv`.** Browser-assisted import requires every `org:` filter value to resolve before it writes the Project. Organization mappings can be inferred from repository owners when the repository mappings make the result unambiguous.
6. **Fill in `user-mappings.csv` if generated.** This is important for Enterprise Managed Users, where target logins usually have a `_shortcode` suffix.
7. **Review `team-mappings.csv` if generated.** Blank targets keep the source slug in the target organization. Fill `target-org/new-slug` for renamed Teams. Import stops before any Project write when a Team is unresolved, a mapping is many-to-one, Team read permission is missing, or the existing Project is not updatable.
8. **Enable browser automation only when needed.** Run `ghpmv setup --browsers` and `ghpmv login`, then pass `--enable-browser-automation` to export, import, and verify when Views or Workflows must be fully migrated and checked.

Pass the same repository, user, organization, and Team mappings to `ghpmv verify`. If browser automation is disabled, `ghpmv` still migrates projects, fields, items, values, ordering, archived state, and linked repositories, but Views and Workflows are not fully recreated.

## Unsupported areas

| Area | Why not? | Workaround |
|---|---|---|
| GitHub Enterprise Server (GHES) | `ghpmv` is designed for GitHub.com and GHEC with data residency. | Use `gh-migrate-project` for GHES scenarios. |
| Migrating repositories, issues, pull requests or their metadata | This is outside the Projects API. | Use GitHub Enterprise Importer first, then map the resulting repositories. `ghpmv` expects Issue / PR numbers to be preserved for Project item relinking. |
| Original author and creation timestamp for draft issues | GitHub always attributes newly-created draft issues to the importing user. | `ghpmv` prepends a note with the original metadata. |
| Item history / field history | GitHub has no API to recreate historical field changes. | Only the current state is migrated. |
| Exporting inherited/base-role project access | GitHub separates explicit project collaborators from inherited access. | Handle inherited access through GEI and the target organization/team/repository/enterprise policy model. |
| Insights charts | No public API; UI is more complex than Views/Workflows. | Future v2 candidate. |
| Redacted items | The exporting account cannot see them. | Export with an account that has access to all project content. |

## Browser automation limitations

- The module is opt-in and uses your own interactive session stored locally in `%APPDATA%/ghpmv/browser-state*.json`. Nothing is sent anywhere except to GitHub itself.
- Automating the web UI is not covered by the public API's stability guarantees. You are responsible for using it consistently with the [GitHub Terms of Service](https://docs.github.com/site-policy/github-terms/github-terms-of-service); `ghpmv` performs low-rate, human-scale, sequential operations against your own Projects and does not scrape other users' data.
- UI selectors can break when GitHub updates the Projects UI. Recoverable failures are warnings, and browser writes are not transactional. Always run browser-assisted `ghpmv verify` afterward.
- View tab ordering is a recoverable, non-transactional browser write. A failed drag leaves any successful earlier moves in place and emits a warning; rerun browser-assisted import or reorder manually, then verify.

### Auto-add workflow limits per plan

The target organization's plan limits the number of Auto-add workflows per project. `ghpmv` has a hard maximum of 20; a lower target-plan limit is reported as a warning.

| Plan | Auto-add workflows per project |
|---|---|
| Free | 1 |
| Pro / Team | 5 |
| Enterprise (GHEC) | 20 |
