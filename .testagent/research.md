# Test Generation Research

## Project Overview
- **Path**: `C:\Users\shodaiikebe\copilot-worktrees\ghpmv\sikebe-cuddly-succotash`
- **Language**: C# / .NET 10 (`global.json` requests SDK `10.0.400`, `latestFeature`)
- **Framework**: .NET 10, GitHub GraphQL and REST APIs
- **Test Framework**: xUnit v3 `4.0.0` on Microsoft Testing Platform; `global.json` selects `Microsoft.Testing.Platform`
- **Project system**: SDK-style
- **Dependency format and versions**: Central `PackageReference` versions in `Directory.Packages.props`: `xunit.v3` 4.0.0, `xunit.runner.visualstudio` 4.0.0, `Microsoft.NET.Test.Sdk` 18.9.0, `coverlet.collector` 10.0.1. No mocking package is needed for live tests.
- **New-file registration**: Implicit SDK `Compile` glob; no explicit `<Compile Include>` is required.
- **Live-test configuration**: `GHPMV_TEST_TOKEN`; organizations/repositories come through `IntegrationTestSettings` / `E2eTestEnvironment`, including `GHPMV_TEST_ORG`, `GHPMV_TEST_TARGET_ORG`, `GHPMV_TEST_FIXTURE_REPO`, and `GHPMV_TEST_TARGET_FIXTURE_REPO`.
- **Suite execution**: `TestAssembly.cs` disables parallel execution for the Integration assembly.

## Dependency Graph
- **Leaf transport types** (no target-domain dependency): `GitHubRestClient` (REST GET/POST/PUT/DELETE and validation probe), `GitHubGraphQLClient` (query/mutation and cursor pagination).
- **Mid-layer targets**:
  - `ImportCapabilityPreflight` depends on `ImportCapabilityPlan`/`RepositoryCapabilityRequirement` and `GitHubRestClient`.
  - `ProjectExporter` depends on `GitHubGraphQLClient` and emits `ProjectSnapshot`.
  - `ItemImporter` depends on `GitHubGraphQLClient`, `ProjectSnapshot`, `ImportResult`, and durable `ImportLog`.
- **Top-layer target**: `ProjectImporter`; its `BeforeWriteAsync` hook is called immediately before the first Project mutation and is where the CLI wires `ImportCapabilityPreflight.ValidateAsync`.
- **Composition-only dependency**: `src/Ghpmv.Cli/Program.cs` analyzes capabilities, creates the production preflight callback, and assigns it to `ProjectImporter.BeforeWriteAsync`. Do not process-test the CLI for this scope.
- **Test helpers**: `IntegrationTestSettings` creates correctly hosted GraphQL/REST clients; `TemporaryProjectFixture` creates/deletes Projects; no disposable-repository helper currently exists.

## Build & Test Commands
- **Restore**: `dotnet restore Ghpmv.slnx`
- **Build**: `dotnet build Ghpmv.slnx -c Release --no-restore -warnaserror`
- **Test (scoped — fix cycles)**:
  `dotnet test --project tests/Ghpmv.Integration.Tests/Ghpmv.Integration.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~Ghpmv.Integration.Tests.ItemImporterTests.Pull_request_item_is_relinked_to_the_target_repository_with_its_number_preserved|FullyQualifiedName~Ghpmv.Integration.Tests.ImportCapabilityPreflightIntegrationTests|FullyQualifiedName~Ghpmv.Integration.Tests.GraphQLClientIntegrationTests.QueryPaginatedAsync_and_ProjectExporter_enumerate_all_items_across_real_pages|FullyQualifiedName~Ghpmv.Integration.Tests.ProjectViewImporterIntegrationTests.Import_and_verify_use_graphql_tab_positions_while_api_only_update_warns"`
