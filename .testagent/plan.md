# Test Implementation Plan — ProjectV2 Status Updates migration (issue #46)

## Overview

Deterministic + live-API test coverage for the status-updates migration slice: `ProjectSnapshot.StatusUpdates`,
status-update export, `StatusUpdateImporter` (create / resume / reconcile), `ImportLog` status-update state,
`ProjectTemplateWriteSession` (unmark / remark), `ProjectVerifier` `StatusUpdate` category,
`FixtureProjectBuilder` demo data, and CLI `import` stdout.

Approach, per `.testagent/research.md`:

- **Strategy: broad** — the feature is brand-new production code with **zero** existing test references
  (`0` matches for `StatusUpdate` under `tests/`). Every new public member gets at least one test.
- **Leaf-first phasing** — pure records/statics (no doubles) → `HttpMessageHandler`-doubled mid-layer →
  process-level CLI (`HttpListener` stub) → live GitHub API.
- **Extend, do not duplicate** — new tests land in the existing test projects
  (`tests/Ghpmv.Core.Tests`, `tests/Ghpmv.Integration.Tests`). Only three genuinely new deterministic files and
  one new integration file are created; everything else extends existing files.
- **No mocking library** — hand-written `HttpMessageHandler` / `HttpListener` doubles only. Do **not** add
  Moq/NSubstitute.
- **Repairs are first-class work** — the new `statusUpdates` GraphQL round trip and the new stdout line
  mechanically break existing positional-stub and `RequestBodies.Count` assertions. Those repairs are listed
  explicitly per phase and must land in the same phase as the feature that broke them.

### Scope guard — issue #47 is NOT in scope

`ProjectTemplateWriteSession` here is **only** the narrow "temporarily unmark the target so status-update writes
succeed, then restore" seam. Broader project-template migration support (issue **#47**) is explicitly **not**
implemented and must **not** be tested: no tests for exporting/importing template state as a migrated property,
no template-to-project instantiation, no template field/view semantics. Any test beyond
*prepare → (unmark if template) → write status updates → restore* is out of scope.

### Global conventions (apply to every phase)

- xUnit v3; `public class XTests`, no base class, no traits; methods `Sentence_case_with_underscores`.
- Test projects have `<Using Include="Xunit" />` — no `using Xunit;` line.
- `Nullable=enable`, `TreatWarningsAsErrors=true`, `InvariantGlobalization=true` ⇒ warning-clean code,
  explicit `StringComparison`/`CultureInfo.InvariantCulture`.
- SDK-style projects: new `.cs` files need **no** `<Compile Include>` registration.
- Temp dirs: `Directory.CreateTempSubdirectory("ghpmv-status-").FullName` with `try/finally` recursive delete.
- GraphQL client double: `new GitHubGraphQLClient("token", baseUrl: null, handler, (_, _) => Task.CompletedTask)`.
- **CancellationToken checklist (acceptance criterion 9)** — see the dedicated section at the end. Every new
  `await` in a test passes `TestContext.Current.CancellationToken`; production seams are called with an explicit
  token, never the default. This is a per-phase success-criteria checkbox, not just an implementation detail.

## Commands

- **Restore**: `dotnet restore Ghpmv.slnx`
- **Build**: `dotnet build Ghpmv.slnx -c Release --no-restore -warnaserror`
- **Test (deterministic)**: `dotnet test tests/Ghpmv.Core.Tests/Ghpmv.Core.Tests.csproj -c Release --no-build`
- **Test (single class)**: `dotnet test tests/Ghpmv.Core.Tests/Ghpmv.Core.Tests.csproj -c Release --filter "FullyQualifiedName~Ghpmv.Core.Tests.StatusUpdateImporterResumeTests"`
- **Test (live)**: `dotnet test tests/Ghpmv.Integration.Tests/Ghpmv.Integration.Tests.csproj -c Release` (skips without `GHPMV_TEST_TOKEN`)
- **Discovery check**: `dotnet test Ghpmv.slnx -c Release --no-build --list-tests`
- **Lint**: none beyond `-warnaserror` (`ghalint` covers workflows only)

## Phase Summary

| Phase | Focus | Files (create / extend) | Est. tests | Repairs |
|-------|-------|------------------------|-----------:|--------:|
| 0 | Build-break gate (production-side blocker, no test code) | 0 | 0 | 0 |
| 1 | Leaf/pure: snapshot, verifier, BuildImportedBody, ImportLog, fixture builder | 1 create / 4 extend | 30–34 | 2 |
| 2 | Mid-layer with `HttpMessageHandler` doubles: exporter, importer, template session | 2 create / 2 extend | 31–35 | 9 |
| 3 | CLI process-level stdout + template ordering | 0 create / 1 extend | 5–6 | 6 |
| 4 | Live GitHub API E2E | 1 create / 3 extend | 7–8 | 0 |
| **Total** | | **4 create / 10 extend** | **73–83** | **17** |

---

## Phase 0: Build-break gate (blocker acknowledgement — no test code)

### Overview

`dotnet build Ghpmv.slnx` currently fails with **61 errors** (CS1022 / CS8803 / CS0106 starting at
`src/Ghpmv.Core/Verify/ProjectVerifier.cs` L894): `private static void CompareStatusUpdates(...)` was inserted
**inside the body of `CompareItems`**, terminating the class early. Nothing — not even test discovery — can run
until this is fixed.

### Rule for the test implementer

- This is **production code owned by the parallel production agent**. Do **not** edit
  `src/Ghpmv.Core/Verify/ProjectVerifier.cs` (or any other `src/**` file) to unblock yourself.
- Before starting Phase 1, run `dotnet build Ghpmv.slnx -c Release --no-restore` and confirm it succeeds.
- If it still fails, **stop and flag the blocker explicitly** in `status.md` / the PR description with the exact
  file, line and error codes above, and state that Phase 1+ is blocked pending the production fix.
- Test **authoring** may proceed against the signatures recorded in research §11 while the fix is pending; test
  **execution / verification** may not be claimed until the build is green.

### Success Criteria

- [ ] `dotnet build Ghpmv.slnx -c Release --no-restore -warnaserror` exits 0 **or** the blocker is recorded verbatim
- [ ] No file under `src/**` or `docs/**` modified by the test work

