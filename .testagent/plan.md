# Test Implementation Plan

## Overview
Add credentialed Integration E2E coverage for exactly three requirements: production capability preflight safety, pull-request item relinking with number preservation, and production exporter connection behavior. Use the targeted strategy because the source targets already have substantial deterministic or partial live coverage. Implement in dependency order: read-only preflight and the `ProjectImporter.BeforeWriteAsync` contract, PR import/relink, then the existing View POSITION and 120-item pagination fixtures.

All work stays in `tests/Ghpmv.Integration.Tests/Ghpmv.Integration.Tests.csproj`. Do not create another test project, process-test the CLI, add standalone transport tests, or modify unrelated source/tests. Preserve the current workspace and `ProjectViewImporterIntegrationTests.Import_and_verify_use_graphql_tab_positions_while_api_only_update_warns` exactly; do not reset, restore, clean, replace, or overwrite branch work.

## Commands
- **Restore**: `dotnet restore Ghpmv.slnx`
- **Build**: `dotnet build Ghpmv.slnx -c Release --no-restore -warnaserror`
- **Test (scoped fix cycle)**: `dotnet test --project tests/Ghpmv.Integration.Tests/Ghpmv.Integration.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~Ghpmv.Integration.Tests.ItemImporterTests.Pull_request_item_is_relinked_to_the_target_repository_with_its_number_preserved|FullyQualifiedName~Ghpmv.Integration.Tests.ImportCapabilityPreflightIntegrationTests|FullyQualifiedName~Ghpmv.Integration.Tests.GraphQLClientIntegrationTests.QueryPaginatedAsync_and_ProjectExporter_enumerate_all_items_across_real_pages|FullyQualifiedName~Ghpmv.Integration.Tests.ProjectViewImporterIntegrationTests.Import_and_verify_use_graphql_tab_positions_while_api_only_update_warns"`
- **Discovery**: `dotnet test --project tests/Ghpmv.Integration.Tests/Ghpmv.Integration.Tests.csproj -c Release --no-build --list-tests`
- **Credentialed whole project**: `dotnet test --project tests/Ghpmv.Integration.Tests/Ghpmv.Integration.Tests.csproj -c Release --no-build --results-directory tests/Ghpmv.Integration.Tests/TestResults/ci --report-xunit-trx --report-xunit-trx-filename integration.trx`
- **Lint**: No separate C# lint command; the Release build uses `-warnaserror`.

## Shared Conventions
- Read only `GHPMV_TEST_TOKEN`; use `Assert.SkipWhen` only when it is absent or blank. Permission, policy, rate-limit, repository-creation, and API-contract failures must fail.
- Capture `TestContext.Current.CancellationToken` at each test's start and pass it to ordinary async work. Pass `CancellationToken.None` to every cleanup operation.
- Create clients only through `IntegrationTestSettings.CreateClient(Token)` and `CreateRestClient(Token)`. Read organizations and fixture repositories from `IntegrationTestSettings`; never embed a host, organization, repository, Project number, or PR number.
- Generate owned resource names with `Guid.NewGuid().ToString("N")`. Keep API writes serial.
- Handle eventual consistency with bounded polling (10–16 attempts and 2–5 second delays), never an unconditional sleep.
- Put resource deletion in `finally`. Attempt every cleanup while retaining the original failure; collect/report cleanup failures using the established `TeamLinkRoundTripTests` convention. Use defensive Project cleanup by unique title when creation may have partially succeeded.

## Phase Summary
| Phase | Focus | Target source files | Test files | Est. Tests |
|---|---|---:|---:|---:|
| 1 | Production preflight and before-write safety | 2 | 1 new | 2 |
| 2 | Real PR relinking and number preservation | 1 | 1 existing | 1 |
| 3 | View POSITION preservation and exporter pagination | 1 | 2 existing, 1 preserved | 1 extended + 1 preserved |

---

## Phase 1: Production Capability Preflight and Before-Write Safety

### Overview
Start with the cheapest leaf-facing live contract. Exercise the production REST preflight directly, then attach that same production method to the top-layer importer and prove a failed check occurs before the first remote Project write.

### Files to Test

#### 1. ImportCapabilityPreflight.cs
- **Source**: `src/Ghpmv.Core/Import/ImportCapabilityPreflight.cs`
- **Test File**: `tests/Ghpmv.Integration.Tests/ImportCapabilityPreflightIntegrationTests.cs` (new)
- **Test Class**: `ImportCapabilityPreflightIntegrationTests`