- **Test (harness-equivalent — discovery check)**: `dotnet test --project tests/Ghpmv.Integration.Tests/Ghpmv.Integration.Tests.csproj -c Release --no-build --list-tests`
- **Credentialed CI execution**: `dotnet test --project tests/Ghpmv.Integration.Tests/Ghpmv.Integration.Tests.csproj -c Release --no-build --results-directory tests/Ghpmv.Integration.Tests/TestResults/ci --report-xunit-trx --report-xunit-trx-filename integration.trx`
- **CI skip policy**: `.github/workflows/live-api.yml` parses the TRX and fails if any credentialed test is `NotExecuted`.
- **Lint**: No C#-specific lint command; the Release build treats warnings as errors. `ghalint run` is only for workflow changes and is out of scope.

## Scope
- **Boundary**: Credentialed Integration E2E generation only for (1) pull-request item relinking/number preservation, (2) production import capability preflight and before-write safety, and (3) production `ProjectExporter` connection behavior for View `POSITION` plus a feasible page-boundary connection. Do not inventory or modify unrelated source/test trees.
- **Source targets**:
  - `src/Ghpmv.Core/Import/ItemImporter.cs`
  - `src/Ghpmv.Core/Import/ImportCapabilityPreflight.cs`
  - `src/Ghpmv.Core/Import/ProjectImporter.cs` (`BeforeWriteAsync` ordering only)
  - `src/Ghpmv.Core/Export/ProjectExporter.cs`
- **Transport dependencies, not separate targets**:
  - `src/Ghpmv.Core/GitHub/GitHubRestClient.cs`
  - `src/Ghpmv.Core/GitHub/GitHubGraphQLClient.cs`
  - `src/Ghpmv.Core/Import/ImportCapabilityAnalyzer.cs`
- **Authoritative branch work to preserve**: `tests/Ghpmv.Integration.Tests/ProjectViewImporterIntegrationTests.cs`, especially `Import_and_verify_use_graphql_tab_positions_while_api_only_update_warns`. The branch is `sikebe-preserve-view-tab-order`; do not reset, restore, clean, or replace its tree.
- **Representative existing tests**:
  - `tests/Ghpmv.Integration.Tests/ItemImporterTests.cs`
  - `tests/Ghpmv.Integration.Tests/ProjectViewImporterIntegrationTests.cs`
- **Static pairing result**: The required Roslyn `find-untested-sources` run completed once in this session: 50 source files, 51 test files, 44 paired and 6 unpaired. The API-facing target files are paired to deterministic and/or Integration tests; operation-level gaps are documented below. `code-testing-extensions` is unavailable; conventions below come from bounded repository files.

## Files to Test

### High Priority
| File | Classes/Functions | Testability | Estimated Coverage | Notes |
|------|-------------------|-------------|-------------------|-------|
| `src/Ghpmv.Core/Import/ItemImporter.cs` | `ItemImporter.ImportAsync`; PR branch of `CreateContentItemAsync` / `ResolveIssueOrPullRequestIdAsync` | High with disposable repos/Projects | Partial | Existing live test deliberately removes `PULL_REQUEST`; add the missing real PR relink case. |
| `src/Ghpmv.Core/Import/ImportCapabilityPreflight.cs` | `ValidateAsync`, repository role validation | High with configured fixture repo/org | Partial | Deterministic unit coverage is substantial, but no real REST integration coverage exists. |
| `src/Ghpmv.Core/Import/ProjectImporter.cs` | `BeforeWriteAsync` placement in `ImportAsync` | High | Partial for this contract | Use a failing production preflight callback and prove no target Project was created. |
| `src/Ghpmv.Core/Export/ProjectExporter.cs` | `ExportAsync`, `FetchViewsAsync`, `FetchItemsAsync` | High | Partial | Stub tests cover View cursor continuation; live tests cover POSITION but not exporter page-boundary completeness. Reuse the existing 120-item live fixture. |

