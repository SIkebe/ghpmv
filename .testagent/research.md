# Test Generation Research — ProjectV2 Status Updates migration (issue #46)

Research time: 2026-08-16 (JST). Workspace: `C:\Users\shodaiikebe\copilot-worktrees\ghpmv\sikebe-sturdy-fiesta`, worktree of `SIkebe/ghpmv`, HEAD `9a8ea73`.

> **IMPORTANT — the parallel production agent has already landed most of the feature.** At the start of this research `StatusUpdate` had zero matches; by the end, 9 files were modified and 3 new files added (see [§11](#11-production-code-status-update-present-wip-currently-not-compiling)). **`dotnet build` currently FAILS** (61 errors) because `CompareStatusUpdates` was inserted *inside* `CompareItems` in `ProjectVerifier.cs`. Test authoring can proceed against the signatures below; the build break is the production agent's to fix.

---

## 1. Project Overview

- **Path**: workspace root above; solution `Ghpmv.slnx` (XML `<Solution>` format, not `.sln`).
- **Language / TFM**: C# `net10.0` (`global.json` pins SDK `10.0.302`, `rollForward: latestFeature`).
- **Project system**: SDK-style (`Microsoft.NET.Sdk`) — new `*.cs` files are picked up by the implicit glob; **no `<Compile Include>` registration needed**.
- **Dependency format**: Central Package Management (`Directory.Packages.props`, `ManagePackageVersionsCentrally=true`). A new `PackageReference` must be version-less in the csproj and versioned centrally.
- **Test framework**: **xUnit v3** `3.2.2` (`xunit.v3`), `xunit.runner.visualstudio` 3.1.5, `Microsoft.NET.Test.Sdk` 18.8.1, `coverlet.collector` 10.0.1. Test projects are `<OutputType>Exe</OutputType>` with `<Using Include="Xunit" />` (no `using Xunit;` needed in files).
- **Mocking library**: **none installed** — all doubles are hand-written (`HttpMessageHandler` subclasses, `HttpListener` stub servers). Do not introduce Moq/NSubstitute.
- **Global build settings** (`Directory.Build.props`): `Nullable=enable`, `ImplicitUsings=enable`, `TreatWarningsAsErrors=true`, `AnalysisLevel=latest-recommended`, `InvariantGlobalization=true`. → New test code must be warning-clean and use `CultureInfo.InvariantCulture` / explicit `StringComparison` overloads.
- **New-file registration**: implicit glob only.

### Solution / project map (`Ghpmv.slnx`)

| Project | Path | Role |
|---|---|---|
| `Ghpmv.Cli` | `src/Ghpmv.Cli/Ghpmv.Cli.csproj` | `System.CommandLine` 2.0.10 CLI (`ghpmv.dll`); commands `export`, `import`, `verify`, `login`, `setup`. Single top-level `Program.cs` (1147+ lines). |
| `Ghpmv.Core` | `src/Ghpmv.Core/Ghpmv.Core.csproj` | All logic: `GitHub/`, `Export/`, `Import/`, `Verify/`, `Snapshot/`, `Fixtures/`, `Browser/`. |
| `Ghpmv.Core.Tests` | `tests/Ghpmv.Core.Tests/Ghpmv.Core.Tests.csproj` | **Deterministic unit tests.** References Core **and Cli** (CLI tests spawn `ghpmv.dll` from `AppContext.BaseDirectory`). No network beyond loopback stubs. |
| `Ghpmv.Integration.Tests` | `tests/Ghpmv.Integration.Tests/Ghpmv.Integration.Tests.csproj` | **Live GitHub API tests.** References Core only. Parallelization disabled assembly-wide. |
| `Ghpmv.Browser.Tests` | `tests/Ghpmv.Browser.Tests/Ghpmv.Browser.Tests.csproj` | Playwright UI tests; `Category=E2E` filtered out in CI. **Out of scope for #46.** |

## 2. Build & Test Commands

Sources: `.github/workflows/ci.yml`, `.github/workflows/live-api.yml`, `docs/TEST_STRATEGY.md` L33-43, `.github/copilot-instructions.md` L9-38.

- **Restore**: `dotnet restore Ghpmv.slnx`
- **Build**: `dotnet build Ghpmv.slnx -c Release --no-restore -warnaserror`
- **Test (scoped — fix cycles)**:
  - `dotnet test tests/Ghpmv.Core.Tests/Ghpmv.Core.Tests.csproj -c Release --no-build`
  - single test: `dotnet test tests/Ghpmv.Core.Tests/Ghpmv.Core.Tests.csproj -c Release --filter "FullyQualifiedName~Ghpmv.Core.Tests.ProjectExporterTests"`
  - integration: `dotnet test tests/Ghpmv.Integration.Tests/Ghpmv.Integration.Tests.csproj -c Release` (skips without `GHPMV_TEST_TOKEN`)
- **Test (harness-equivalent — discovery check)**: `dotnet test Ghpmv.slnx -c Release --no-build --list-tests`. CI equivalent = the two deterministic runs: `dotnet test tests/Ghpmv.Core.Tests/... -c Release --no-build --logger trx` and `dotnet test tests/Ghpmv.Browser.Tests/... -c Release --no-build --filter "Category!=E2E" --logger trx`.
- **Live-API CI** first syncs the fixture: `dotnet run --project src/Ghpmv.Cli -c Release --no-build -- setup --fixture --fixture-org $env:GHPMV_TEST_ORG --fixture-title gpm-fixture --fixture-repo $env:GHPMV_TEST_FIXTURE_REPO --token $env:GHPMV_TEST_TOKEN`, then `dotnet test tests/Ghpmv.Integration.Tests/... -c Release --no-build --logger 'trx;LogFileName=integration.trx'`.
- **Lint**: none for C# beyond `-warnaserror`; `ghalint` lints workflows only.

## 3. Scope

- **Boundary**: the status-updates migration slice — `Snapshot`, `Export`, `Import` (`StatusUpdateImporter`, `ImportLog`, `ProjectTemplateWriteSession`), `Verify`, `Fixtures`, CLI `import` stdout; and the paired test files in `tests/Ghpmv.Core.Tests` + `tests/Ghpmv.Integration.Tests`.
- **Targets**: `src/Ghpmv.Core/Import/StatusUpdateImporter.cs`, `src/Ghpmv.Core/Import/ProjectTemplateWriteSession.cs`, `src/Ghpmv.Core/Import/StatusUpdateImportResult.cs`, status-update parts of `src/Ghpmv.Core/Export/ProjectExporter.cs`, `src/Ghpmv.Core/Snapshot/ProjectSnapshot.cs`, `src/Ghpmv.Core/Import/ImportLog.cs`, `src/Ghpmv.Core/Verify/ProjectVerifier.cs`, `src/Ghpmv.Core/Fixtures/FixtureProjectBuilder.cs`, `src/Ghpmv.Cli/Program.cs` (import stdout).
- **Out of scope**: `src/Ghpmv.Core/Browser/**`, `tests/Ghpmv.Browser.Tests/**`, `MappingTemplates`, `CsvMapping`, `ProjectFilterTransformer`, `UpdateChecker`, `GitHubRestClient`, CLI `login` / `setup --browsers`.

## 4. Dependency Graph (status-update slice)

- **Leaf types (no in-scope deps; test directly, no doubles)**:
  - `StatusUpdateSnapshot`, `ProjectSnapshot`, `StatusUpdateImportResult`, `VerifyDifference` / `VerifyCategoryResult` / `VerifyReport` — pure records.
  - `StatusUpdateImporter.BuildImportedBody(StatusUpdateSnapshot)` — `public static`, pure (mirrors `ItemImporter.BuildDraftBody`).
  - `ImportLog` (file I/O only) — tested against a temp directory.
  - `ProjectVerifier.Compare(...)` — `public static`, pure snapshot→snapshot.
  - `FixtureProjectBuilder.CreateSnapshot(...)` / `ShouldImportItems(...)` — `public static`, pure.
- **Mid-layer (depend on `GitHubGraphQLClient`; fake via `HttpMessageHandler`)**: `StatusUpdateImporter.ImportAsync`, `ProjectTemplateWriteSession.PrepareAsync/RestoreAsync`, `ProjectExporter.ExportAsync`, `ProjectVerifier.VerifyAsync`.
- **Top layer (process-level)**: `Ghpmv.Cli/Program.cs` import/verify pipelines — tested by spawning `ghpmv.dll` against an `HttpListener` stub (`CliImportTests`).

## 5. Analogous already-implemented sub-entity — **Views** (plus Items for the import log)

Views are the closest complete template ((a)–(e) all present); Items are the closest template for *resume by persisted id*.

### (a) Export → snapshot section
`src/Ghpmv.Core/Export/ProjectExporter.cs`
- `public async Task<ProjectSnapshot> ExportAsync(string ownerLogin, int projectNumber, CancellationToken cancellationToken = default)` (L44).
- GraphQL call order (**matters for positional stub queues**): metadata → items → **statusUpdates** → fields.
- Paginated fetch pattern (the new status-update fetch is a copy of this):
```csharp
private async Task<List<ItemSnapshot>> FetchItemsAsync(string ownerLogin, int projectNumber, CancellationToken cancellationToken)
{
    var items = new List<ItemSnapshot>();
    await foreach (var node in _client.QueryPaginatedAsync(
        ItemsQuery,
        new { login = ownerLogin, number = projectNumber, first = ItemsPageSize },
        OwnerField + ".projectV2.items",
        cancellationToken: cancellationToken).ConfigureAwait(false))
    { items.Add(ParseItem(node, position: items.Count)); }
    return items;
}
```
- `private string OwnerField => OwnerType == ProjectOwnerType.User ? "user" : "organization";` — query constants use a `__OWNER__` token: `MetadataQueryTemplate.Replace("__OWNER__", OwnerField, StringComparison.Ordinal)`.
- Also: `public Action<string>? OnProgress`, `public Func<ProjectSnapshot, CancellationToken, Task<ProjectSnapshot>>? PostExportAsync`, `public ProjectOwnerType OwnerType { get; init; }`, `ListProjectsAsync(...)`, `public sealed record ProjectListEntry(int Number, string Title, bool Closed)`.

### (b) Import via GraphQL mutation with resume/import-log
`src/Ghpmv.Core/Import/ProjectViewImporter.cs` (`internal sealed class`, 478 lines):
```csharp
public ProjectViewImporter(GitHubGraphQLClient client, ProjectImportLog operationLog, Func<CancellationToken, Task> saveOperationLogAsync)
public async Task<IReadOnlyDictionary<int, int>> ImportAsync(
    IReadOnlyList<ViewSnapshot> sourceViews, string projectId,
    IReadOnlyDictionary<string, string> fieldIds, ProjectImportOutcome projectOutcome, CancellationToken cancellationToken)
public IReadOnlyList<string> Warnings { get; }
public Action<string>? OnProgress { get; set; }
```
Create path — persist pending → mutate → clear pending; `AmbiguousMutationResultException` is rethrown so the pending record survives:
```csharp
_operationLog.PendingViews[source.Number] = new PendingViewOperation
{
    OperationId = operationId, ProjectId = projectId, SourceNumber = source.Number,
    Name = source.Name, Layout = source.Layout,
    ExistingViewIds = [.. targetViews.Select(view => view.Id)],
};
await _saveOperationLogAsync(cancellationToken).ConfigureAwait(false);
try
{
    data = await _client.MutationAsync("createProjectV2View", CreateViewMutation, new { projectId, name = source.Name, layout = source.Layout, configuration = new { visibleFieldIds } },
        MutationRetryPolicy.Create, target: projectId, clientMutationId: operationId,
        requiredResultPath: "projectV2View.id", cancellationToken: cancellationToken).ConfigureAwait(false);
}
catch (AmbiguousMutationResultException) { throw; }
catch { _operationLog.PendingViews.Remove(source.Number); await _saveOperationLogAsync(CancellationToken.None).ConfigureAwait(false); throw; }
```
Reconcile-after-ambiguity (3 attempts, exactly-one-candidate rule):
```csharp
private async Task<TargetView> ReconcilePendingViewAsync(ViewSnapshot source, PendingViewOperation pending, CancellationToken cancellationToken)
{
    for (var attempt = 0; attempt < 3; attempt++)
    {
        if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), cancellationToken).ConfigureAwait(false);
        var candidates = views.Where(v => !pending.ExistingViewIds.Contains(v.Id, StringComparer.Ordinal) && …).ToArray();
        if (candidates.Length == 1) return candidates[0];
        if (candidates.Length > 1) throw new InvalidOperationException($"Pending view operation '{pending.OperationId}' matches multiple new views. Reconcile the target manually.");
    }
    throw new InvalidOperationException($"Pending view operation '{pending.OperationId}' could not be reconciled. The view may not have been created; reconcile the target manually.");
}
```
Warning idiom: `private void Warn(string message) { _warnings.Add(message); OnProgress?.Invoke("warning: " + message); }`

### (c) Verify class / category
`src/Ghpmv.Core/Verify/ProjectVerifier.cs`
```csharp
private const string ProjectCategory = "Project";
private const string FieldCategory = "Field";
private const string ViewCategory = "View";
private const string WorkflowCategory = "Workflow";
private const string ItemCategory = "Item";
private const string StatusUpdateCategory = "StatusUpdate";   // added by #46
private const string CollaboratorCategory = "Collaborator";
private const string LinkedRepositoryCategory = "LinkedRepository";
```
- Public API: `VerifyAsync(ProjectSnapshot source, string targetOrgLogin, int targetProjectNumber, CancellationToken)` (re-exports the target through `ProjectExporter`), plus four `public static VerifyReport Compare(...)` overloads (2/3/4/5 args — repository, user, organization mappings).
- Category rollup (~L1098):
```csharp
private static VerifyCategoryResult CategoryResult(string category, IReadOnlyList<VerifyDifference> differences, HashSet<string> notVerified)
// any Error ⇒ Mismatch; notVerified contains category ⇒ NotVerified; any Warning ⇒ PartialMatch; else Match
```
- `AddError(differences, category, message)` / `Add(differences, severity, category, message)`.
- **Nullable-collection precedent**: `CompareCollaborators` / `CompareLinkedRepositories` (L406-484) use `if (source is null || target is null) { notVerified.Add(Category); if (source is not null) Add(..., Warning, ...); return; }`. `CompareStatusUpdates` deliberately differs: `source is null ⇒ return` (category omitted entirely), `target ??= []`.
- Report shape: `src/Ghpmv.Core/Verify/VerifyReport.cs` — `VerifySeverity {Info,Warning,Error}`, `VerifyStatus {Match,Mismatch,PartialMatch,NotVerified}`, `VerifyReport.Status/IsMatch/ErrorCount/WarningCount/InfoCount/NotVerifiedCount/ShouldFail(bool failOnWarning)/WithWarnings(string category, IEnumerable<string> warnings)`. JSON output: `src/Ghpmv.Core/Verify/VerifyReportFile.cs`.

### (d) CLI stdout lines (`src/Ghpmv.Cli/Program.cs`)
stdout is the machine-readable contract; progress/warnings go to **stderr**. Import block (L536-549), current shape including the new line:
```
<result.Url>
result={created|updated|skipped} project=<n>
items: created=… resumed=… already-complete=… skipped=… warnings=…
status-updates: created=… resumed=… already-complete=…      ← NEW (printed on every non-skip path)
views: imported=… warnings=…
workflows: imported=… warnings=…                             ← only with --enable-browser-automation
```
Skip path (L449-457) prints only the URL and `result=skipped project=…`. Verify stdout (L1157-1183): `OK: the target project matches the snapshot.` or a `SEVERITY / CATEGORY / MESSAGE` table followed by `CATEGORY STATUS` and `"{Category}: {Status}"` lines.

### (e) Fixtures project builder
`src/Ghpmv.Core/Fixtures/FixtureProjectBuilder.cs`
- `public static ProjectSnapshot CreateSnapshot(string title, string repositoryFullName, string viewerLogin, int pullRequestNumber)` — pure; used by unit tests **and** `IntegrationFixtureSnapshot.CreateKnownAsync`.
- `public static bool ShouldImportItems(bool projectAlreadyExists, bool hasItemLog, bool projectImportWasPending)` — pure, already `[Theory]`-tested.
- Setup flow now wraps the status-update stage in a template session + `finally` restore (diff in §11).

## 6. ProjectImportLog / import-log.json mechanism

Two logs live side by side in the snapshot directory:

| File | Type | Scope |
|---|---|---|
| `project-import-log.json` | `ProjectImportLog` (`src/Ghpmv.Core/Import/ProjectImportLog.cs`) | project / field / issue-field / issue-field-link / view create reconciliation |
| `import-log.json` | `ImportLog` (`src/Ghpmv.Core/Import/ImportLog.cs`) | **item + status-update** target ids and pending creates |

`ProjectImportLog`:
```csharp
public const string FileName = "project-import-log.json";
public PendingProjectOperation? PendingProject { get; set; }
public Dictionary<string, PendingFieldOperation> PendingFields { get; init; }
public Dictionary<string, PendingIssueFieldOperation> PendingIssueFields { get; init; }
public Dictionary<string, PendingIssueFieldLinkOperation> PendingIssueFieldLinks { get; init; }
public Dictionary<int, PendingViewOperation> PendingViews { get; init; }
public static async Task<ProjectImportLog> LoadAsync(string directory, CancellationToken cancellationToken)  // new() when absent
public async Task SaveAsync(string directory, CancellationToken cancellationToken)                            // temp file + File.Move(overwrite: true)
```
All `Pending*Operation` records carry `OperationId` + an `Existing*Ids` baseline array (`ExistingProjectIds`, `ExistingFieldIds`, `ExistingIssueFieldIds`, `ExistingViewIds`).

`ImportLog` (the one status updates use):
```csharp
public const string FileName = "import-log.json";
public const string BackupFileName = "import-log.json.bak";
public const int CurrentSchemaVersion = 2;
public required string ProjectId { get; init; }
public string? SourceSnapshotFingerprint { get; init; }
public Dictionary<string, string> Items { get; init; }                                       // "sourceIndex" → target item id
public Dictionary<string, ImportItemState> ItemStates { get; init; }
public Dictionary<string, PendingDraftOperation> PendingDrafts { get; init; }
public Dictionary<string, PendingContentOperation> PendingContents { get; init; }
public Dictionary<string, string> StatusUpdates { get; init; }                                // NEW: "sourceIndex" → target status-update node id
public Dictionary<string, PendingStatusUpdateOperation> PendingStatusUpdates { get; init; }   // NEW
public bool HasIncompleteItems { get; }
public static async Task<ImportLog?> LoadAsync(string directory, CancellationToken cancellationToken = default)   // null when absent; InvalidDataException when malformed/legacy
public async Task<string> SaveAsync(string directory, CancellationToken cancellationToken = default)
public static string ComputeSnapshotFingerprint(ProjectSnapshot snapshot)
```
`LoadAsync` rejects (`InvalidDataException`): null dictionaries, non-integer/negative status-update keys, blank ids, blank `OperationId`/`ProjectId`, null/blank `ExistingStatusUpdateIds`, **duplicate target ids** in `StatusUpdates`, and keys present in both `StatusUpdates` and `PendingStatusUpdates`. Messages:
```
"{FileName} contains malformed item state and cannot be resumed safely."
"{FileName} contains inconsistent status update mappings and cannot be resumed safely."
```

**De-duplication semantics (confirmed):** resume is **by persisted target node id only**. Items dedupe by *content identity* (`ImportItemState.TargetContentIdentity`, `PendingDraftOperation.Title/Body/AssigneeIds`, `PendingContentOperation.ContentId`); status updates deliberately do **not** — `PendingStatusUpdateOperation` carries only `OperationId`, `ProjectId`, `ExistingStatusUpdateIds`, and reconciliation is "exactly one id absent from the baseline". Bodies are never compared for dedupe. Tests should assert both directions: with the log present a re-run creates nothing (`AlreadyComplete`), with the log removed a re-run **does** create duplicates (no content dedupe).

## 7. Template unmark/remark + final orchestration seam

- **Seam class**: `src/Ghpmv.Core/Import/ProjectTemplateWriteSession.cs` (new, 106 lines):
```csharp
public sealed class ProjectTemplateWriteSession
{
    public bool RestorationRequired { get; }
    public Action<string>? OnProgress { get; init; }
    public static async Task<ProjectTemplateWriteSession> PrepareAsync(GitHubGraphQLClient client, string projectId,
        Action<string>? onProgress = null, CancellationToken cancellationToken = default);
    public async Task RestoreAsync(CancellationToken cancellationToken = default);
    private async Task SetTemplateAsync(bool mark, CancellationToken cancellationToken);
}
```
  `PrepareAsync` queries `node(id:$projectId){ ... on ProjectV2 { id template } }`; throws `GitHubGraphQLException($"Target project '{projectId}' was not found while checking template state.")` when the node is missing; when `template == true` it issues `unmarkProjectV2AsTemplate`. `RestoreAsync` issues `markProjectV2AsTemplate`. Both use `MutationRetryPolicy.Idempotent`, `target: projectId`, `requiredResultPath: "projectV2.id"`. `RestoreAsync` is a no-op when `!RestorationRequired || _restored` (idempotent — the CLI calls it twice: happy path + `finally`).
  Progress strings: `"Temporarily unmarking the target project as a template before status update writes..."` / `"Restoring the target project's template state as the final import stage..."`.
- **Final orchestration seams**:
  - CLI: `src/Ghpmv.Cli/Program.cs` → `importCommand.SetAction(async (parseResult, cancellationToken) => …)`, sequence `ProjectImporter.ImportAsync|ImportIntoAsync` → `ItemImporter.ImportAsync` → **`ProjectTemplateWriteSession.PrepareAsync` + `StatusUpdateImporter.ImportAsync`** (only when `snapshot.StatusUpdates is { Count: > 0 }`) → browser view/workflow importers → `templateWriteSession.RestoreAsync` → stdout; `finally { if (templateWriteSession is { RestorationRequired: true }) { try { await templateWriteSession.RestoreAsync(CancellationToken.None); } catch (…) { Console.Error.WriteLine($"error: failed to restore the target project's template state: {exception.Message}"); } } }` (L559-577).
  - Core: `ProjectImporter.ApplySnapshotAsync(ProjectSnapshot snapshot, string ownerLogin, ProjectRef project, ProjectImportOutcome outcome, CancellationToken cancellationToken)` (`src/Ghpmv.Core/Import/ProjectImporter.cs` L386) — metadata → visibility → issue fields → fields → views. **Status updates are NOT sequenced here**; they are a CLI/fixture-level stage.
  - Fixture: `FixtureProjectBuilder` setup does the same template wrap in `try/finally` (§11).
- Before #46 the repo had **no** template logic at all; `docs/MIGRATION_SCOPE.md` L20 still reads "Project templates ❌ … Template status is not part of v1." (that doc is being edited concurrently).

## 8. GraphQL client (`src/Ghpmv.Core/GitHub/`)

`GitHubGraphQLClient.cs` (`public sealed class … : IDisposable`, 659 lines):
```csharp
public GitHubGraphQLClient(string token, Uri? baseUrl = null)
internal GitHubGraphQLClient(string token, Uri? baseUrl, HttpMessageHandler handler, Func<TimeSpan, CancellationToken, Task>? delayAsync)  // ← test seam
public static Uri NormalizeBaseUrl(string baseUrl)
public Action<string>? OnRetry { get; set; }
public async Task<JsonElement> QueryAsync(string query, object? variables = null, CancellationToken cancellationToken = default)
public async Task<JsonElement> MutationAsync(string operationName, string mutation, object? variables = null,
    MutationRetryPolicy retryPolicy = MutationRetryPolicy.Create, string? target = null, string? clientMutationId = null,
    string? requiredResultPath = null, CancellationToken cancellationToken = default)
public async IAsyncEnumerable<JsonElement> QueryPaginatedAsync(string query, object? variables, string connectionPath,
    string cursorVariableName = "after", [EnumeratorCancellation] CancellationToken cancellationToken = default)
public async Task<string> GetViewerLoginAsync(CancellationToken cancellationToken = default)
public void Dispose()
```
- **Cursor pagination contract**: the connection at `connectionPath` (dot path inside `data`, e.g. `"organization.projectV2.statusUpdates"` for owner-scoped queries, `"node.statusUpdates"` for node-id queries) must select `nodes` and `pageInfo { hasNextPage endCursor }`, and the query must declare the cursor variable (default `after`). The client sets `variableMap[cursorVariableName] = cursor` on each round and yields every node.
- **Cancellation**: every public member takes an explicit `CancellationToken`, threaded through `ConfigureAwait(false)`; async iterators use `[EnumeratorCancellation]`. Tests always pass `TestContext.Current.CancellationToken`.
- **Retry / ambiguity**: `MutationRetryPolicy { Create, Idempotent }`. `Create` throws `AmbiguousMutationResultException` if an error may have followed the side effect or the payload is missing; `Idempotent` retries up to 3× with backoff. `GitHubGraphQLException` exposes `ErrorsJson`, `ErrorType`, `StatusCode`. Also auto-retries `"temporary conflict"` and `"Something went wrong while executing your query"`. Always sends header `GraphQL-Features: issue_fields`, UA `ghpmv`.
- **Client double used by every deterministic test**:
```csharp
using var client = new GitHubGraphQLClient("token", baseUrl: null, handler, (_, _) => Task.CompletedTask); // no real delays
// or with an explicit endpoint:
new GitHubGraphQLClient("dummy-token", new Uri("https://example.test/graphql"), handler, delayAsync: static (_, _) => Task.CompletedTask);
```

### Integration-test credential / skip convention
There is **no custom attribute** (`[SkipIfNoCredentials]` does not exist). Every live test class declares:
```csharp
private static string Token
{
    get
    {
        var token = Environment.GetEnvironmentVariable("GHPMV_TEST_TOKEN");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(token), "GHPMV_TEST_TOKEN is not set; skipping real-API test.");
        return token!;
    }
}
```
(`ConnectivityTests.Token` is `internal static` and reusable; most files declare their own copy verbatim.)
`tests/Ghpmv.Integration.Tests/TestAssembly.cs` is one line: `[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]`.

`IntegrationTestSettings` (internal static): `SourceOrg` (`GHPMV_TEST_ORG` → `GHPMV_SOURCE_ORG` → `"gpm-source"`), `TargetOrg` (`GHPMV_TEST_TARGET_ORG` → `GHPMV_TARGET_ORG` → `"gpm-target"`), `FixtureRepositoryName` (`GHPMV_TEST_FIXTURE_REPO`, default `fixture-repo2`), `TargetFixtureRepositoryName` (`GHPMV_TEST_TARGET_FIXTURE_REPO`, default `fixture-repo`), `FixtureRepositoryFullName`, `TargetFixtureRepositoryFullName`, `FixtureProjectNumber` (`GHPMV_TEST_PROJECT_NUMBER`, default `89`, `FormatException` when unparsable), `FixturePullRequestNumber = 3`, `CreateOperationLogDirectory()` → `%TEMP%/ghpmv-project-import-<guid:N>`.

## 9. Snapshot schema versioning

`src/Ghpmv.Core/Snapshot/ProjectSnapshot.cs`:
```csharp
public const int CurrentSchemaVersion = 1;      // ← must stay 1 for #46
public required int SchemaVersion { get; init; }
```
Serialization: source-generated `SnapshotJsonContext` (`SnapshotJsonContext.cs`, camelCase + indented); file I/O `SnapshotFile.SaveAsync/LoadAsync`, `public const string FileName = "snapshot.json"`.

**Backward-compatible nullable-collection precedent within v1** (exact pattern the new field follows):
```csharp
/// <summary>Project collaborators … Null when not captured …</summary>
public IReadOnlyList<CollaboratorSnapshot>? Collaborators { get; init; }

/// <summary>Repositories linked to the project, in "owner/name" form. Null when the snapshot
/// predates this field (schema additions are backward compatible within version 1).</summary>
public IReadOnlyList<string>? LinkedRepositories { get; init; }

/// <summary>Project status update history in reverse chronological order. Null when the
/// snapshot predates status update support (a backward-compatible schema-v1 addition).</summary>
public IReadOnlyList<StatusUpdateSnapshot>? StatusUpdates { get; init; }   // ← #46
```
Existing regression tests for this rule: `SnapshotTests.Deserialize_snapshot_without_collaborators_and_linked_repositories_yields_null` (JSON literal with only `schemaVersion/project/fields/views/workflows/items`) and `Deserialize_without_schema_version_throws` (`JsonException`).

## 10. Existing tests to extend (do **not** create redundant files)

### 10.1 `tests/Ghpmv.Core.Tests` (deterministic; namespace `Ghpmv.Core.Tests`)

Shared conventions: `public class XTests` (no base class, no `[Trait]`/categories), methods named `Sentence_case_with_underscores`, `public async Task` / `public void`, `TestContext.Current.CancellationToken` everywhere, temp dirs via `Directory.CreateTempSubdirectory("ghpmv-resume-").FullName` or `Path.Combine(Path.GetTempPath(), "ghpmv-cli-verify-" + Guid.NewGuid().ToString("N"))` with `try/finally { Directory.Delete(directory, recursive: true); }`, raw-string-literal JSON fixtures, `Assert.Single/Assert.Equal(collection, projection)` style.

| File | #tests | Conventions / doubles (exact) | Extension notes |
|---|---|---|---|
| `ProjectExporterTests.cs` | 9 `[Fact]/[Theory]` (2 `[InlineData]`) | `private sealed class StubHandler(params string[] responses) : HttpMessageHandler` — `Queue<string> _responses`, `List<string> RequestBodies`; helpers `MetadataResponse(string views)`, `FieldsResponse(string fields, bool hasNextPage = false, string? endCursor = null)`, `const string EmptyItemsResponse`, `CreateClient(HttpMessageHandler)`. Example names: `Export_prefers_configured_visible_fields_from_view_configuration`, `Export_paginates_project_fields_without_truncating_the_snapshot`, `Export_rejects_duplicate_field_identity(bool isIssueField, string identityKind)`, `Export_fails_instead_of_writing_an_incomplete_snapshot_when_field_enumeration_fails`. | **BREAKING**: the queue is positional and export now issues metadata → items → **statusUpdates** → fields. Every `new StubHandler(Metadata, Items, Fields)` needs a `StatusUpdatesResponse` inserted 3rd, and `Assert.Equal(6, handler.RequestBodies.Count)` must be re-counted. Add `Export_captures_status_updates_in_reverse_chronological_order`, `Export_paginates_status_updates`, `Export_leaves_optional_status_update_dates_and_creator_null`. |
| `ProjectImporterLogicTests.cs` | 16 (9 `[InlineData]`) | Handler-based fakes; names `Conflict_skip_returns_skipped_without_sending_mutations`, `Conflict_update_runs_prewrite_hook_before_sending_mutations`, `Visibility_update_is_only_required_when_the_value_changes(...)`. | `ProjectImporter` never calls the new importer — prefer a **new** `StatusUpdateImporterLogicTests.cs` / `ProjectTemplateWriteSessionTests.cs` rather than stuffing this file. |
| `ProjectImporterResumeTests.cs` | 10 | `ProjectImportLog` reconciliation; names `Ambiguous_project_create_is_adopted_without_resending`, `Field_reconciliation_rejects_multiple_same_named_candidates`, `Import_into_rejects_pending_field_omitted_from_snapshot_before_mutating`. | Template for "reconciliation refuses ambiguous candidates" assertions and exception-message matching. |
| `ItemImporterResumeTests.cs` | 11 (7 `[InlineData]`) | **Closest analogue for `import-log.json` resume.** `private sealed class ResumeHandler(bool draft, string directory) : HttpMessageHandler` with `Resume`, `FailBeforeMutation`, `FailDefinitively`, `CreateMutationCount`, `ClientMutationId`, `PendingWasPresentAtMutation`. Flow: `await Assert.ThrowsAsync<AmbiguousMutationResultException>(() => importer.ImportAsync(snapshot, Target, directory, ct))` → `var pending = await ImportLog.LoadAsync(directory, ct)` → `Assert.Single(pending.PendingDrafts).Value.OperationId` → `handler.Resume = true` → re-run → `Assert.Equal(1, handler.CreateMutationCount)`, `Assert.Equal(0, result.Created)`, `Assert.Equal(1, result.Resumed)`. | Mirror for status updates: pending persisted before the mutation, ambiguous create reconciled without re-sending, definitive/pre-send failure clears pending, second run reports `AlreadyComplete`, cross-project pending rejected. |
| `ItemImporterLogicTests.cs` | 16 | Pure-helper tests `BuildDraftBody_prepends_creator_and_date`, `BuildDraftBody_with_creator_only`, `BuildDraftBody_returns_only_the_note_when_body_is_empty`; log tests `ImportLog_round_trips_through_the_file`, `ImportLog_load_returns_null_when_missing_and_rejects_corrupt_content`, `ImportLog_rejects_legacy_schema_instead_of_ignoring_it`, `ImportLog_replace_failure_preserves_previous_primary`. | **Direct template for `StatusUpdateImporter.BuildImportedBody`** and the new `ImportLog` validation branches (duplicate target ids, key in both maps, non-integer key). |
| `ProjectVerifierTests.cs` | 47 (all `[Fact]`, mostly `public void`) | Pure `ProjectVerifier.Compare(source, target)` with in-file snapshot factories; names `Missing_field_in_target_is_an_error`, `Draft_body_with_imported_attribution_note_matches_the_original`, `Linked_repositories_are_not_verified_when_the_source_predates_capture`, `Exit_policy_fails_errors_unconditionally_and_optional_incomplete_results`, `Json_report_contains_the_same_status_and_counts_as_the_report`. | Add `Status_update_count_mismatch_is_an_error`, `Status_update_status_and_date_differences_are_errors`, `Status_update_body_with_imported_attribution_note_matches_the_original`, `Status_updates_are_not_compared_when_the_source_predates_capture` (assert no `StatusUpdate` entry in `report.Categories`), `Extra_target_status_updates_are_errors`. |
| `SnapshotTests.cs` | 5 | `private static ProjectSnapshot CreateFullSnapshot()` + roundtrip via `SnapshotJsonContext.Default.ProjectSnapshot`; `Roundtrip_preserves_all_values`, `Serialized_json_contains_schema_version`, `SnapshotFile_saves_and_loads_snapshot_json`. | Add `StatusUpdates` to `CreateFullSnapshot()` and assert roundtrip fidelity; assert `Assert.Null(restored.StatusUpdates)` for the legacy JSON literal; keep `schemaVersion == 1`. |
| `FixtureProjectBuilderTests.cs` | 4 (4 `[InlineData]`) | Reflection-driven completeness (`typeof(FieldValueSnapshot).GetProperties()` … `Assert.Contains(values, v => property.GetValue(v) is not null)`); builder call `FixtureProjectBuilder.CreateSnapshot("Fixture", "example/fixture", "octocat", pullRequestNumber: 2)`. | Add `Demo_fixture_exercises_every_status_update_status` (all five statuses, null/non-null `StartDate`/`TargetDate`, strictly descending `CreatedAt`) — a reflection loop over `StatusUpdateSnapshot` properties matches the file's style. |
| `CliImportTests.cs` | 6 | Spawns `dotnet <AppContext.BaseDirectory>/ghpmv.dll import --org target --in <dir> --token dummy-token --target-base-url <stub> --no-update-check` (`RunCliAsync`) and `… verify --org target --project 42 …` (`RunVerifyCliAsync`); `private sealed class GraphQlStubServer : IDisposable` over `HttpListener`, **index-clamped** responses (`_responses[Math.Min(RequestBodies.Count - 1, _responses.Length - 1)]`) and `List<string> RequestBodies`. Asserts exact stdout: `"result=updated project=42"`, `"items: created=0 resumed=0 already-complete=0 skipped=0 warnings=0"`, `"views: imported=0 warnings=0"`, `Assert.Equal(3, server.RequestBodies.Count)`, `Assert.Contains("Project: Match", …)`. | **BREAKING**: the new `status-updates:` stdout line and the extra `statusUpdates` query in export/verify shift both output and request counts. Extend `Conflict_update_emits_stable_result_and_applies_project_mutation` with `Assert.Contains("status-updates: created=0 resumed=0 already-complete=0", result.Output, StringComparison.Ordinal)`; add a case proving `unmarkProjectV2AsTemplate`/`markProjectV2AsTemplate` are issued **only** when `snapshot.StatusUpdates` is non-empty, and that the restore runs after downstream importers. |

Untouched Core.Tests files (reference only): `GitHubGraphQLClientTests.cs` (702 lines; pagination/rate-limit/ambiguity coverage), `GitHubRestClientTests.cs`, `ConflictActionsTests.cs`, `CsvMappingTests.cs`, `MappingTemplatesTests.cs`, `ProjectFilterTransformerTests.cs`, `ProjectViewImporterTests.cs`, `UpdateCheckerTests.cs`, `Browser*Tests.cs`.

### 10.2 `tests/Ghpmv.Integration.Tests` (live GitHub API)

Shared pattern: unique title per run (`private static string NewTestTitle() => "ghpmv-import-test-" + Guid.NewGuid().ToString("N");`), create → assert → **`finally { await DeleteProjectAsync(client, result.ProjectId); }`**. **No `IAsyncLifetime` or class fixtures anywhere** — cleanup is a per-test `try/finally`, helped by `TemporaryProjectFixture.CreateAsync(client, organization, title, ct)` / `DeleteAllByTitleAsync(...)` and local `TryDeleteDirectory(string)` for temp log dirs. Operation-log dirs come from `IntegrationTestSettings.CreateOperationLogDirectory()` or `Directory.CreateTempSubdirectory("ghpmv-m5-").FullName`.

| File | #tests | Pattern | Extension notes |
|---|---|---|---|
| `ProjectExporterTests.cs` | 8 | Reads the shared source fixture (`IntegrationTestSettings.FixtureProjectNumber` in `SourceOrg`); names `Export_has_schema_version_and_project_metadata`, `Export_captures_linked_repositories_and_leaves_collaborators_null`, `Export_contains_the_seven_canonical_fixture_items_with_positions`. | Add `Export_captures_fixture_status_updates_in_reverse_chronological_order` (5 updates: `COMPLETE/OFF_TRACK/AT_RISK/ON_TRACK/INACTIVE`). |
| `ProjectImporterTests.cs` | 6 | `IntegrationFixtureSnapshot.CreateKnownAsync(client, ct)` → `source with { Project = source.Project with { Title = NewTestTitle() } }` → `new ProjectImporter(client) { OperationLogDirectory = IntegrationTestSettings.CreateOperationLogDirectory() }` → asserts on `ImportResult` maps → `finally` delete. Names `Full_round_trip_recreates_all_custom_fields_and_status_options`, `Import_into_existing_project_by_number_merges_fields_and_items`. | Add a status-update round trip: import the project, run `StatusUpdateImporter.ImportAsync(snapshot, result, logDirectory, ct)`, re-export and compare `Status`/`StartDate`/`TargetDate` + attributed bodies; re-run to assert `Created == 0 && AlreadyComplete == 5`; assert `ImportLog.StatusUpdates.Count == 5`. |
| `VerifyTests.cs` | 1 (long) | Known fixture → import → `ItemImporter` → poll `ProjectVerifier.VerifyAsync` until match (`VerificationPollInterval = 5s`, `VerificationTimeout = 2min`) → drift the target (`deleteProjectV2Field`, change a Status value) → assert errors; `finally` deletes the project and `TryDeleteDirectory(logDirectory)`. Contains explicit "guard against silent null==null passes" assertions on fixture content. | Add the same guard for status updates (`Assert.Equal(5, source.StatusUpdates!.Count)`), assert `StatusUpdate: Match` after import, then drift (create an extra status update on the target) and assert a `StatusUpdate` **Mismatch**. |
| `IntegrationFixtureSnapshotTests.cs` | 2 (`public void`, token-free) | Pure tests over `IntegrationFixtureSnapshot.NormalizeKnownSnapshot(snapshot, viewerLogin)` and `SelectCanonicalItems(snapshot)`. | `NormalizeKnownSnapshot` currently rewrites only `Fields`/`Items`; decide whether re-exported status-update bodies (which carry the attribution note) need normalization and cover it here. |
| `TemporaryProjectFixture.cs` | helper | `CreateAsync` → `createProjectV2` → `(string Id, int Number)`; `DeleteAllByTitleAsync` paginates `organization.projectsV2` and issues `deleteProjectV2` (`MutationRetryPolicy.Idempotent`). | Reuse for a live template test: create → `markProjectV2AsTemplate` → `ProjectTemplateWriteSession.PrepareAsync` → import status updates → `RestoreAsync` → assert `template == true` → delete. |
| `ItemImporterTests.cs` (2), `CollaboratorImportTests.cs` (1), `GraphQLClientIntegrationTests.cs` (3), `IssueFieldLifecycleIntegrationTests.cs` (1), `ProjectViewImporterIntegrationTests.cs` (1), `UserProjectTests.cs` (1), `ConnectivityTests.cs` (2) | — | Reference only. `ItemImporterTests.Round_trip_imports_drafts_with_values_and_order_and_resume_skips_existing` is the closest live resume template. |

## 11. Production code status update (present, WIP, currently not compiling)

`git status --porcelain` at research end:
```
 M docs/MANUAL_TEST_PLAN.md      M docs/MIGRATION_SCOPE.md
 M src/Ghpmv.Cli/Program.cs      M src/Ghpmv.Core/Export/ProjectExporter.cs
 M src/Ghpmv.Core/Fixtures/FixtureProjectBuilder.cs   M src/Ghpmv.Core/Import/ImportLog.cs
 M src/Ghpmv.Core/Snapshot/ProjectSnapshot.cs         M src/Ghpmv.Core/Verify/ProjectVerifier.cs
 M src/Ghpmv.Core/Verify/VerifyReport.cs
?? src/Ghpmv.Core/Import/ProjectTemplateWriteSession.cs
?? src/Ghpmv.Core/Import/StatusUpdateImportResult.cs
?? src/Ghpmv.Core/Import/StatusUpdateImporter.cs
```
`dotnet build Ghpmv.slnx -c Debug` → **61 errors**, all in `src/Ghpmv.Core/Verify/ProjectVerifier.cs` (CS1022 / CS8803 / CS0106 starting at L894): `private static void CompareStatusUpdates(...)` was inserted **inside the body of `CompareItems`**, terminating the class early. **No test code references `StatusUpdate` yet** (0 matches under `tests/`).

### New / changed signatures

`src/Ghpmv.Core/Snapshot/ProjectSnapshot.cs`
```csharp
public IReadOnlyList<StatusUpdateSnapshot>? StatusUpdates { get; init; }   // on ProjectSnapshot, declared after Items

/// <summary>A historical Project status update.</summary>
public sealed record StatusUpdateSnapshot
{
    public required string Body { get; init; }
    public required string Status { get; init; }      // GraphQL ProjectV2StatusUpdateStatus
    public string? StartDate { get; init; }           // yyyy-MM-dd
    public string? TargetDate { get; init; }          // yyyy-MM-dd
    public string? Creator { get; init; }             // login, when GitHub exposes it
    public required string CreatedAt { get; init; }   // ISO 8601 — ordering + attribution
    public required string UpdatedAt { get; init; }   // ISO 8601
}
```

`src/Ghpmv.Core/Import/StatusUpdateImportResult.cs`
```csharp
public sealed record StatusUpdateImportResult
{
    public required int Created { get; init; }
    public required int Resumed { get; init; }
    public required int AlreadyComplete { get; init; }
}
```

`src/Ghpmv.Core/Import/StatusUpdateImporter.cs` (306 lines)
```csharp
public sealed class StatusUpdateImporter
{
    private static readonly HashSet<string> SupportedStatuses =
        new(["INACTIVE", "ON_TRACK", "AT_RISK", "OFF_TRACK", "COMPLETE"], StringComparer.Ordinal);

    public StatusUpdateImporter(GitHubGraphQLClient client);
    public Action<string>? OnProgress { get; set; }

    public async Task<StatusUpdateImportResult> ImportAsync(
        ProjectSnapshot snapshot, ImportResult target, string logDirectory, CancellationToken cancellationToken = default);

    /// <summary>Adds source attribution that GitHub's create API cannot preserve.</summary>
    public static string BuildImportedBody(StatusUpdateSnapshot update);
}
```
Encoded behaviors worth asserting:
- `snapshot.StatusUpdates is null` → progress `"Status updates were not captured by this schema-v1 snapshot; leaving the target history unchanged."` + all-zero result, **no API calls**.
- `ValidateStatusUpdates` → `InvalidDataException`: `"Status update at snapshot sequence {i} has unsupported status '{s}'."` / `"Status update at snapshot sequence {i} has invalid createdAt '{v}'."`
- `LoadAsync` guard → `InvalidOperationException`: `"{ImportLog.FileName} in '{dir}' belongs to a different source snapshot or target project. Use a separate log directory or restore the matching snapshot and target before resuming."`
- `ValidateLogAgainstSnapshot` → `InvalidOperationException`: `"{ImportLog.FileName} contains status update state that does not match the selected snapshot and target project."` (key outside `[0,count)` or pending `ProjectId` mismatch).
- Import order `OrderBy(CreatedAt).ThenBy(SourceIndex)` — **oldest first** while the snapshot stores newest first; log keys are the *snapshot* indices as invariant strings.
- Pending mismatch → `InvalidOperationException $"Pending status update operation '{pending.OperationId}' does not match target project '{target.ProjectId}'."`
- Progress: `"[{i}/{n}] Status update at snapshot sequence {k}: already complete."`, `"[{i}/{n}] Creating status update at snapshot sequence {k}..."`, `"[{i}/{n}] Reconciled status update at snapshot sequence {k} to target '{id}'."`, final `"Status update import finished: {c} created, {r} resumed, {a} already complete."`
- Create mutation `createProjectV2StatusUpdate(input:{ projectId, body, status, startDate, targetDate, clientMutationId }) { statusUpdate { id } }`, default `MutationRetryPolicy.Create`, `requiredResultPath: "statusUpdate.id"`; empty id → `GitHubGraphQLException("createProjectV2StatusUpdate returned an empty status update id.")`.
- `ReconcilePendingAsync`: baseline = `ExistingStatusUpdateIds ∪ already-mapped ids`; 3 attempts with `Task.Delay(attempt + 1 s)`; `>1` candidate → `"Pending status update operation '{id}' matches multiple new target updates. Reconcile the target manually."`; none after 3 → `"… could not be reconciled by target id. Refusing to create a possible duplicate."`
- `FetchStatusUpdateIdsAsync` pages `node.statusUpdates(first: 100, orderBy: { field: CREATED_AT, direction: DESC })`.
- Attribution:
```csharp
var note = update.Creator is { Length: > 0 } creator
    ? $"> _Originally created by @{creator} on {update.CreatedAt}._"
    : $"> _Originally created on {update.CreatedAt}._";
return string.IsNullOrEmpty(update.Body) ? note : note + "\n\n" + update.Body;
```

`src/Ghpmv.Core/Import/ImportLog.cs` — added `StatusUpdates`, `PendingStatusUpdates`, `public sealed record PendingStatusUpdateOperation { required string OperationId; required string ProjectId; required string[] ExistingStatusUpdateIds; }` and the `LoadAsync` validation branches in §6. `CurrentSchemaVersion` stays **2**.

`src/Ghpmv.Core/Export/ProjectExporter.cs` — `private const int StatusUpdatesPageSize = 50;`, `FetchStatusUpdatesAsync(...)`, `StatusUpdatesQuery`/`StatusUpdatesQueryTemplate`:
```graphql
query($login: String!, $number: Int!, $first: Int!, $after: String) {
  __OWNER__(login: $login) { projectV2(number: $number) {
    statusUpdates(first: $first, after: $after, orderBy: { field: CREATED_AT, direction: DESC }) {
      nodes { body status startDate targetDate creator { login } createdAt updatedAt }
      pageInfo { hasNextPage endCursor } } } }
```
Progress strings changed to `"Fetched {v} views and {w} workflows. Fetching items and status updates..."` and `"Fetched {f} fields, {i} items, and {s} status updates."`. Export **always** sets `StatusUpdates = statusUpdates` (empty list, never null, on the API path); `Creator` falls back to `null` when the `creator` node is absent.

`src/Ghpmv.Core/Verify/ProjectVerifier.cs` — new `StatusUpdateCategory = "StatusUpdate"`; `Categories` became a `List<VerifyCategoryResult>` with the StatusUpdate entry **appended only when `source.StatusUpdates is not null`** (legacy snapshots keep the original 7 categories). `CompareStatusUpdates(source, target, differences)`: `source is null ⇒ return`; `target ??= []`; count mismatch → `"status update count mismatch (source {n}, target {m})"`; per index with prefix `"status update sequence {i}"` compares `Status`, `StartDate`, `TargetDate` and body as `NormalizeBody(StatusUpdateImporter.BuildImportedBody(expected))` vs `NormalizeBody(actual.Body)` → `"…: body mismatch (including original creator/time attribution)"`. **Note the new `Ghpmv.Core.Verify → Ghpmv.Core.Import` dependency.** `VerifyReport.VerifyDifference.Category` doc comment updated to mention StatusUpdate.

`src/Ghpmv.Core/Fixtures/FixtureProjectBuilder.cs` — `CreateSnapshot` now emits 5 `StatusUpdateSnapshot`s, newest→oldest: `COMPLETE` (2026-01-05, start 2026-01-01, target 2026-04-15), `OFF_TRACK` (01-04, start null), `AT_RISK` (01-03, target null), `ON_TRACK` (01-02, multi-line body, `UpdatedAt` ≠ `CreatedAt`), `INACTIVE` (01-01, Markdown body, both dates null) — all with `Creator = viewerLogin`. Setup wraps the status-update stage:
```csharp
ProjectTemplateWriteSession? templateWriteSession = null;
try
{
    if (snapshot.StatusUpdates is { Count: > 0 })
    {
        templateWriteSession = await ProjectTemplateWriteSession.PrepareAsync(_graphQl, project.ProjectId, OnProgress, cancellationToken).ConfigureAwait(false);
        var statusUpdateImporter = new StatusUpdateImporter(_graphQl) { OnProgress = OnProgress };
        await statusUpdateImporter.ImportAsync(snapshot, project, operationDirectory, cancellationToken).ConfigureAwait(false);
    }
}
finally
{
    if (templateWriteSession is not null) await templateWriteSession.RestoreAsync(CancellationToken.None).ConfigureAwait(false);
}
```

`src/Ghpmv.Cli/Program.cs` — `var hasIncompleteStatusUpdateWork = itemLog is { PendingStatusUpdates.Count: > 0 };` folded into the forced-`ConflictAction.Update` retry rule; status-update stage + template session per §7; new stdout line `status-updates: created=… resumed=… already-complete=…`.

## 12. Recommendations (priority order)

1. **Unblock the build**: `CompareStatusUpdates` must be a class-level member of `ProjectVerifier`, not nested inside `CompareItems`. Nothing can be run until then.
2. **Repair mechanically-broken existing tests first**: `ProjectExporterTests` stub-response ordering/counts, `CliImportTests` stdout assertions + `RequestBodies.Count`, `SnapshotTests` roundtrip.
3. **Leaf-first new unit tests (no doubles)**: `BuildImportedBody` (creator / no creator / empty body / body preserved), `ProjectVerifier.Compare` StatusUpdate matrix incl. legacy-null ⇒ category absent, `SnapshotTests` roundtrip + legacy-null, `FixtureProjectBuilderTests` status coverage, `ImportLog` malformed/inconsistent status-update rejection.
4. **Mid-layer with `HttpMessageHandler` fakes**: `StatusUpdateImporter` (ordering oldest-first, pending persisted pre-mutation, ambiguous create reconciled without resend, >1 candidate rejection, definitive failure clears pending, resume ⇒ `AlreadyComplete`, no content dedupe), `ProjectTemplateWriteSession` (non-template ⇒ zero mutations; template ⇒ unmark then remark; `RestoreAsync` idempotent; missing node ⇒ `GitHubGraphQLException`), `ProjectExporter` status-update pagination.
5. **CLI level**: template mutations only when status updates exist, stdout line stability, restore-failure stderr message.
6. **Live integration** (`GHPMV_TEST_TOKEN`): fixture export of 5 status updates, import round trip + resume re-run, template flag preserved end-to-end, `StatusUpdate: Match` then drift ⇒ `Mismatch`. Each new live test needs a unique title, `finally` cleanup, and the `Assert.SkipWhen` token idiom.
7. **Concerns / blockers**: (a) `Verify` now depends on `Import` for the attribution contract — assert that contract in one place; (b) bodies are rewritten on import, so byte-equal source↔target body assertions fail by design; (c) GitHub exposes no documented delete for status updates — write tests should use throwaway projects (`TemporaryProjectFixture`) rather than the shared fixture, and repeated live runs will accumulate history on the shared fixture project; (d) `docs/MIGRATION_SCOPE.md` / `docs/MANUAL_TEST_PLAN.md` are being edited concurrently — re-read before asserting on documented behavior.