**Methods to Test**:
1. `ValidateAsync` — `Production_preflight_validates_organization_repository_and_members_capabilities`
   - Obtain the token, target organization, and `TargetFixtureRepositoryFullName` through `IntegrationTestSettings`.
   - Build an `ImportCapabilityPlan` with a real mapping to the configured target fixture repository, `RequiresOrganizationAdministrator: true`, `RequiresMembersRead: true`, and requirements for `MetadataRead`, `IssuesWrite`, `ContentsWrite`, and `SameOwner`.
   - Call production `ImportCapabilityPreflight.ValidateAsync` with `CreateRestClient(Token)`.
   - Assert the call completes without exception, thereby exercising the validation-only organization Issue Field probe, real repository visibility/role validation, and `GET /orgs/{org}/teams?per_page=1`.

#### 2. ProjectImporter.cs
- **Source**: `src/Ghpmv.Core/Import/ProjectImporter.cs` (`BeforeWriteAsync` ordering only)
- **Test File**: `tests/Ghpmv.Integration.Tests/ImportCapabilityPreflightIntegrationTests.cs` (new)
- **Test Class**: `ImportCapabilityPreflightIntegrationTests`

**Methods to Test**:
1. `ImportAsync` / `BeforeWriteAsync` — `Failed_production_preflight_runs_before_first_project_write`
   - Create a minimal `ProjectSnapshot` with a unique target title and a unique nonexistent target repository mapping.
   - Configure the real `ProjectImporter.BeforeWriteAsync` callback to invoke production `ImportCapabilityPreflight.ValidateAsync`; do not reproduce its HTTP checks in test code.
   - Invoke `ProjectImporter.ImportAsync` and assert the production preflight exception identifies the invalid/unavailable repository capability.
   - Query `ProjectExporter.ListProjectsAsync(..., includeClosed: true)` and assert no Project with the unique title exists.
   - Do not assert that no local operation-log state was written; the contract is no remote Project write.
   - In `finally`, call `TemporaryProjectFixture.DeleteAllByTitleAsync` with `CancellationToken.None` as defensive cleanup and delete any unique operation/log directory with `CancellationToken.None`.

### Fixtures and Helpers
- Reuse `IntegrationTestSettings` for token, endpoints, target organization, and fixture repository.
- Reuse `TemporaryProjectFixture.DeleteAllByTitleAsync` for defensive title cleanup.
- Add only private snapshot/plan factory methods in the new class if they remove duplication; no new general fixture is needed.

### Scoped Verification
1. Build with the repository Release command.
2. Run:
   `dotnet test --project tests/Ghpmv.Integration.Tests/Ghpmv.Integration.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~Ghpmv.Integration.Tests.ImportCapabilityPreflightIntegrationTests"`
3. Confirm both exact test names pass with credentials and are skipped only when `GHPMV_TEST_TOKEN` is absent.
4. Run `--list-tests` and confirm both tests are discovered.

### Success Criteria
- [ ] Production `ValidateAsync` performs live repository-role and members-read checks.
- [ ] The failed production callback prevents creation of the uniquely titled target Project.
- [ ] Both tests clean partial local/remote state without masking the original failure.
- [ ] No CLI, transport-only, or unrelated tests are added.

---

## Phase 2: Pull-Request Item Relinking

### Overview
Add the missing real `PULL_REQUEST` branch to the existing `ItemImporter` Integration class using the canonical fixture PR and an identity repository mapping. This exercises PR resolution and relinking without expanding the credential contract or mutating long-lived repositories.

### Files to Test

#### 1. ItemImporter.cs
- **Source**: `src/Ghpmv.Core/Import/ItemImporter.cs`
- **Test File**: `tests/Ghpmv.Integration.Tests/ItemImporterTests.cs` (extend)
- **Test Class**: `ItemImporterTests`

**Methods to Test**:
1. `ImportAsync`, PR branch of `CreateContentItemAsync` / `ResolveIssueOrPullRequestIdAsync` — `Pull_request_item_is_relinked_to_the_target_repository_with_its_number_preserved`
   - Read the canonical `PULL_REQUEST` from `IntegrationFixtureSnapshot.CreateKnownAsync`.
   - Build a minimal snapshot containing only that PR with position zero and no field values.
   - Import to a uniquely titled target Project using production `ItemImporter`, mapping the configured fixture repository to itself, with a unique operation/log directory.
   - Poll production export until the target item is visible.
   - Assert exactly one content item; `Type == "PULL_REQUEST"`; repository equals the configured fixture repository; number equals the source PR number; `ImportResult.Created == 1`; `ImportResult.Skipped == 0`; and no warning is associated with the item.