### Medium Priority
| File | Classes/Functions | Testability | Estimated Coverage | Notes |
|------|-------------------|-------------|-------------------|-------|
| `src/Ghpmv.Core/GitHub/GitHubRestClient.cs` | `GetAsync`, `PostValidationProbeAsync`, `DeleteAsync` | High | Substantial | Exercised transitively; do not create a separate transport test. |
| `src/Ghpmv.Core/GitHub/GitHubGraphQLClient.cs` | `QueryPaginatedAsync` | High | Substantial | Existing live test already crosses pages with 120 items; extend its fixture assertions instead of creating another expensive 51-item Project. |

### Low Priority / Skip
| File | Reason |
|------|--------|
| `src/Ghpmv.Cli/Program.cs` | Composition was inspected to confirm production wiring. A spawned CLI migration would duplicate the direct production callback/ProjectImporter contract and complicate secret/process handling. |
| `src/Ghpmv.Core/Import/ProjectViewImporter.cs` | Existing Issue #50 live test is authoritative for creation/update behavior; preserve it rather than replacing it. |
| Other source/test files | Outside the three requested scenarios. |

## Existing Tests & Coverage Classification
- `ItemImporter.cs` ↔ `tests/Ghpmv.Integration.Tests/ItemImporterTests.cs`: **partial**. It covers drafts, Issue mapping, warnings, ordering, and resume, but lines 55–58 explicitly filter out `PULL_REQUEST`.
- `ItemImporter.cs` ↔ `tests/Ghpmv.Core.Tests/ItemImporterLogicTests.cs` and `ItemImporterResumeTests.cs`: substantial deterministic branch/resume coverage, but not a real target PR lookup/add/read-back.
- `ImportCapabilityPreflight.cs` ↔ `tests/Ghpmv.Core.Tests/ImportCapabilityTests.cs`: **substantial deterministic / untested live**. Unit tests cover accepted 422 probes, classic PAT header omission, members read, missing/cross-owner mappings, repository roles, and early rejection. No Integration test calls the production method.
- `ProjectImporter.cs` ↔ `tests/Ghpmv.Core.Tests/ProjectImporterLogicTests.cs`: **partial for live before-write safety**. A fake callback failure is covered; no real REST preflight is attached to a real import attempt.
- `ProjectExporter.cs` ↔ `tests/Ghpmv.Core.Tests/ProjectExporterTests.cs`: **substantial deterministic**. It includes `Export_paginates_position_ordered_views_without_resetting_tab_positions` and field/team pagination stubs.
- `ProjectExporter.cs` ↔ `tests/Ghpmv.Integration.Tests/ProjectExporterTests.cs`: **partial live**. Fixture export checks PR recognition and three Views, but not a production page boundary.
- `ProjectExporter.cs` / `GitHubGraphQLClient.cs` ↔ `tests/Ghpmv.Integration.Tests/GraphQLClientIntegrationTests.cs`: **partial**. `QueryPaginatedAsync_enumerates_120_items_across_real_pages` already creates the ideal >50 item fixture but only asserts the generic client IDs.
- View POSITION contract ↔ `tests/Ghpmv.Integration.Tests/ProjectViewImporterIntegrationTests.cs`: **substantial for the requested real-API POSITION contract**. The new branch test asserts exact names in POSITION order and contiguous `TabPosition` values `0..2`, then verifies mismatch reporting after an API-only reorder attempt.

## Existing Test Projects
- **Project file**: `tests/Ghpmv.Integration.Tests/Ghpmv.Integration.Tests.csproj`
  - **Target source project**: `src/Ghpmv.Core/Ghpmv.Core.csproj`
  - **Relevant test files**: `ItemImporterTests.cs`, `ProjectExporterTests.cs`, `GraphQLClientIntegrationTests.cs`, `ProjectViewImporterIntegrationTests.cs`
  - **Relevant helpers**: `IntegrationTestSettings.cs`, `TemporaryProjectFixture.cs`, `TestAssembly.cs`