---

## Phase 1: Leaf & pure tests (no HTTP doubles)

### Overview

Establishes the contracts every later phase depends on: the snapshot shape and its backward compatibility, the
attribution-note contract shared by importer *and* verifier, the verifier's `StatusUpdate` category rules, the
`ImportLog` status-update validation branches, and the demo fixture data. All pure — no network, no doubles.

### Files to Test

#### 1. ProjectSnapshot.cs → `tests/Ghpmv.Core.Tests/SnapshotTests.cs` *(extend)*

- **Source**: `src/Ghpmv.Core/Snapshot/ProjectSnapshot.cs`
- **Test File**: `tests/Ghpmv.Core.Tests/SnapshotTests.cs`
- **Test Class**: `SnapshotTests` (existing, 5 tests)

**Repairs (existing tests):**

1. `Roundtrip_preserves_all_values` — **repair**: `CreateFullSnapshot()` must now populate `StatusUpdates` with at
   least two `StatusUpdateSnapshot`s (one fully populated, one with `StartDate`/`TargetDate`/`Creator` null) so the
   "all values" claim stays true; assert every `StatusUpdateSnapshot` property round-trips.
2. `SnapshotFile_saves_and_loads_snapshot_json` — **repair**: consumes `CreateFullSnapshot()`; extend its assertion
   to include `StatusUpdates` count and first-element `Body`/`Status`/`CreatedAt`.

**New tests:**

3. `Roundtrip_preserves_status_updates` — serializes/deserializes via
   `SnapshotJsonContext.Default.ProjectSnapshot` and asserts `Body`, `Status`, `StartDate`, `TargetDate`,
   `Creator`, `CreatedAt`, `UpdatedAt` are byte-identical for both a fully populated and an all-optional-null
   update, and that reverse-chronological order is preserved.
4. `Deserialize_snapshot_without_status_updates_yields_null` — legacy raw-string JSON literal containing only
   `schemaVersion/project/fields/views/workflows/items` deserializes with `Assert.Null(restored.StatusUpdates)`
   (mirrors `Deserialize_snapshot_without_collaborators_and_linked_repositories_yields_null`).
5. `Serialized_json_keeps_schema_version_one_when_status_updates_are_present` — asserts
   `ProjectSnapshot.CurrentSchemaVersion == 1` and the emitted JSON contains `"schemaVersion": 1` even with a
   populated `statusUpdates` array (schema-v1 additive rule).
6. `Snapshot_with_empty_status_update_list_round_trips_as_empty_not_null` — distinguishes "not captured" (`null`)
   from "captured, none exist" (`[]`), which the verifier and importer treat differently.

> Covers acceptance criteria **1**.

#### 2. StatusUpdateImporter.BuildImportedBody → `tests/Ghpmv.Core.Tests/StatusUpdateImporterLogicTests.cs` *(create)*

- **Source**: `src/Ghpmv.Core/Import/StatusUpdateImporter.cs` (`public static string BuildImportedBody(StatusUpdateSnapshot)`)
- **Test File**: `tests/Ghpmv.Core.Tests/StatusUpdateImporterLogicTests.cs` **(net-new file)**
- **Test Class**: `StatusUpdateImporterLogicTests`
- **Rationale**: research §10.1 says `ItemImporterLogicTests.cs` is the *template* (`BuildDraftBody_*`) but
  recommends a **new** file for status updates rather than stuffing existing importer files. Phase 2 extends this
  same file with handler-doubled `ImportAsync` tests.

**New tests (pure section of the file):**

1. `BuildImportedBody_prepends_creator_and_created_at` — for `Creator = "octocat"`, `CreatedAt = "2026-01-05T00:00:00Z"`,
   asserts the result starts with exactly
   `> _Originally created by @octocat on 2026-01-05T00:00:00Z._` followed by `"\n\n"` then the original body.
2. `BuildImportedBody_without_creator_omits_the_mention` — `Creator = null` ⇒ note is exactly
   `> _Originally created on {CreatedAt}._` with no `@`.
3. `BuildImportedBody_returns_only_the_note_when_body_is_empty` — `Body = ""` ⇒ result equals the note alone, no
   trailing blank lines.
4. `BuildImportedBody_preserves_multi_line_markdown_body` — a body with headings, a bullet list and `\n\n`
   paragraphs survives verbatim after the note prefix (no re-wrapping, no line-ending rewrite).
5. `BuildImportedBody_is_stable_for_repeated_calls` — calling twice on the same snapshot yields identical strings
   (the verifier depends on this determinism).

> Covers acceptance criteria **3** (attribution) and the contract half of **4**.

#### 3. ProjectVerifier StatusUpdate category → `tests/Ghpmv.Core.Tests/ProjectVerifierTests.cs` *(extend)*

- **Source**: `src/Ghpmv.Core/Verify/ProjectVerifier.cs` (`CompareStatusUpdates`, `StatusUpdateCategory`)
- **Test File**: `tests/Ghpmv.Core.Tests/ProjectVerifierTests.cs` (existing, 47 tests)
- **Test Class**: `ProjectVerifierTests`
- **Style**: pure `ProjectVerifier.Compare(source, target)` with in-file snapshot factories, `public void`.

**New tests:**

1. `Status_updates_are_not_compared_when_the_source_predates_capture` — `source.StatusUpdates is null` ⇒ the report
   contains **no** `StatusUpdate` entry at all (`Assert.DoesNotContain(report.Categories, c => c.Category == "StatusUpdate")`),
   the original 7 categories are unchanged, and `report.NotVerifiedCount` is unaffected (category omitted, **not**
   `NotVerified`).
2. `Status_update_category_is_present_and_matches_when_sequences_align` — source with 3 updates vs target whose
   bodies are `BuildImportedBody(source[i])` ⇒ `StatusUpdate: Match` and zero differences in that category.
3. `Status_update_count_mismatch_is_an_error` — source 3 / target 2 ⇒ one Error whose message is
   `status update count mismatch (source 3, target 2)`.
4. `Extra_target_status_updates_are_errors` — source 2 / target 3 ⇒ Error
   `status update count mismatch (source 2, target 3)`, category `Mismatch`.