### Fixtures and Helpers
- Reuse `IntegrationFixtureSnapshot` for the read-only source PR.
- Reuse `TemporaryProjectFixture` for target Project lifecycle and defensive deletion by unique title.
- Add a bounded private polling helper only if existing local polling code cannot express “export until exactly one expected PR appears.”

### Cleanup
- In `finally`, delete the owned target Project and operation/log directories.
- Use `CancellationToken.None` for every cleanup call, attempt all cleanup operations, and retain the original test failure.
- Never delete `SourceFixtureRepositoryFullName`, `TargetFixtureRepositoryFullName`, or any other configured long-lived repository.

### Scoped Verification
1. Build with the repository Release command.
2. Run:
   `dotnet test --project tests/Ghpmv.Integration.Tests/Ghpmv.Integration.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~Ghpmv.Integration.Tests.ItemImporterTests.Pull_request_item_is_relinked_to_the_target_repository_with_its_number_preserved"`
3. Confirm the test passes with credentials, skips only for an absent token, and leaves neither target Project nor local log directory behind.
4. Run `--list-tests` and confirm the exact test name is discovered.

### Success Criteria
- [ ] Read-back proves relinking through the identity repository mapping with type and source number preserved.
- [ ] Import result counts and warning behavior are asserted.
- [ ] Every owned remote and local resource is cleaned in `finally`; shared fixture resources remain read-only.

---

## Phase 3: Production Exporter Connection Contracts

### Overview
Preserve the authoritative Issue #50 View POSITION test and extend the already expensive 120-item fixture to verify `ProjectExporter` across its production item page size of 50. Do not create a >50-View fixture or a second 51/120-item Project.

### Files to Test

#### 1. ProjectExporter.cs
- **Source**: `src/Ghpmv.Core/Export/ProjectExporter.cs`
- **Test File**: `tests/Ghpmv.Integration.Tests/GraphQLClientIntegrationTests.cs` (extend/rename existing 120-item test)
- **Test Class**: `GraphQLClientIntegrationTests`

**Methods to Test**:
1. `ExportAsync` / `FetchItemsAsync` and the existing direct `QueryPaginatedAsync` comparison — `QueryPaginatedAsync_and_ProjectExporter_enumerate_all_items_across_real_pages`
   - Reuse the existing disposable Project and its 120 serially created draft items.
   - Retain each item title from the direct paginated connection in observed connection order and obtain the created Project number from the fixture.
   - Poll `new ProjectExporter(client).ExportAsync(...)` until all 120 items are visible.
   - Assert exact count `120`.
   - Assert the exported title set has no duplicates and no missing titles relative to the 120 created/directly observed titles.
   - Assert the exporter title sequence exactly equals the direct connection sequence; do not assume insertion order.
   - Assert exporter `Position` is exactly the contiguous sequence `0..119`.
   - Retain the existing fixture's `finally` cleanup and use `CancellationToken.None`.

#### 2. Existing View POSITION contract (preserve, do not edit)
- **Source exercised**: `src/Ghpmv.Core/Export/ProjectExporter.cs` (`FetchViewsAsync`)
- **Test File**: `tests/Ghpmv.Integration.Tests/ProjectViewImporterIntegrationTests.cs`
- **Test Class**: `ProjectViewImporterIntegrationTests`
- **Existing Test**: `Import_and_verify_use_graphql_tab_positions_while_api_only_update_warns`

**Assertions to Preserve and Re-run**:
- Exact View count and names in GraphQL `POSITION` order.
- Contiguous `TabPosition` values `0..2`.
- Mismatch reporting after an API-only reorder attempt.
- Existing owned-resource cleanup behavior.

### Fixtures and Helpers
- Reuse the existing 120-draft-item Project setup and its current cleanup; only retain direct titles, expose/use the Project number, and add bounded exporter polling.
- Reuse the existing View fixture unchanged.
- Add no new Project fixture, no >50-View setup, and no transport-only test.

### Scoped Verification
1. Build with the repository Release command.
2. Run:
   `dotnet test --project tests/Ghpmv.Integration.Tests/Ghpmv.Integration.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~Ghpmv.Integration.Tests.GraphQLClientIntegrationTests.QueryPaginatedAsync_and_ProjectExporter_enumerate_all_items_across_real_pages|FullyQualifiedName~Ghpmv.Integration.Tests.ProjectViewImporterIntegrationTests.Import_and_verify_use_graphql_tab_positions_while_api_only_update_warns"`