- **Project file**: `tests/Ghpmv.Core.Tests/Ghpmv.Core.Tests.csproj`
  - **Target source project**: `src/Ghpmv.Core/Ghpmv.Core.csproj`
  - **Relevant paired tests only**: `ImportCapabilityTests.cs`, `ItemImporterLogicTests.cs`, `ItemImporterResumeTests.cs`, `ProjectExporterTests.cs`, `ProjectImporterLogicTests.cs`

## Testing Patterns
- `[Fact]` async methods with descriptive underscore names; no extra E2E trait in the Integration project.
- Token accessor uses only `Assert.SkipWhen(string.IsNullOrWhiteSpace(token), ...)`. Do not skip for missing permissions, organization policy, repository creation failure, rate limiting, or an unexpected API contract.
- Capture `TestContext.Current.CancellationToken` at test start and pass it to all ordinary async operations.
- Use unique names from `Guid.NewGuid().ToString("N")`; obtain organizations and fixture repositories through `IntegrationTestSettings`, never literals.
- All created GitHub resources are owned by the test and deleted in `finally` with `CancellationToken.None`. Project deletion by unique title (`TemporaryProjectFixture.DeleteAllByTitleAsync`) is a useful defensive cleanup if creation partially succeeds.
- API writes are serial to avoid GitHub secondary rate limits. Eventual consistency is handled by bounded polling (`10–16` attempts, `2–5` second delays), not arbitrary unconditional sleeps.
- The Integration assembly is nonparallel. Use production `IntegrationTestSettings.CreateClient(Token)` and `CreateRestClient(Token)` so GitHub.com/GHEC endpoints remain configuration-driven.
- For multiple cleanup operations, the strongest existing convention is `TeamLinkRoundTripTests`: retain the original test failure, attempt every cleanup, collect cleanup failures, and then rethrow/report. At minimum, cleanup must not be omitted when setup partially succeeds.

## Proposed Test Design

### 1. Pull Request item import/relink
- **Placement/name**: add `ItemImporterTests.Pull_request_item_is_relinked_to_the_target_repository_with_its_number_preserved`.
- Add narrowly scoped private helper code (or one internal `DisposableRepositoryFixture` if reused) because no repository lifecycle helper exists.
- For both configured `SourceOrg` and `TargetOrg`, create a uniquely named private repository through `GitHubRestClient`. Mirror the proven fixture sequence: create repo, PUT `README.md` for the initial commit, read default branch/ref, create a unique head ref, PUT one file on that branch, then POST `/pulls`.
- Create no Issues before either PR. Issues and PRs share numbering; assert the returned source and target PR numbers are equal before testing relinking. Fresh disposable repositories should produce matching `#1`, but the assertion—not a hardcoded number—is authoritative.
- Create a disposable source Project, resolve the source PR node ID, add it with `addProjectV2ItemById`, and export until the source contains exactly the expected `PULL_REQUEST`.
- Import a uniquely titled target Project, then call production `ItemImporter` with `{sourceRepoFullName -> targetRepoFullName}` and a unique log directory.
- Export the target until visible and assert: exactly one content item; `Type == "PULL_REQUEST"`; repository is the target full name; number equals both returned PR numbers; result has `Created == 1`, `Skipped == 0`, and no warning for that item.
- Finally delete both Projects, both repositories (`repos/{owner}/{name}`), and operation/log directories with `CancellationToken.None`. Never delete configured long-lived fixture repositories.