5. `Status_update_status_difference_is_an_error` — `ON_TRACK` vs `AT_RISK` at index 1 ⇒ Error prefixed
   `status update sequence 1`.
6. `Status_update_start_and_target_date_differences_are_errors` — differing `StartDate` and `TargetDate` each
   produce an Error prefixed `status update sequence {i}`; a null-vs-null date pair produces none.
7. `Status_update_body_with_imported_attribution_note_matches_the_original` — target body is
   `StatusUpdateImporter.BuildImportedBody(source)` ⇒ **no** difference (the single place the Verify→Import
   contract is asserted; mirrors `Draft_body_with_imported_attribution_note_matches_the_original`).
8. `Status_update_body_without_the_attribution_note_is_an_error` — target body equals the raw source body ⇒ Error
   ending `: body mismatch (including original creator/time attribution)`.
9. `Status_update_creator_and_created_at_are_not_compared` — target updates carry a different `Creator` and a
   different `CreatedAt`/`UpdatedAt` than the source, everything else equal ⇒ `StatusUpdate: Match`, zero
   differences (explicitly asserts these fields are **not** API-reproducible and are excluded).
10. `Status_updates_with_null_target_collection_are_treated_as_empty` — `source.StatusUpdates` has 2,
    `target.StatusUpdates` is `null` ⇒ `target ??= []` ⇒ count-mismatch Error (not a crash, not `NotVerified`).
11. `Status_update_category_rolls_up_to_mismatch_on_any_error` — asserts `CategoryResult` rollup: any Error ⇒
    `VerifyStatus.Mismatch` and `report.IsMatch == false`.

> Covers acceptance criteria **4**.

#### 4. ImportLog status-update state → `tests/Ghpmv.Core.Tests/ItemImporterLogicTests.cs` *(extend)*

- **Source**: `src/Ghpmv.Core/Import/ImportLog.cs` (`StatusUpdates`, `PendingStatusUpdates`, `PendingStatusUpdateOperation`, `LoadAsync` validation)
- **Test File**: `tests/Ghpmv.Core.Tests/ItemImporterLogicTests.cs` (existing, 16 tests — already the home of
  `ImportLog_round_trips_through_the_file`, `ImportLog_load_returns_null_when_missing_and_rejects_corrupt_content`,
  `ImportLog_rejects_legacy_schema_instead_of_ignoring_it`)
- **Test Class**: `ItemImporterLogicTests`

**New tests (all `public async Task`, temp directory + `try/finally` delete):**

1. `ImportLog_round_trips_status_update_mappings_and_pending_operations` — saves a log with
   `StatusUpdates["0"] = "SU_1"` and one `PendingStatusUpdateOperation { OperationId, ProjectId, ExistingStatusUpdateIds }`,
   reloads, asserts every value survives and `CurrentSchemaVersion` is still **2**.
2. `ImportLog_without_status_update_sections_loads_with_empty_maps` — a schema-2 JSON literal written before #46
   (no `statusUpdates`/`pendingStatusUpdates` keys) loads successfully with both dictionaries empty (backward
   compatible; **no** `InvalidDataException`).
3. `ImportLog_rejects_duplicate_status_update_target_ids` — two keys mapping to the same target id ⇒
   `InvalidDataException` with message
   `import-log.json contains inconsistent status update mappings and cannot be resumed safely.`
4. `ImportLog_rejects_keys_present_in_both_status_update_maps` — `"0"` in both `StatusUpdates` and
   `PendingStatusUpdates` ⇒ same `InvalidDataException` message as above.
5. `ImportLog_rejects_non_integer_or_negative_status_update_keys` — keys `"a"` and `"-1"` ⇒ `InvalidDataException`.
6. `ImportLog_rejects_blank_status_update_target_ids` — `StatusUpdates["0"] = "  "` ⇒ `InvalidDataException`.
7. `ImportLog_rejects_pending_status_update_with_missing_operation_id_or_existing_ids` — blank `OperationId`,
   blank `ProjectId`, and null/blank-containing `ExistingStatusUpdateIds` each ⇒ `InvalidDataException`.

> Covers the persistence half of acceptance criterion **3**.

#### 5. FixtureProjectBuilder → `tests/Ghpmv.Core.Tests/FixtureProjectBuilderTests.cs` *(extend)*

- **Source**: `src/Ghpmv.Core/Fixtures/FixtureProjectBuilder.cs` (`CreateSnapshot`)
- **Test File**: `tests/Ghpmv.Core.Tests/FixtureProjectBuilderTests.cs` (existing, 4 tests)
- **Test Class**: `FixtureProjectBuilderTests`
- **Builder call**: `FixtureProjectBuilder.CreateSnapshot("Fixture", "example/fixture", "octocat", pullRequestNumber: 2)`

**New tests:**

1. `Demo_fixture_exercises_every_status_update_status` — asserts exactly 5 updates and that the status set equals
   `{ COMPLETE, OFF_TRACK, AT_RISK, ON_TRACK, INACTIVE }` (ordinal comparison, no duplicates).
2. `Demo_fixture_status_updates_are_in_strictly_descending_created_at_order` — parses `CreatedAt` invariantly and
   asserts strict descending (`2026-01-05` → `2026-01-01`), i.e. newest-first as exported.
3. `Demo_fixture_status_updates_mix_null_and_populated_dates` — asserts at least one update has
   `StartDate is null`, one has `TargetDate is null`, one has **both** null (`INACTIVE`), and one has **both**
   populated (`COMPLETE`, start `2026-01-01`, target `2026-04-15`).
4. `Demo_fixture_status_update_bodies_include_multi_line_and_markdown_content` — asserts one body contains a
   newline (`ON_TRACK`, multi-line) and one contains Markdown syntax (`INACTIVE`).