3. Confirm the exporter test proves completeness/order/positions across three pages and the unchanged View test still passes.
4. Run `--list-tests`; confirm both exact names are discovered and the old pagination-only name is no longer listed if it was renamed.

### Success Criteria
- [ ] The existing Issue #50 View test and assertions remain unchanged.
- [ ] The existing 120-item fixture, not a duplicate fixture, drives exporter verification.
- [ ] Count, uniqueness/completeness, direct observed order, and positions `0..119` are all asserted.
- [ ] Both connection tests pass under the scoped filter.

---

## Final Integration Verification
1. Run the combined scoped fix-cycle command from **Commands**.
2. Run the discovery command from the repository root and verify these exact tests are listed:
   - `Ghpmv.Integration.Tests.ImportCapabilityPreflightIntegrationTests.Production_preflight_validates_organization_repository_and_members_capabilities`
   - `Ghpmv.Integration.Tests.ImportCapabilityPreflightIntegrationTests.Failed_production_preflight_runs_before_first_project_write`
   - `Ghpmv.Integration.Tests.ItemImporterTests.Pull_request_item_is_relinked_to_the_target_repository_with_its_number_preserved`
   - `Ghpmv.Integration.Tests.GraphQLClientIntegrationTests.QueryPaginatedAsync_and_ProjectExporter_enumerate_all_items_across_real_pages`
   - `Ghpmv.Integration.Tests.ProjectViewImporterIntegrationTests.Import_and_verify_use_graphql_tab_positions_while_api_only_update_warns`
3. Run the credentialed whole Integration project with TRX reporting.
4. Inspect `integration.trx` and require zero `NotExecuted`/skipped tests for the credentialed run.
5. Confirm `git diff` contains only the intended new/extended test files and no reset, replacement, or unrelated workspace changes.

## Research Checklist Mapping
| # | Research requirement | Planned implementation / verification |
|---:|---|---|
| 1 | Use only `GHPMV_TEST_TOKEN`; skip only when absent. | Shared Conventions; asserted in every phase's scoped verification. |
| 2 | Work cancellation token; `None` for cleanup. | Shared Conventions plus cleanup sections in Phases 1–3. |
| 3 | Configured orgs/endpoints; no hardcoded identifiers. | Shared Conventions; Phase 1 settings, Phase 2 configured fixture, Phase 3 existing fixtures. |
| 4 | PR test keeps shared fixtures read-only and cleans its target Project. | Phase 2 setup and `finally` cleanup. |
| 5 | Assert mapped target PR read-back preserves the source number. | Phase 2 assertions after export. |
| 6 | Call production `ImportCapabilityPreflight.ValidateAsync`. | Both Phase 1 tests call the production method directly/from `BeforeWriteAsync`. |
| 7 | Real repository role and Team/Members-read calls. | Phase 1 success plan and completion assertion. |
| 8 | Safe read-only failure through `BeforeWriteAsync`; no Project. | Phase 1 failure test and defensive title verification/cleanup. |
| 9 | Preserve Issue #50 View POSITION/TabPosition test. | Phase 3 explicitly marks the file/test unchanged and reruns it. |
| 10 | Exporter connection beyond 50; complete order/positions. | Phase 3 reuses 120 items and asserts 120, uniqueness, equality to direct order, and `0..119`. |
| 11 | Serial writes and bounded eventual-consistency polling. | Shared Conventions and Phase 2/3 setup/read-back steps. |
| 12 | Do not restore/reset/clean or overwrite work. | Overview preservation boundary and final diff check. |
| 13 | Scoped fixes, then whole credentialed project. | Per-phase scoped verification and Final Integration Verification. |
| 14 | Discovery from repository root lists all tests. | Per-phase discovery checks and final exact-name list. |
| 15 | Credentialed whole run has zero skipped tests. | Final TRX run and explicit `NotExecuted` check. |

## Planned File Changes
- **Add** `tests/Ghpmv.Integration.Tests/ImportCapabilityPreflightIntegrationTests.cs`
- **Extend** `tests/Ghpmv.Integration.Tests/ItemImporterTests.cs`
- **Extend/rename one test in** `tests/Ghpmv.Integration.Tests/GraphQLClientIntegrationTests.cs`
- **Preserve without edits** `tests/Ghpmv.Integration.Tests/ProjectViewImporterIntegrationTests.cs`

No production implementation changes are planned.