### 2. Production import capability preflight
- **Placement**: new `tests/Ghpmv.Integration.Tests/ImportCapabilityPreflightIntegrationTests.cs`.
- **Success test**: call `ImportCapabilityPreflight.ValidateAsync` directly with real `IntegrationTestSettings.CreateRestClient(Token)`, target org, a mapping to `TargetFixtureRepositoryFullName`, `RequiresOrganizationAdministrator: true`, `RequiresMembersRead: true`, and repository capabilities including `MetadataRead`, `IssuesWrite`, `ContentsWrite`, and `SameOwner`. This performs the validation-only Issue Field POST plus real `GET /orgs/{org}/teams?per_page=1` and `GET /repos/{owner}/{repo}`, then validates the returned role.
- **Safe-failure test**: create a minimal snapshot with a unique target title and configure real `ProjectImporter.BeforeWriteAsync` to call `ImportCapabilityPreflight.ValidateAsync` with a mapping to a unique nonexistent target repository. Assert the preflight exception, then use `ProjectExporter.ListProjectsAsync(..., includeClosed: true)` to prove no Project with that title exists. Defensive title cleanup still belongs in `finally`.
- This tests “before remote write,” not “before local operation-log write”: `ProjectImporter` may persist local resumability state before invoking the callback.

### 3. Connection-specific pagination
- **View POSITION**: preserve and continue running `ProjectViewImporterIntegrationTests.Import_and_verify_use_graphql_tab_positions_while_api_only_update_warns`. It already exercises production `ProjectExporter` against the real `views(orderBy: { field: POSITION, direction: ASC })` contract and asserts exact count/order and contiguous `TabPosition`. The bounded repository evidence does not establish that creating more than 50 Views is a feasible live fixture; cursor continuation is already deterministic in Core tests.
- **Feasible >page-size connection**: reuse `GraphQLClientIntegrationTests`' existing disposable Project with 120 draft items (production `ItemsPageSize` is 50). Do not create a second expensive 51/120-item fixture.
- Extend/rename that test to retain each item title from the direct paginated connection, obtain the created Project number, then poll `new ProjectExporter(client).ExportAsync(...)` until 120 items are visible.
- Assert exact count `120`, no duplicate/missing titles, exporter title sequence equals the direct connection sequence, and exporter `Position` is exactly `0..119`. This proves no truncation across three production pages without assuming insertion order.

## Explicit Requirement Checklist
- [ ] Use only `GHPMV_TEST_TOKEN`; skip only when it is absent.
- [ ] Use `TestContext.Current.CancellationToken` for test work and `CancellationToken.None` for every cleanup.
- [ ] Use configured source/target organizations and API endpoints; no hardcoded org, host, repository, Project number, or PR number.
- [ ] PR test owns unique source/target repositories and Projects and cleans all of them in `finally`.
- [ ] Assert source/target PR numbers match before import; assert target export is `PULL_REQUEST` in the mapped target repo with that preserved number.
- [ ] Call production `ImportCapabilityPreflight.ValidateAsync`, not a reimplemented HTTP check.
- [ ] Success preflight performs real repository visibility/role and Team/Members-read REST calls.
- [ ] Failure preflight is safe/read-only and is attached through production `ProjectImporter.BeforeWriteAsync`; assert no target Project was created.
- [ ] Preserve the existing Issue #50 View test and its authoritative POSITION/TabPosition assertions.
- [ ] Exercise another production `ProjectExporter` connection beyond page size 50 using the existing 120-item fixture; assert complete count, exact observed order, contiguous positions, and no truncation.
- [ ] Keep API writes serial and poll boundedly for eventual consistency.
- [ ] Do not restore/reset/clean the branch or overwrite existing tests/helpers.
- [ ] Run the scoped filter during fixes, then the whole credentialed Integration project.
- [ ] Run harness discovery from repo root and verify all new tests are listed.
- [ ] A credentialed whole-project run must contain zero skipped tests.

## Recommendations
1. Add the preflight Integration class first; it is read-only and validates the credential contract cheaply.
2. Add the disposable PR relink test and its tightly bounded repository helper next.
3. Extend the existing 120-item live pagination test rather than adding another high-write fixture.
4. Preserve and include the branch’s existing View POSITION test in the scoped and whole-project runs.
5. Update credential documentation if repository create/delete in both configured organizations is a newly enforced CI prerequisite. Do not convert permission/policy failures into skips.