5. `Demo_fixture_status_updates_populate_every_snapshot_property_somewhere` — reflection loop over
   `typeof(StatusUpdateSnapshot).GetProperties()` asserting each property is non-null on at least one update
   (matches this file's existing reflection-driven completeness style), including `Creator == viewerLogin` and
   an update where `UpdatedAt != CreatedAt`.

> Covers acceptance criterion **7** (data half; the template wrap/unwrap half is Phase 3/4).

### Success Criteria

- [ ] `tests/Ghpmv.Core.Tests/StatusUpdateImporterLogicTests.cs` created; `SnapshotTests.cs`,
      `ProjectVerifierTests.cs`, `ItemImporterLogicTests.cs`, `FixtureProjectBuilderTests.cs` extended
- [ ] 2 repaired tests (`Roundtrip_preserves_all_values`, `SnapshotFile_saves_and_loads_snapshot_json`) pass
- [ ] All new tests pass; build is warning-clean under `-warnaserror`
- [ ] Every async test call passes `TestContext.Current.CancellationToken`
- [ ] `SchemaVersion` assertions still read `1`; `ImportLog.CurrentSchemaVersion` still reads `2`

---

## Phase 2: Mid-layer with `HttpMessageHandler` doubles

### Overview

Everything that talks to `GitHubGraphQLClient` but not to a process. Fixes the *mechanically broken* exporter
stub tests caused by the new `statusUpdates` query, then covers export pagination, importer create/resume/
reconcile semantics (including the deliberate **no content dedupe** behavior), and the template write session.
Client double: `new GitHubGraphQLClient("token", baseUrl: null, handler, (_, _) => Task.CompletedTask)` so retry
delays are instant.

### Files to Test

#### 1. ProjectExporter status updates → `tests/Ghpmv.Core.Tests/ProjectExporterTests.cs` *(extend + repair)*

- **Source**: `src/Ghpmv.Core/Export/ProjectExporter.cs` (`FetchStatusUpdatesAsync`, `StatusUpdatesQueryTemplate`, `StatusUpdatesPageSize = 50`)
- **Test File**: `tests/Ghpmv.Core.Tests/ProjectExporterTests.cs` (existing, 9 `[Fact]/[Theory]`)
- **Test Class**: `ProjectExporterTests`

**Repairs — REQUIRED, these currently fail by construction.** `StubHandler` dequeues responses **positionally**
and the call order is now **metadata → items → statusUpdates → fields**. Every `new StubHandler(...)` needs a
status-updates response inserted **3rd**, and every `RequestBodies.Count` assertion re-counted (the existing
`Assert.Equal(6, handler.RequestBodies.Count)`-style assertions are off by the number of status-update rounds).

1. Add helpers alongside the existing `MetadataResponse`/`FieldsResponse`/`EmptyItemsResponse`:
   `const string EmptyStatusUpdatesResponse` and
   `static string StatusUpdatesResponse(string nodes, bool hasNextPage = false, string? endCursor = null)`.
2. `Export_prefers_configured_visible_fields_from_view_configuration` — **repair** (insert stub, recount).
3. `Export_paginates_project_fields_without_truncating_the_snapshot` — **repair** (insert stub, recount).
4. `Export_rejects_duplicate_field_identity(bool isIssueField, string identityKind)` — **repair** (both
   `[InlineData]` cases; insert stub before the fields response).
5. `Export_fails_instead_of_writing_an_incomplete_snapshot_when_field_enumeration_fails` — **repair**: the failing
   response must stay attached to the **fields** round, so a successful status-updates response is inserted first.
6. **Repair sweep**: every remaining `StubHandler`-based test in the file (all 9 constructions) must be audited for
   the inserted response and the recounted `RequestBodies.Count`. The repair is complete only when no test relies
   on the pre-#46 ordering.

**New tests:**

7. `Export_captures_status_updates_in_reverse_chronological_order` — stubbed response with 3 nodes newest-first;
   asserts `snapshot.StatusUpdates` has 3 entries in the same order with `Body`, `Status`, `StartDate`,
   `TargetDate`, `Creator` (from `creator.login`), `CreatedAt`, `UpdatedAt` all mapped, and that the issued query
   contains `orderBy: { field: CREATED_AT, direction: DESC }`.
8. `Export_paginates_status_updates` — page 1 `hasNextPage: true` + `endCursor: "c1"`, page 2 `hasNextPage: false`;
   asserts all nodes across both pages appear once, in order, and that the second request body carries
   `"after":"c1"` and `first` = 50.
9. `Export_leaves_optional_status_update_dates_and_creator_null` — node with `startDate: null`, `targetDate: null`
   and an **absent** `creator` object ⇒ `StartDate`/`TargetDate`/`Creator` are all `null`, no exception.
10. `Export_sets_an_empty_status_update_list_when_the_project_has_none` — empty `nodes` ⇒
    `Assert.NotNull(snapshot.StatusUpdates)` and `Assert.Empty(...)` (API path never yields `null`).
11. `Export_requests_status_updates_after_items_and_before_fields` — asserts on `handler.RequestBodies` that the
    `statusUpdates(` query appears after the items query and before the fields query (locks the ordering that the
    positional stubs depend on, so a future reorder fails loudly here instead of everywhere).

> Covers acceptance criterion **2**, and the export half of **6**.

#### 2. StatusUpdateImporter core logic → `tests/Ghpmv.Core.Tests/StatusUpdateImporterLogicTests.cs` *(extend — created in Phase 1)*

- **Source**: `src/Ghpmv.Core/Import/StatusUpdateImporter.cs` (`ImportAsync`, `ValidateStatusUpdates`, `ValidateLogAgainstSnapshot`)
- **Test Class**: `StatusUpdateImporterLogicTests`
- **Double**: private `sealed class StatusUpdateHandler(...) : HttpMessageHandler` recording
  `RequestBodies`, `CreateMutationCount`, `ClientMutationIds`, mirroring `ItemImporterResumeTests.ResumeHandler`.

**New tests:**

1. `Import_creates_status_updates_oldest_first` — snapshot stores newest-first; asserts the `createProjectV2StatusUpdate`
   mutations are issued in ascending `CreatedAt` order (`OrderBy(CreatedAt).ThenBy(SourceIndex)`) and that the
   `ImportLog` keys are the **snapshot** indices as invariant strings (so the oldest update maps to the *last* key).
2. `Import_sends_the_attributed_body_and_optional_dates` — asserts the mutation variables contain
   `BuildImportedBody(update)` verbatim, the raw `Status`, and `startDate`/`targetDate` as `null` when the source
   values are null.
3. `Import_does_nothing_when_the_snapshot_predates_status_updates` — `snapshot.StatusUpdates is null` ⇒
   zero HTTP requests, result `Created == 0 && Resumed == 0 && AlreadyComplete == 0`, and progress contains exactly
   `Status updates were not captured by this schema-v1 snapshot; leaving the target history unchanged.`
4. `Import_does_nothing_when_the_snapshot_has_an_empty_status_update_list` — `[]` ⇒ zero mutations, all-zero result.
5. `Import_rejects_unsupported_status_values` — status `"BLOCKED"` at index 1 ⇒ `InvalidDataException`
   `Status update at snapshot sequence 1 has unsupported status 'BLOCKED'.` and **zero** mutations sent.
6. `Import_accepts_every_supported_status` — `[Theory]` over `INACTIVE/ON_TRACK/AT_RISK/OFF_TRACK/COMPLETE`;
   each is accepted and forwarded unchanged.
7. `Import_rejects_invalid_created_at_values` — `CreatedAt = "not-a-date"` at index 0 ⇒ `InvalidDataException`
   `Status update at snapshot sequence 0 has invalid createdAt 'not-a-date'.`, zero mutations.
8. `Import_rejects_a_log_from_a_different_snapshot_or_target` — log fingerprint/`ProjectId` mismatch ⇒
   `InvalidOperationException` containing
   `belongs to a different source snapshot or target project. Use a separate log directory or restore the matching snapshot and target before resuming.`
9. `Import_rejects_log_state_outside_the_snapshot_range` — key `"9"` with a 3-update snapshot ⇒
   `InvalidOperationException` `import-log.json contains status update state that does not match the selected snapshot and target project.`
10. `Import_throws_when_the_mutation_returns_an_empty_status_update_id` — payload with `statusUpdate.id == ""` ⇒
    `GitHubGraphQLException` `createProjectV2StatusUpdate returned an empty status update id.`
11. `Import_reports_progress_for_each_stage_and_a_final_summary` — asserts the emitted progress lines include
    `[1/3] Creating status update at snapshot sequence 2...` and the final
    `Status update import finished: 3 created, 0 resumed, 0 already complete.`

> Covers acceptance criterion **3** (create path) and the API-shape half of **2**/**4**.

#### 3. StatusUpdateImporter resume/reconcile → `tests/Ghpmv.Core.Tests/StatusUpdateImporterResumeTests.cs` *(create)*

- **Source**: `src/Ghpmv.Core/Import/StatusUpdateImporter.cs` (pending persistence, `ReconcilePendingAsync`, `FetchStatusUpdateIdsAsync`)
- **Test File**: `tests/Ghpmv.Core.Tests/StatusUpdateImporterResumeTests.cs` **(net-new file)**
- **Test Class**: `StatusUpdateImporterResumeTests`
- **Double**: `private sealed class ResumeHandler(string directory) : HttpMessageHandler` with toggles
  `Resume`, `FailBeforeMutation`, `FailDefinitively`, `Ambiguous`, and counters `CreateMutationCount`,
  `ClientMutationId`, `PendingWasPresentAtMutation` — modelled on `ItemImporterResumeTests.ResumeHandler`.
- **Flow template**: `await Assert.ThrowsAsync<AmbiguousMutationResultException>(...)` →
  `var log = await ImportLog.LoadAsync(directory, ct)` → inspect `PendingStatusUpdates` → flip handler flag →
  re-run → assert counts.

**New tests:**

1. `Pending_status_update_is_persisted_before_the_mutation_is_sent` — asserts `PendingWasPresentAtMutation` is
   `true`, i.e. `import-log.json` on disk already contains the `PendingStatusUpdateOperation` (with `OperationId`,
   `ProjectId`, `ExistingStatusUpdateIds` baseline) **at the moment** the create mutation is observed —
   a crash here is resumable.
2. `Ambiguous_create_survives_in_the_log_and_is_reconciled_by_target_id_without_resending` — first run throws
   `AmbiguousMutationResultException`; the pending record survives; second run reconciles to the single new target
   id, asserts `handler.CreateMutationCount == 1`, `result.Created == 0`, `result.Resumed == 1`, and the log now
   holds the mapping in `StatusUpdates` with `PendingStatusUpdates` empty.
3. `Reconciliation_rejects_multiple_new_candidates` — two ids absent from the baseline ⇒
   `InvalidOperationException` `Pending status update operation '{id}' matches multiple new target updates. Reconcile the target manually.`
4. `Reconciliation_that_finds_nothing_refuses_to_create_a_possible_duplicate` — no new candidate after 3 attempts ⇒
   `InvalidOperationException` ending `could not be reconciled by target id. Refusing to create a possible duplicate.`
   and **no** additional create mutation.
5. `Reconciliation_baseline_excludes_ids_already_mapped_in_the_log` — a previously imported update's id is in
   `StatusUpdates` but not in `ExistingStatusUpdateIds`; asserts it is still excluded from the candidate set so
   reconciliation resolves to exactly one.
6. `Definitive_mutation_failure_clears_the_pending_operation` — non-ambiguous GraphQL failure ⇒ the exception
   propagates and `PendingStatusUpdates` is empty on reload (no stale pending).
7. `Failure_before_the_mutation_clears_the_pending_operation` — transport failure before send ⇒ same assertion.
8. `Second_run_with_the_log_reports_already_complete_without_sending_mutations` — after a clean first run, a second
   `ImportAsync` over the same directory asserts `CreateMutationCount` unchanged, `result.Created == 0`,
   `result.AlreadyComplete == <n>`, `result.Resumed == 0`, and progress line
   `[1/3] Status update at snapshot sequence 2: already complete.`
9. `Second_run_without_the_log_creates_duplicates_because_there_is_no_content_dedupe` — deletes `import-log.json`
   between runs; asserts the second run creates **the full set again** (`result.Created == <n>`, total
   `CreateMutationCount == 2 * n`) — proving resume is **by persisted target id only** and bodies are never
   compared for dedupe.
10. `Pending_operation_for_a_different_project_is_rejected` — pending `ProjectId` ≠ `target.ProjectId` ⇒
    `InvalidOperationException` `Pending status update operation '{OperationId}' does not match target project '{ProjectId}'.`
11. `Existing_target_status_updates_are_left_untouched` — the target already has 2 unrelated status updates in the
    baseline; asserts no update/delete mutation is ever issued, the pre-existing ids never appear in
    `log.StatusUpdates`, and only `createProjectV2StatusUpdate` operations are sent.
12. `Status_update_id_fetch_paginates_the_target_history` — `node.statusUpdates(first: 100, ...)` paged twice;
    asserts the baseline set contains ids from both pages (otherwise reconciliation would see phantom "new" ids).

> Covers acceptance criterion **3** in full (resume-by-id, crash-resume, no-dedupe, existing left alone).

#### 4. ProjectTemplateWriteSession → `tests/Ghpmv.Core.Tests/ProjectTemplateWriteSessionTests.cs` *(create)*

- **Source**: `src/Ghpmv.Core/Import/ProjectTemplateWriteSession.cs`
- **Test File**: `tests/Ghpmv.Core.Tests/ProjectTemplateWriteSessionTests.cs` **(net-new file)**
- **Test Class**: `ProjectTemplateWriteSessionTests`
- **Scope reminder**: only the unmark/remark seam. **No** issue-#47 template-migration scenarios.

**New tests:**

1. `Prepare_leaves_a_non_template_project_alone` — `node.template == false` ⇒ `RestorationRequired == false`,
   exactly **one** request (the query), zero mutations.
2. `Prepare_unmarks_an_existing_template_project` — `template == true` ⇒ `unmarkProjectV2AsTemplate` is issued with
   the project id, `RestorationRequired == true`.
3. `Restore_remarks_the_project_as_a_template` — after a `RestorationRequired` prepare, `RestoreAsync` issues
   `markProjectV2AsTemplate` exactly once.
4. `Restore_is_idempotent_when_called_twice` — two `RestoreAsync` calls (the CLI happy path + `finally`) ⇒ still
   exactly one `markProjectV2AsTemplate` mutation.
5. `Restore_is_a_no_op_when_restoration_was_not_required` — non-template project ⇒ `RestoreAsync` sends nothing.
6. `Prepare_throws_when_the_target_node_is_missing` — `data.node == null` ⇒ `GitHubGraphQLException` with message
   `Target project 'PVT_missing' was not found while checking template state.`
7. `Prepare_and_restore_emit_the_documented_progress_messages` — asserts the exact strings
   `Temporarily unmarking the target project as a template before status update writes...` and
   `Restoring the target project's template state as the final import stage...`
8. `Template_mutations_use_the_idempotent_retry_policy_and_required_result_path` — a first transient failure is
   retried and succeeds (proving `MutationRetryPolicy.Idempotent`), and a payload missing `projectV2.id` fails.

> Covers acceptance criterion **5** (unit half). Note in the implementation: `ProjectImporter.ApplySnapshotAsync`
> must **not** be asserted to invoke this session — sequencing lives in the CLI / fixture builder (Phase 3/4).
> Add `Apply_snapshot_does_not_touch_template_state` to `ProjectImporterLogicTests.cs` *(extend, 1 test)* asserting
> `ApplySnapshotAsync` issues no `markProjectV2AsTemplate`/`unmarkProjectV2AsTemplate` mutation.

### Success Criteria

- [ ] `StatusUpdateImporterResumeTests.cs` and `ProjectTemplateWriteSessionTests.cs` created
- [ ] All 9 repaired `ProjectExporterTests` constructions pass with recounted `RequestBodies.Count`
- [ ] No-dedupe behavior asserted in **both** directions (with log ⇒ `AlreadyComplete`; without log ⇒ duplicates)
- [ ] Every async call passes `TestContext.Current.CancellationToken`; the delay hook is stubbed so no test sleeps
- [ ] No mocking library added

---

## Phase 3: CLI process-level stdout & orchestration ordering

### Overview

`CliImportTests` spawns `ghpmv.dll` against an `HttpListener` stub and asserts **exact** stdout strings — the
machine-readable contract. The new `statusUpdates` export query and the new `status-updates:` line break both the
request counts and the output assertions. Repair first, then lock the new contract: existing lines byte-for-byte
unchanged, new line additive on non-skip paths only.

### Files to Test

#### 1. `tests/Ghpmv.Core.Tests/CliImportTests.cs` *(extend + repair)*

- **Source**: `src/Ghpmv.Cli/Program.cs` (import stdout block, template session, `finally` restore)
- **Test Class**: `CliImportTests` (existing, 6 tests)
- **Harness**: `RunCliAsync` (`import --org target --in <dir> --token dummy-token --target-base-url <stub> --no-update-check`),
  `RunVerifyCliAsync`, `GraphQlStubServer` with index-clamped responses and `List<string> RequestBodies`.

**Repairs — REQUIRED:**

1. `Conflict_update_emits_stable_result_and_applies_project_mutation` — **repair**: `Assert.Equal(3, server.RequestBodies.Count)`
   must be recounted for the extra `statusUpdates` query; then **extend** with
   `Assert.Contains("status-updates: created=0 resumed=0 already-complete=0", result.Output, StringComparison.Ordinal)`.
   The pre-existing assertions must remain **byte-for-byte identical**: `"result=updated project=42"`,
   `"items: created=0 resumed=0 already-complete=0 skipped=0 warnings=0"`, `"views: imported=0 warnings=0"`.
2. **Repair sweep** — every remaining test in `CliImportTests` that asserts a `RequestBodies.Count` or an exact
   stdout block (including the `RunVerifyCliAsync` tests asserting `"Project: Match"` / the category table) must be
   re-counted against the new export round. The clamped stub means a missing response silently repeats the last
   one, so each repaired test must assert the **count**, not just the content.

**New tests:**

3. `Import_prints_the_status_update_summary_line_after_items` — asserts the stdout line order is exactly
   `<url>` → `result=…` → `items: …` → `status-updates: created=… resumed=… already-complete=…` → `views: …`
   and that no existing line's text changed.
4. `Import_skip_path_does_not_print_the_status_update_line` — conflict `skip` ⇒ stdout is only the URL and
   `result=skipped project=42`; `Assert.DoesNotContain("status-updates:", result.Output, StringComparison.Ordinal)`
   (additive on non-skip paths **only**).
5. `Import_marks_and_restores_the_template_only_when_the_snapshot_has_status_updates` — two runs: with
   `snapshot.StatusUpdates == []`/`null` ⇒ **no** `unmarkProjectV2AsTemplate`/`markProjectV2AsTemplate` in
   `server.RequestBodies`; with a non-empty list on a template target ⇒ both mutations present exactly once.
6. `Import_restores_the_template_after_downstream_importers` — asserts request ordering in `server.RequestBodies`:
   `unmarkProjectV2AsTemplate` precedes `createProjectV2StatusUpdate`, and `markProjectV2AsTemplate` is the **last**
   mutation, after the view/workflow import stage (restore is the final orchestration stage).
7. `Import_reports_a_template_restore_failure_on_stderr_without_changing_stdout` — the stub fails
   `markProjectV2AsTemplate`; asserts stderr contains
   `error: failed to restore the target project's template state:` while stdout still carries the unchanged
   `result=` / `items:` / `status-updates:` / `views:` lines (the `finally` path never corrupts the contract).

> Covers acceptance criteria **6**, the orchestration half of **5**, and the template wrap/unwrap half of **7**.

### Success Criteria

- [ ] All 6 existing `CliImportTests` pass after re-count/repair, with the pre-#46 stdout strings unchanged
- [ ] New `status-updates:` line asserted present on non-skip paths and absent on the skip path
- [ ] Template mutations asserted conditional on `snapshot.StatusUpdates` being non-empty, and restore asserted last
- [ ] Every async call passes `TestContext.Current.CancellationToken`

---

## Phase 4: Live GitHub API integration tests

### Overview

Real-credential E2E in `tests/Ghpmv.Integration.Tests`. Because **GitHub exposes no documented delete for an
individual status update**, every *write* test must target a **throwaway** project created by
`TemporaryProjectFixture` — never the shared fixture project — and must clean up in `finally`. Read-only export
assertions may use the shared source fixture. Parallelization is already disabled assembly-wide.

**Credential idiom (declare per class, verbatim):**

```
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

Titles: `"ghpmv-status-update-test-" + Guid.NewGuid().ToString("N")`. Log dirs:
`IntegrationTestSettings.CreateOperationLogDirectory()` with `TryDeleteDirectory(...)` in `finally`.
No `IAsyncLifetime`, no class fixtures — per-test `try/finally` only.

### Files to Test

#### 1. `tests/Ghpmv.Integration.Tests/ProjectExporterTests.cs` *(extend — read-only, shared fixture OK)*

1. `Export_captures_fixture_status_updates_in_reverse_chronological_order` — exports the shared source fixture and
   asserts 5 status updates whose statuses are, newest-first,
   `COMPLETE, OFF_TRACK, AT_RISK, ON_TRACK, INACTIVE`, with strictly descending `CreatedAt`, at least one null
   `StartDate`, one null `TargetDate`, and a non-null `Creator` — a real-API guard against silent empty passes.

#### 2. `tests/Ghpmv.Integration.Tests/ProjectImporterTests.cs` *(extend — throwaway projects)*

Each test: `TemporaryProjectFixture.CreateAsync(client, TargetOrg, NewTestTitle(), ct)` for the target (and, where
a distinct source is needed, a second temporary project), `finally` delete both + `TryDeleteDirectory(logDirectory)`.

2. `Status_updates_round_trip_into_a_temporary_project_with_every_status_and_date_shape` — imports the 5-update
   fixture snapshot into a throwaway target via `StatusUpdateImporter.ImportAsync(snapshot, result, logDirectory, ct)`,
   re-exports, and asserts all five statuses are present, `StartDate`/`TargetDate` round-trip for both the
   populated and the `null` cases, and `ImportLog.StatusUpdates.Count == 5`.
3. `Status_updates_are_created_oldest_first_and_re_export_in_reverse_chronological_order` — asserts the re-exported
   target sequence matches the source sequence position-for-position (proving oldest-first creation produced the
   correct newest-first server ordering).
4. `Status_update_bodies_carry_the_source_attribution_note` — asserts each re-exported body equals
   `StatusUpdateImporter.BuildImportedBody(sourceUpdate)`, including the `@{creator}` mention and source
   `CreatedAt`, and that Markdown/multi-line content survives the round trip verbatim.
5. `Status_update_rerun_creates_nothing_and_reports_already_complete` — runs `ImportAsync` a **second** time against
   the same log directory and target; asserts `result2.Created == 0`, `result2.AlreadyComplete == 5`,
   `result2.Resumed == 0`, and that a re-export still returns exactly 5 updates (no duplicates) — the live proof of
   resume-by-persisted-id.

#### 3. `tests/Ghpmv.Integration.Tests/VerifyTests.cs` *(extend)*

6. `Status_update_category_matches_after_import_and_mismatches_after_drift` — after importing status updates into a
   throwaway target, guards against silent null passes with `Assert.Equal(5, source.StatusUpdates!.Count)`, polls
   `ProjectVerifier.VerifyAsync` until `StatusUpdate: Match`, then drifts the target by creating one extra status
   update and asserts the category becomes `Mismatch` with a
   `status update count mismatch (source 5, target 6)` error. (Drift is additive because deletion is unavailable —
   hence the throwaway project.)

#### 4. `tests/Ghpmv.Integration.Tests/ProjectTemplateWriteSessionTests.cs` *(create)*

7. `Template_state_is_restored_after_status_update_writes` — `TemporaryProjectFixture.CreateAsync` →
   `markProjectV2AsTemplate` → `ProjectTemplateWriteSession.PrepareAsync` (asserts `RestorationRequired == true`) →
   `StatusUpdateImporter.ImportAsync` succeeds (proving the unmark was necessary and effective) → `RestoreAsync` →
   re-query `node.template` and assert `true`; `finally` deletes the project.
8. `Prepare_on_a_non_template_project_requires_no_restoration` — same fixture without marking; asserts
   `RestorationRequired == false`, the import still succeeds, and the project's `template` flag stays `false`.

> **Out of scope reminder**: no other template behavior is exercised — issue #47 is not implemented.

### Open item (decide during implementation, do not expand scope)

`IntegrationFixtureSnapshot.NormalizeKnownSnapshot` currently rewrites only `Fields`/`Items`. If re-exported
status-update bodies (which carry the attribution note) need normalization for reuse as a *source* snapshot, add a
matching case and cover it in `tests/Ghpmv.Integration.Tests/IntegrationFixtureSnapshotTests.cs` (token-free,
`public void`) as `Normalize_known_snapshot_leaves_status_update_bodies_unchanged` — otherwise record the decision
in `status.md` and skip it.

### Success Criteria

- [ ] `tests/Ghpmv.Integration.Tests/ProjectTemplateWriteSessionTests.cs` created; three existing files extended
- [ ] Every new live test skips cleanly via `Assert.SkipWhen` when `GHPMV_TEST_TOKEN` is absent
- [ ] Every write test uses `TemporaryProjectFixture` throwaway projects, never the shared fixture, and deletes in `finally`
- [ ] Rerun non-duplication asserted live (`Created == 0`, `AlreadyComplete == 5`, still 5 on re-export)
- [ ] Every async call passes `TestContext.Current.CancellationToken`
- [ ] No temp log directory or project leaks after a full run

---

## CancellationToken checklist (acceptance criterion 9 — explicit, per phase)

Treat this as a review gate, not an implementation detail. Before closing each phase, confirm:

- [ ] Every `await` on a production API in a new/repaired test passes an explicit token —
      `TestContext.Current.CancellationToken` — never `default`, never `CancellationToken.None`
      (the one exception: asserting the CLI/fixture `finally` restore path, which deliberately uses
      `CancellationToken.None`, and that fact is itself asserted).
- [ ] `ImportLog.LoadAsync` / `SaveAsync`, `SnapshotFile.SaveAsync` / `LoadAsync`,
      `ProjectExporter.ExportAsync`, `StatusUpdateImporter.ImportAsync`,
      `ProjectTemplateWriteSession.PrepareAsync` / `RestoreAsync`, and `ProjectVerifier.VerifyAsync`
      calls all carry the token.
- [ ] `await foreach` over `QueryPaginatedAsync` in any helper uses `.WithCancellation(TestContext.Current.CancellationToken)`
      or passes the token argument.
- [ ] Live tests pass the token to `TemporaryProjectFixture.CreateAsync` / `DeleteAllByTitleAsync` and to every
      raw `client.QueryAsync` / `client.MutationAsync` helper call.

## Acceptance-criteria → test map (for `status.md`)

| # | Criterion | Tests |
|---|---|---|
| 1 | Nullable & backward compatible `StatusUpdates`, `SchemaVersion` stays 1 | `SnapshotTests.Roundtrip_preserves_status_updates`, `SnapshotTests.Deserialize_snapshot_without_status_updates_yields_null`, `SnapshotTests.Serialized_json_keeps_schema_version_one_when_status_updates_are_present`, `SnapshotTests.Snapshot_with_empty_status_update_list_round_trips_as_empty_not_null`, repaired `Roundtrip_preserves_all_values` |
| 2 | Export via cursor pagination, reverse-chronological, null-safe optionals | `ProjectExporterTests.Export_captures_status_updates_in_reverse_chronological_order`, `…Export_paginates_status_updates`, `…Export_leaves_optional_status_update_dates_and_creator_null`, `…Export_sets_an_empty_status_update_list_when_the_project_has_none`, `…Export_requests_status_updates_after_items_and_before_fields`, live `Export_captures_fixture_status_updates_in_reverse_chronological_order` |
| 3 | Oldest-first create, attribution note, pending persisted pre-mutation, resume by id only, existing left alone | `StatusUpdateImporterLogicTests.BuildImportedBody_*` (5), `…Import_creates_status_updates_oldest_first`, `…Import_sends_the_attributed_body_and_optional_dates`, `StatusUpdateImporterResumeTests.Pending_status_update_is_persisted_before_the_mutation_is_sent`, `…Ambiguous_create_survives_in_the_log_and_is_reconciled_by_target_id_without_resending`, `…Second_run_with_the_log_reports_already_complete_without_sending_mutations`, `…Second_run_without_the_log_creates_duplicates_because_there_is_no_content_dedupe`, `…Existing_target_status_updates_are_left_untouched`, `ItemImporterLogicTests.ImportLog_*` (7) |
| 4 | Verify compares sequence/body/status/dates, not creator/createdAt; additive category | `ProjectVerifierTests.Status_update_*` (11), esp. `Status_update_creator_and_created_at_are_not_compared` and `Status_updates_are_not_compared_when_the_source_predates_capture` |
| 5 | Template unmark/remark seam, idempotent, conditional, missing node throws, not in `ApplySnapshotAsync` | `ProjectTemplateWriteSessionTests` (8), `ProjectImporterLogicTests.Apply_snapshot_does_not_touch_template_state`, `CliImportTests.Import_marks_and_restores_the_template_only_when_the_snapshot_has_status_updates`, `…Import_restores_the_template_after_downstream_importers`, live `Template_state_is_restored_after_status_update_writes` |
| 6 | CLI stdout unchanged + additive new line | repaired `CliImportTests.Conflict_update_emits_stable_result_and_applies_project_mutation` + repair sweep, `…Import_prints_the_status_update_summary_line_after_items`, `…Import_skip_path_does_not_print_the_status_update_line`, `…Import_reports_a_template_restore_failure_on_stderr_without_changing_stdout` |
| 7 | Fixture covers all statuses/dates/Markdown/order + template wrap | `FixtureProjectBuilderTests` (5 new), plus `CliImportTests.Import_restores_the_template_after_downstream_importers` for the wrap/unwrap |
| 8 | Live E2E on throwaway projects incl. rerun non-duplication | Phase 4 tests 1–8 |
| 9 | CancellationToken threaded everywhere | CancellationToken checklist above (per-phase gate) |
| 10 | Exact test names stated | this document |
