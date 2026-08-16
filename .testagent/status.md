# Test Implementation Status — Phases 0 & 1 (issue #46)

## Final parent-agent validation

The production agent fixed the schema-2 log compatibility blocker, hardened
durable template restoration across process termination and early resume
failures, preserved nullable status values, isolated the live round trip with
unique source/target Projects, and prevented status-only fixture logs from
replaying items.

- `dotnet build Ghpmv.slnx -c Release --no-restore -warnaserror`: 0 warnings, 0 errors.
- `Ghpmv.Core.Tests`: 390 passed, 0 failed.
- Deterministic `Ghpmv.Browser.Tests` (`Category!=E2E`): 73 passed, 0 failed.
- `Ghpmv.Integration.Tests`: 2 passed, 31 credential-gated skips, 0 failed.
- Final high-confidence diff review findings were fixed and covered by focused
  regression tests for old logs, nullable status, template restoration state,
  CLI failure paths, and status-only fixture logs.

Run: 2026-08-16 (JST). Scope: `.testagent/plan.md` Phase 0 (build gate) + Phase 1 (leaf/pure tests).

## Phase 0 — build-break gate: CLEARED

`dotnet build Ghpmv.slnx -c Debug --no-incremental` → **exit 0, 0 errors, 0 warnings.**

The 61-error blocker recorded in research §11 (`CompareStatusUpdates` nested inside `CompareItems`
in `src/Ghpmv.Core/Verify/ProjectVerifier.cs` L894, CS1022/CS8803/CS0106) has been **fixed by the
parallel production agent**. `CompareStatusUpdates` is now a class-level member at L881. No `src/**`
or `docs/**` file was touched by the test work (verified via `git diff`).

## Phase 1 — leaf & pure tests: COMPLETE (32 new tests, 31 passing)

| File | Action | New tests |
|---|---|---:|
| `tests/Ghpmv.Core.Tests/SnapshotTests.cs` | extend + 2 repairs | 4 |
| `tests/Ghpmv.Core.Tests/StatusUpdateImporterLogicTests.cs` | **create** | 5 |
| `tests/Ghpmv.Core.Tests/ProjectVerifierTests.cs` | extend | 11 |
| `tests/Ghpmv.Core.Tests/ItemImporterLogicTests.cs` | extend | 7 |
| `tests/Ghpmv.Core.Tests/FixtureProjectBuilderTests.cs` | extend | 5 |

Harness discovery (`dotnet test Ghpmv.slnx --no-build --list-tests`): **+32**, matching exactly.
All edits are additive — `git diff -U0` reports **zero removed lines** in the four extended files.

## Blocker for the production agent: `ImportLog` schema-2 backward compatibility

`ImportLog.LoadAsync` rejects a schema-2 log whose `statusUpdates` / `pendingStatusUpdates`
sections are absent, throwing:

```
import-log.json contains malformed item state and cannot be resumed safely.
```

Cause: `ImportLogJsonContext` deserializes **omitted** collection properties as `null` (the
`= new(StringComparer.Ordinal)` initializers do not run), and the new `LoadAsync` guard
(`src/Ghpmv.Core/Import/ImportLog.cs` L84-85) treats `null` as malformed. Verified directly:
for a log body containing `items`/`itemStates`/`pendingDrafts`/`pendingContents` but no
status-update sections, `StatusUpdates is null` → `True`, `PendingStatusUpdates is null` → `True`.

Impact: `CurrentSchemaVersion` deliberately stays **2**, so a log written by a pre-#46 ghpmv is
still in-contract, but can no longer be resumed after upgrading — the user must delete the log
and risk duplicate items.

Consequences in the test suite (both **production-owned**, not test defects):

- `ItemImporterLogicTests.ImportLog_without_status_update_sections_loads_with_empty_maps` (new,
  plan §Phase 1 file 4 test 2 — specified as "backward compatible; **no** `InvalidDataException`")
  fails, documenting the gap.
- `ItemImporterLogicTests.ImportLog_load_returns_null_when_missing_and_rejects_corrupt_content`
  (**pre-existing**, untouched) fails: its "inconsistent item mappings" literal omits the new
  sections and now trips the malformed guard first.

Suggested production fix: default the two new collections when absent (or relax the guard to
`?? []`), keeping schema version 2.

## Other pre-existing failures — deferred to Phases 2 & 3 (not Phase 1 scope)

The new `statusUpdates` export round shifted every positional `StubHandler` queue, exactly as
plan §Phase 2 "Repairs — REQUIRED" anticipated. Currently failing and owned by later phases:

- `ProjectExporterTests` — 10 tests (Phase 2 repair sweep).
- `ProjectVerifierTests.VerifyAsync_applies_post_export_hook_before_comparison` — 1 test; its
  3-response stub queue (metadata → items → fields) needs a status-updates response inserted 3rd.
  Not listed in the plan; add it to the Phase 2 repair sweep.
- `CliImportTests.Verify_reports_category_statuses_and_writes_consistent_json` — 1 test (Phase 3
  repair sweep, `RequestBodies.Count` recount).

Full `Ghpmv.Core.Tests` run: **325 passed / 14 failed / 339 total**. 13 of the 14 failures are
pre-existing production-side breakage; 1 is the new plan-mandated backward-compatibility test above.

---

# Test Implementation Status — Phase 2 (issue #46)

Run: 2026-08-16 (JST). Scope: `.testagent/plan.md` Phase 2 "Mid-layer with `HttpMessageHandler` doubles".

## Pre-flight: the two RED tests are unchanged and still production-owned

Re-confirmed before starting (`dotnet test --filter FullyQualifiedName~ItemImporterLogicTests`):

- `ImportLog_without_status_update_sections_loads_with_empty_maps` → `InvalidDataException : import-log.json
  contains malformed item state and cannot be resumed safely.`
- `ImportLog_load_returns_null_when_missing_and_rejects_corrupt_content` → `Assert.Contains` failure,
  `"inconsistent item mappings"` not found (the malformed guard fires first).

Byte-for-byte the failures recorded for Phase 1. **Neither test was touched**; both remain red and continue to
document the schema-2 backward-compatibility bug awaiting a `src/**` fix.

## Phase 2 — COMPLETE (41 new tests, 41 passing; 11 repairs, all green)

| File | Action | New tests | Repairs |
|---|---|---:|---:|
| `tests/Ghpmv.Core.Tests/ProjectExporterTests.cs` | extend + repair | 5 | 9 stub constructions |
| `tests/Ghpmv.Core.Tests/StatusUpdateImporterLogicTests.cs` | extend | 15 (10 `[Fact]` + 5 `[Theory]` cases) | 0 |
| `tests/Ghpmv.Core.Tests/StatusUpdateImporterResumeTests.cs` | **create** | 12 | 0 |
| `tests/Ghpmv.Core.Tests/ProjectTemplateWriteSessionTests.cs` | **create** | 9 | 0 |
| `tests/Ghpmv.Core.Tests/ProjectVerifierTests.cs` | repair only | 0 | 1 |

`dotnet build tests/Ghpmv.Core.Tests/Ghpmv.Core.Tests.csproj -c Debug --no-incremental` → **0 warnings, 0 errors**.

Phase-2 filter run (`ProjectExporterTests|StatusUpdateImporterLogicTests|StatusUpdateImporterResumeTests|ProjectTemplateWriteSessionTests|ProjectVerifierTests`):
**114 passed / 0 failed / 114 total.**

Full `Ghpmv.Core.Tests`: **377 passed / 3 failed / 380 total** (was 325/14/339). The 3 remaining failures are the
two production-owned `ImportLog` tests above plus `CliImportTests.Verify_reports_category_statuses_and_writes_consistent_json`
(Phase 3 repair sweep).

Harness discovery (`dotnet test Ghpmv.slnx -c Debug --no-build --list-tests`, from the repo root): **484 cases**,
i.e. **+41** — `StatusUpdateImporterResumeTests` 12, `ProjectTemplateWriteSessionTests` 9,
`StatusUpdateImporterLogicTests` 20 (5 from Phase 1 + 15 new), `ProjectExporterTests` 23 (15 Core + 8 Integration).
Both new files are picked up by the SDK-style glob; no `.slnx`/`Compile Include` edit was needed.

### Repairs (positional stub queue / recounts only — no assertion weakened)

Export now issues **metadata → items → statusUpdates → fields**. A status-updates response was inserted 3rd in all
9 `new StubHandler(...)` constructions in `ProjectExporterTests`, via the new helpers `EmptyStatusUpdatesResponse`
and `StatusUpdatesResponse(nodes, hasNextPage, endCursor)`:

1. `Export_prefers_configured_visible_fields_from_view_configuration`
2. `Export_reads_linked_issue_field_identity_and_definition_directly_from_project_fields` — recount 3 → 4, body index 2 → 3
3. `Export_paginates_project_fields_without_truncating_the_snapshot` — recount 4 → 5, cursor body index 3 → 4
4. `Export_identifies_unset_linked_issue_field_without_item_value_evidence`
5. `Export_rejects_issue_field_without_linked_definition`
6. `Export_rejects_mismatched_linked_issue_field_definition`
7. `Export_rejects_duplicate_field_identity(bool, string)` — both `[InlineData]` cases
8. `Export_fails_instead_of_writing_an_incomplete_snapshot_when_field_enumeration_fails` — success inserted before the 4 failing **fields** rounds; recount 6 → 7
9. `Export_reads_ordinary_multi_select_field_definitions_and_item_values`

`git diff -U0` on the two repaired files removes **only** the 3 recounted assertion lines and the one stub payload
below — no test method was deleted or rewritten.

### Repair of `ProjectVerifierTests.VerifyAsync_applies_post_export_hook_before_comparison`

Flagged by Phase 1 and missing from the plan's repair list. Two defects, both fixed:

1. the status-updates response was inserted 3rd (the #46 cause), **and**
2. its **fields** stub omitted `pageInfo`, which `QueryPaginatedAsync` requires.

(2) is **pre-existing breakage on `main`**: verified by running this exact test in a detached worktree at
`HEAD` (9a8ea73), where it fails identically with `KeyNotFoundException` at `GitHubGraphQLClient.cs:267`. It was
therefore never passing, independent of #46. Adding `"pageInfo":{"hasNextPage":false,"endCursor":null}` to that
stub is a payload completion only; every assertion in the test is untouched and the test now passes.

> A `git worktree` at `../head-check` was created for that baseline check and left in place (removing it was not
> authorized). Clean up with `git worktree remove ../head-check` when convenient.

### New tests

`ProjectExporterTests` — `Export_captures_status_updates_in_reverse_chronological_order`,
`Export_paginates_status_updates`, `Export_leaves_optional_status_update_dates_and_creator_null`,
`Export_sets_an_empty_status_update_list_when_the_project_has_none`,
`Export_requests_status_updates_after_items_and_before_fields`.

`StatusUpdateImporterLogicTests` — `Import_creates_status_updates_oldest_first`,
`Import_sends_the_attributed_body_and_optional_dates`,
`Import_does_nothing_when_the_snapshot_predates_status_updates`,
`Import_does_nothing_when_the_snapshot_has_an_empty_status_update_list`,
`Import_rejects_unsupported_status_values`, `Import_accepts_every_supported_status` (`[Theory]` ×5),
`Import_rejects_invalid_created_at_values`, `Import_rejects_a_log_from_a_different_snapshot_or_target`,
`Import_rejects_log_state_outside_the_snapshot_range`,
`Import_throws_when_the_mutation_returns_an_empty_status_update_id`,
`Import_reports_progress_for_each_stage_and_a_final_summary`.

`StatusUpdateImporterResumeTests` — `Pending_status_update_is_persisted_before_the_mutation_is_sent`,
`Ambiguous_create_survives_in_the_log_and_is_reconciled_by_target_id_without_resending`,
`Reconciliation_rejects_multiple_new_candidates`,
`Reconciliation_that_finds_nothing_refuses_to_create_a_possible_duplicate`,
`Reconciliation_baseline_excludes_ids_already_mapped_in_the_log`,
`Definitive_mutation_failure_clears_the_pending_operation`,
`Failure_before_the_mutation_clears_the_pending_operation`,
`Second_run_with_the_log_reports_already_complete_without_sending_mutations`,
`Second_run_without_the_log_creates_duplicates_because_there_is_no_content_dedupe`,
`Pending_operation_for_a_different_project_is_rejected`,
`Existing_target_status_updates_are_left_untouched`,
`Status_update_id_fetch_paginates_the_target_history`.

`ProjectTemplateWriteSessionTests` — `Prepare_leaves_a_non_template_project_alone`,
`Prepare_unmarks_an_existing_template_project`, `Restore_remarks_the_project_as_a_template`,
`Restore_is_idempotent_when_called_twice`, `Restore_is_a_no_op_when_restoration_was_not_required`,
`Prepare_throws_when_the_target_node_is_missing`,
`Prepare_and_restore_emit_the_documented_progress_messages`,
`Template_mutations_use_the_idempotent_retry_policy_and_required_result_path`,
`Apply_snapshot_does_not_touch_template_state`.

`Apply_snapshot_does_not_touch_template_state` was placed in `ProjectTemplateWriteSessionTests.cs` (per the task
instruction) rather than `ProjectImporterLogicTests.cs` (per the plan note); it drives the public
`ProjectImporter.ImportIntoAsync` because `ApplySnapshotAsync` is private, and asserts that neither
`markProjectV2AsTemplate`/`unmarkProjectV2AsTemplate` nor `createProjectV2StatusUpdate` is ever issued.

## Two plan-specified assertions describe unreachable production code (tests assert real behavior instead)

Both tests keep the plan's exact names, cover the same intent, and pass. Neither production path is *wrong* — the
specific error message the plan predicted is simply pre-empted by an earlier, stricter guard:

1. `Import_throws_when_the_mutation_returns_an_empty_status_update_id` — the plan expected
   `GitHubGraphQLException("createProjectV2StatusUpdate returned an empty status update id.")`. That `?? throw` in
   `StatusUpdateImporter.CreateAsync` (L238) is **dead code**: `GitHubGraphQLClient.HasExpectedMutationResult`
   rejects an empty/null `statusUpdate.id` first, and under `MutationRetryPolicy.Create` that surfaces as
   `AmbiguousMutationResultException`. The test asserts that, plus the safer consequence (pending record survives
   for id-based reconciliation, no duplicate create).
2. `Pending_operation_for_a_different_project_is_rejected` — the plan expected
   `Pending status update operation '{id}' does not match target project '{ProjectId}'.` (`ImportAsync` L82).
   Also unreachable: `ValidateLogAgainstSnapshot` (L176) rejects any pending whose `ProjectId` differs *before*
   the loop starts, with `import-log.json contains status update state that does not match the selected snapshot
   and target project.` The test asserts that message **and** that zero requests reach the wrong project.

No `src/**` or `docs/**` file was modified by Phase 2 (`git status` confirms only the parallel production agent's
edits there).

---

# Test Implementation Status — Phase 3 (issue #46)

Run: 2026-08-16 (JST). Scope: `.testagent/plan.md` Phase 3 "CLI process-level stdout & orchestration ordering".

## Phase 3 — COMPLETE (5 new tests, 5 passing; 2 repairs, both green)

| File | Action | New tests | Repairs |
|---|---|---:|---:|
| `tests/Ghpmv.Core.Tests/CliImportTests.cs` | extend + repair | 5 | 2 |

`dotnet build tests/Ghpmv.Core.Tests/Ghpmv.Core.Tests.csproj -c Debug --no-incremental` → **0 warnings, 0 errors**.

`dotnet test … --filter "FullyQualifiedName~CliImportTests"` → **11 passed / 0 failed / 11 total**
(6 pre-existing + 5 new).

Full `Ghpmv.Core.Tests` (`--no-build`): **383 passed / 2 failed / 385 total** (was 377/3/380). The only remaining
failures are the two production-owned `ImportLog` schema-2 tests documented under Phase 1/2
(`ImportLog_without_status_update_sections_loads_with_empty_maps`,
`ImportLog_load_returns_null_when_missing_and_rejects_corrupt_content`). Both were re-confirmed byte-for-byte
identical to the Phase 2 record and **were not touched**.

Harness discovery (`dotnet test Ghpmv.slnx -c Debug --no-build --list-tests`, from the repo root): **489 cases**,
i.e. **+5** — exactly the five new `CliImportTests` methods. No `.slnx` / `Compile Include` edit needed
(SDK-style glob).

### Repairs (2)

1. `Verify_reports_category_statuses_and_writes_consistent_json` — **was RED**, now green. Two defects, both fixed
   in the stub payloads only:
   - the new export round is metadata → items → **statusUpdates** → fields, so `VerifyStatusUpdatesResponse` was
     inserted 3rd in the `GraphQlStubServer` queue, and `Assert.Equal(4, server.RequestBodies.Count)` was **added**
     (the clamped stub otherwise silently repeats the last response);
   - `VerifyFieldsResponse` omitted `pageInfo`, which `QueryPaginatedAsync` requires. This is **pre-existing
     breakage on `main`**, not #46: verified by running this exact test in the `../head-check` worktree at
     `HEAD` (9a8ea73), where it fails identically with `error: The given key was not present in the dictionary.`
     The cause is commit 624e4c5 "Paginate Project field exports". Adding
     `"pageInfo":{"hasNextPage":false,"endCursor":null}` is a payload completion only.

   Every pre-existing assertion (`"Project: Match"`, `"LinkedRepository: PartialMatch"`,
   `"Collaborator: NotVerified"`, `"1 warning(s)"`, the `EndsWith` and the three report-JSON assertions) is
   **byte-for-byte unchanged**. `VerifySnapshot()` still has `StatusUpdates == null`, so the verifier adds no
   `StatusUpdate` category and the category table is unchanged.

2. `Conflict_update_emits_stable_result_and_applies_project_mutation` — the plan predicted a `RequestBodies.Count`
   recount; **none was needed**. `MinimalSnapshot()` has no status updates, so the CLI skips both the template
   session and `StatusUpdateImporter` and the count is still exactly **3** (verified, not assumed). The test was
   extended with the plan's `status-updates: created=0 resumed=0 already-complete=0` assertion plus a new negative
   assertion that no `ProjectV2AsTemplate` mutation is issued. Pre-existing strings unchanged.

`git diff -U0` on the file removes exactly **two** lines — the reformatted verify stub construction and the
`VerifyFieldsResponse` payload. No test method, and no assertion, was deleted or weakened.

### New tests (5)

- `Import_prints_the_status_update_summary_line_after_items` — non-template target, one status update. Asserts the
  **whole stdout block as an ordered array**: `<url>` → `result=updated project=42` →
  `items: …` → `status-updates: created=1 resumed=0 already-complete=0` → `views: …`, so any reordering,
  insertion or text drift on an existing line fails. Secondary observables: exactly 6 requests, the
  `createProjectV2StatusUpdate` body carries the attribution note
  (`Originally created by @octocat on 2024-01-05T09:00:00Z`), the original body, `"status":"ON_TRACK"`,
  `"startDate"` and `"targetDate"`, and no `ProjectV2AsTemplate` mutation is sent for a non-template target.
- `Import_skip_path_does_not_print_the_status_update_line` — `--on-conflict skip` with a snapshot that **does**
  have status updates. stdout is exactly `<url>` + `result=skipped project=42`;
  `Assert.DoesNotContain("status-updates:", …)`; a single request that is neither a mutation nor a
  `statusUpdates` query; stderr still reports `skipped without making changes`.
- `Import_marks_and_restores_the_template_only_when_the_snapshot_has_status_updates` — two runs against two
  servers/directories. `StatusUpdates = []` ⇒ **3 requests, zero** `ProjectV2AsTemplate` mutations and zero
  `createProjectV2StatusUpdate` (the template state is not even probed), while the additive
  `status-updates: created=0 …` line is still printed. Non-empty list on a template target ⇒ **exactly one**
  `unmarkProjectV2AsTemplate` and **exactly one** `markProjectV2AsTemplate`, 8 requests total.
- `Import_restores_the_template_after_downstream_importers` — ordering assertion over `server.RequestBodies`:
  `updateProjectV2` < `unmarkProjectV2AsTemplate` < `createProjectV2StatusUpdate` < `markProjectV2AsTemplate`,
  and `markProjectV2AsTemplate` is the **last** request (`markIndex == RequestBodies.Count - 1`), i.e. restore is
  the final orchestration stage. Also pins the two documented progress messages on stderr. A dedicated
  `IsMarkTemplateMutation` helper excludes bodies containing `unmarkProjectV2AsTemplate`, because
  `markProjectV2AsTemplate` is a substring of it.
- `Import_reports_a_template_restore_failure_on_stderr_and_fails_the_run` — the stub fails the
  `markProjectV2AsTemplate` mutation. Asserts the status update was written first, exit code 1, the GraphQL error
  on stderr, the dedicated `error: failed to restore the target project's template state:` diagnostic from the
  `finally` path, that **both** restore attempts (in-`try` and `finally`) are mark mutations, and that the last
  request is the mark retry.

### One plan-specified assertion contradicts production behavior (test asserts real behavior instead)

The plan's test 7 was named `Import_reports_a_template_restore_failure_on_stderr_without_changing_stdout` and
expected stdout to still carry the `result=` / `items:` / `status-updates:` / `views:` lines. That is **not**
reachable: the primary `templateWriteSession.RestoreAsync` runs at `src/Ghpmv.Cli/Program.cs` L531-534, i.e.
**inside the `try` and before** the stdout summary block (L536-544). A restore failure therefore propagates to the
L554 `catch`, which prints `error: …` and returns 1 — stdout is **empty**. The `finally` (L559-571) then retries
and emits the dedicated restore diagnostic. The test keeps the plan's intent (stderr diagnostic, contract never
*partially* emitted) but was renamed to `…_and_fails_the_run` and asserts `Assert.Equal(string.Empty, result.Output)`
so the name matches the verified behavior. This test is additional to the four explicitly requested for this run.

> If the intended contract really is "stdout unchanged on restore failure", that is a **production** change
> (wrap the L531-534 restore in its own try/catch so the summary block still runs); flagging it, not fixing it.

### Files modified

- `tests/Ghpmv.Core.Tests/CliImportTests.cs` (only file changed by Phase 3)

No file under `src/**` or `docs/**` was modified by Phase 3 (`git status` shows only the parallel production
agent's edits there). Every `await` in the new tests uses `TestContext.Current.CancellationToken`; no mocking
library was added; the existing `GraphQlStubServer` / `RunCliAsync` / `RunVerifyCliAsync` harness was reused
unchanged.

> The Phase 2 `git worktree` at `../head-check` was reused for the `main`-baseline check above and is still in
> place. Clean up with `git worktree remove ../head-check` when convenient.


---

# Test Implementation Status — Phase 4 (issue #46)

Run: 2026-08-16 (JST). Scope: `.testagent/plan.md` Phase 4 "Live GitHub API integration tests".

## Phase 4 — COMPLETE (5 new live tests, all skipping cleanly without credentials; 0 repairs)

| File | Action | New tests |
|---|---|---:|
| `tests/Ghpmv.Integration.Tests/ProjectExporterTests.cs` | extend | 1 |
| `tests/Ghpmv.Integration.Tests/ProjectImporterTests.cs` | extend | 1 |
| `tests/Ghpmv.Integration.Tests/VerifyTests.cs` | extend | 1 |
| `tests/Ghpmv.Integration.Tests/ProjectTemplateWriteSessionTests.cs` | **create** | 2 |

`dotnet build tests/Ghpmv.Integration.Tests/Ghpmv.Integration.Tests.csproj -c Debug --no-incremental`
-> **0 warnings, 0 errors**.

`dotnet test tests/Ghpmv.Integration.Tests/Ghpmv.Integration.Tests.csproj -c Debug --no-build`
-> **0 failed / 2 passed / 31 skipped / 33 total** (was 0 / 2 / 26 / 28). `GHPMV_TEST_TOKEN` is absent in this
environment, so every real-API test — the 26 pre-existing ones and all 5 new ones — reports **SKIP**, not FAIL.
The 2 passing tests are the token-free `IntegrationFixtureSnapshotTests`. **No pre-existing test regressed.**

Explicitly confirmed SKIPPED (xUnit `[SKIP]` markers):

- `ProjectExporterTests.Export_captures_fixture_status_updates_in_reverse_chronological_order`
- `ProjectImporterTests.Status_updates_round_trip_into_a_temporary_project_with_every_status_and_date_shape`
- `VerifyTests.Status_update_category_matches_after_import_and_mismatches_after_drift`
- `ProjectTemplateWriteSessionTests.Template_state_is_restored_after_status_update_writes`
- `ProjectTemplateWriteSessionTests.Prepare_on_a_non_template_project_requires_no_restoration`

Harness discovery: `dotnet test tests/Ghpmv.Integration.Tests/... -c Debug --list-tests` **28 -> 33 (+5)**, matching
exactly. `dotnet test Ghpmv.slnx -c Debug --list-tests` from the repo root lists **494** cases and includes all 33
`Ghpmv.Integration.Tests.*` cases, so the new SDK-style file needs no `.slnx` / `Compile Include` registration.

`git diff -U0 -- tests/Ghpmv.Integration.Tests` reports **zero removed lines**; the three extended files are strictly
append-only. No file under `src/**` or `docs/**` was touched by this phase (the `src`/`docs` entries in
`git status` are the parallel production agent's work, unchanged by Phase 4).

## What each test pins down

1. **`Export_captures_fixture_status_updates_in_reverse_chronological_order`** (read-only, shared source fixture,
   `SelectCanonicalItems` path). Asserts exactly 5 updates; the newest-first status sequence
   `COMPLETE, OFF_TRACK, AT_RISK, ON_TRACK, INACTIVE`; the documented `(StartDate, TargetDate)` pairs index for
   index — including the `(null, "2026-04-15")`, `("2026-01-01", null)` and `(null, null)` shapes; the five
   verbatim bodies (the fixture writes its own history with `AddAttributionNote = false`, so multi-line and
   `**Markdown**` content survives unchanged); descending `createdAt`; and non-empty `Creator` / `UpdatedAt`.
2. **`Status_updates_round_trip_into_a_temporary_project_with_every_status_and_date_shape`** (throwaway project).
   `TemporaryProjectFixture.CreateAsync` -> `StatusUpdateImporter.ImportAsync` -> `ProjectExporter.ExportAsync`
   of the target. Asserts `Created == 5 / Resumed == 0 / AlreadyComplete == 0`; `ImportLog.StatusUpdates.Count == 5`
   with 5 distinct node ids, empty `PendingStatusUpdates`, and `log.ProjectId == projectId`; then per source update
   `Status`, `StartDate`, `TargetDate` and the body equal to `StatusUpdateImporter.BuildImportedBody(source)`
   (plus `@{creator}` mention, source `CreatedAt`, and the original body surviving below the note). Order is
   compared index for index: oldest-first creation makes the server's reverse-chronological history line up with
   the source sequence. Finally it **re-runs** the same import against the same log directory and asserts
   `Created == 0 && Resumed == 0 && AlreadyComplete == 5`, a re-export still returning exactly 5 bodies identical
   to the first re-export (no content dedupe, no duplicates), and an unchanged `StatusUpdates` map.
3. **`Status_update_category_matches_after_import_and_mismatches_after_drift`** (throwaway project). Guards with
   `Assert.Equal(5, source.StatusUpdates!.Count)`, imports, polls `ProjectVerifier.VerifyAsync` via the existing
   `VerifyUntilAsync` helper until the `StatusUpdate` category is `Match` (and asserts zero `StatusUpdate`
   differences), then drifts the target by creating one **extra** status update directly — deletion of an
   individual update is unavailable — and asserts the category flips to `Mismatch` carrying the
   `status update count mismatch (source 5, target 6)` error.
4. **`Template_state_is_restored_after_status_update_writes`** (throwaway project). `markProjectV2AsTemplate` ->
   assert `template == true` -> `PrepareAsync` (asserts `RestorationRequired == true`, that the live `template`
   flag is now **false**, and the documented unmark progress line) -> `StatusUpdateImporter.ImportAsync` of a
   2-update snapshot succeeds (`Created == 2`), proving the unmark was necessary and effective -> `RestoreAsync`
   -> re-query `node.template` directly and assert **true** again plus the documented restore progress line.
   A second `RestoreAsync` asserts idempotency: the flag stays true and the restore message is emitted once.
5. **`Prepare_on_a_non_template_project_requires_no_restoration`** — same fixture without marking; asserts
   `RestorationRequired == false`, **no** progress messages at all (i.e. zero template mutations), that the import
   still succeeds, and that the project's `template` flag stays `false` after `RestoreAsync`.

## Conventions followed

- Per-class verbatim `Token` property with `Assert.SkipWhen(...)` — no custom skip attribute, no shared base class.
  `ProjectTemplateWriteSessionTests` carries its own copy; the three extended files reuse theirs.
- Unique per-run titles (`"ghpmv-import-test-" + Guid.NewGuid().ToString("N")`, and
  `"ghpmv-status-update-test-" + ...` in `VerifyTests`); per-test `try/finally` cleanup, no `IAsyncLifetime`,
  no class fixtures, no `[Collection]` (`TestAssembly.cs` already disables parallelization).
- Every **write** test targets a `TemporaryProjectFixture.CreateAsync` throwaway project — never the shared
  fixture — and deletes it plus its temp log directory in `finally`. Only the read-only exporter test touches the
  shared source fixture.
- `IntegrationTestSettings` supplies org / fixture / project-number config
  (`SourceOrg`, `TargetOrg`, `FixtureProjectNumber`, `CreateOperationLogDirectory()`).
- **CancellationToken checklist: satisfied.** Every new `await` passes `TestContext.Current.CancellationToken`
  explicitly — `ImportLog.LoadAsync`, `ProjectExporter.ExportAsync`, `StatusUpdateImporter.ImportAsync`,
  `ProjectTemplateWriteSession.PrepareAsync`/`RestoreAsync`, `ProjectVerifier.VerifyAsync`,
  `TemporaryProjectFixture.CreateAsync`, and every raw `client.QueryAsync`/`client.MutationAsync` helper. The only
  `CancellationToken.None` uses are the pre-existing `DeleteProjectAsync` cleanup helpers (deliberate: cleanup must
  still run after cancellation), matching the existing file convention.

## Deviations from the plan (deliberate, recorded per instruction)

1. **Plan tests 2-5 merged into one round-trip test.** The plan listed four separate `ProjectImporterTests`
   (round trip / creation order / attribution body / rerun). They are implemented as the single test named after
   plan test 2, per the task instruction ("a full round trip ... then re-run"). Every assertion from plan tests 3,
   4 and 5 is present in that test; merging keeps one throwaway project and one API round trip instead of four.
2. **`createdAt` ordering is asserted as descending, not *strictly* descending.** The plan wrote "strictly
   descending". GitHub assigns `createdAt` at second resolution and the fixture creates all five updates in one
   quick oldest-first burst, so two adjacent updates can legitimately share a timestamp; a strict comparison would
   be a live flake. Ordering is still pinned exactly by the newest-first status/date/body sequence assertions.
3. **Fixture `CreatedAt` values are not asserted on the live export.** `createdAt` is server-assigned, so the
   documented `2026-01-0x` timestamps cannot survive a live create; they are asserted where they *are*
   contractual — inside the imported body's attribution note (test 2).
4. **Open item resolved: no `NormalizeKnownSnapshot` change.** Re-exported status-update bodies are never reused as
   a *source* snapshot — test 2 compares them against `BuildImportedBody(source)` directly and the verifier applies
   the same transformation internally — so `NormalizeKnownSnapshot` needs no status-update case and
   `Normalize_known_snapshot_leaves_status_update_bodies_unchanged` was **not** added, per the plan's
   "otherwise record the decision in status.md and skip it".
5. Bodies are compared after normalizing `\r\n` to `\n` (the same normalization `ProjectVerifier.NormalizeBody`
   applies), because GitHub may return CRLF for multi-line bodies.

## Not re-run in this phase

The 2 documented `ImportLog.LoadAsync` failures in `Ghpmv.Core.Tests` (schema-2 backward compatibility, production
owned, 383 passed / 2 failed / 385) are out of scope and were not touched. Phase 4 changes are confined to
`tests/Ghpmv.Integration.Tests` and cannot affect them.

---

# Final Validation — Steps 6–9 (orchestrator, after Phases 0–4)

## Step 6 — Full solution build

`dotnet build Ghpmv.slnx -c Debug --no-incremental` → **0 warnings, 0 errors** (all 5 projects: `Ghpmv.Core`,
`Ghpmv.Cli`, `Ghpmv.Core.Tests`, `Ghpmv.Integration.Tests`, `Ghpmv.Browser.Tests`).

## Step 7 — Full solution test run (fresh build, `dotnet test Ghpmv.slnx -c Debug`)

| Project | Passed | Failed | Skipped | Total |
|---|---:|---:|---:|---:|
| `Ghpmv.Core.Tests` | 383 | **2** | 0 | 385 |
| `Ghpmv.Integration.Tests` | 2 | 0 | 31 | 33 |
| `Ghpmv.Browser.Tests` | 73 | 0 | 3 | 76 |
| **Total** | **458** | **2** | 34 | 494 |

The only 2 failures are the documented, production-owned `ImportLog.LoadAsync` schema-2 backward-compatibility
bug (see Phase 1 section above) — unrelated to any test code in this run, left red intentionally so the signal
is not masked. Harness discovery (`dotnet test Ghpmv.slnx --no-build --list-tests`) shows 494 tests, matching the
solution-level run exactly (no test invisible to the harness).

## Step 7 — Pre-completion gate (mandatory: ≥5 tests added, many behaviors specified)

### `test-gap-analysis` (pseudo-mutation), empirically verified — 8/8 mutations killed

Applied real, temporary edits to the new production code, re-ran the narrowest covering tests, and reverted every
edit (`git diff --stat -- src/` confirmed byte-identical to the pre-probe diff after each revert; final full-suite
run reproduced the exact same 458/2/34/494 tally):

| # | File / mutation | Covering test(s) | Verdict |
|---|---|---|---|
| 1 | `ProjectVerifier.CompareStatusUpdates`: `!=` → `==` on count check | 10 of 11 `ProjectVerifierTests.Status_update*`/`Extra_target_status_updates_are_errors` | **Killed** |
| 2 | `StatusUpdateImporter.ImportAsync`: `OrderBy` → `OrderByDescending` (reversed import order) | 5 tests incl. `Import_creates_status_updates_oldest_first` | **Killed** |
| 3 | `ProjectTemplateWriteSession.PrepareAsync`: unmark-skip guard defeated at runtime | 5 of 9 `ProjectTemplateWriteSessionTests` | **Killed** |
| 4 | `ImportLog.LoadAsync`: duplicate-target-id check inverted | 10 of 11 `ItemImporterLogicTests.ImportLog_*` | **Killed** |
| 5 | `StatusUpdateImporter.ReconcilePendingAsync`: `candidates.Length == 1` → `>= 1` | `Reconciliation_rejects_multiple_new_candidates` | **Killed** |
| 6 | `StatusUpdateImporter.ImportAsync`: already-complete short-circuit defeated | `Second_run_with_the_log_reports_already_complete_without_sending_mutations`, `Reconciliation_baseline_excludes_ids_already_mapped_in_the_log` | **Killed** |
| 7 | `StatusUpdateImporter.BuildImportedBody`: creator branch defeated (always "no creator" note) | 3 of 5 `BuildImportedBody_*` | **Killed** |
| 8 | `ProjectVerifier.Compare`: StatusUpdate category always added (schema-v1 guard defeated) | `Status_updates_are_not_compared_when_the_source_predates_capture` | **Killed** |

**8/8 verified — no survivors found** in the sampled high-risk mutation points (ordering, resume/dedupe,
reconciliation boundary, template unmark/remark, verifier count/category guards, attribution note). Two items
of **unreachable production code** were identified by the Phase-2 implementer and independently confirmed by
reading `GitHubGraphQLClient.ExecuteOperationAsync`: (a) `StatusUpdateImporter.CreateAsync`'s
`?? throw new GitHubGraphQLException("createProjectV2StatusUpdate returned an empty status update id.")` is
dead — the client's `requiredResultPath` check throws `AmbiguousMutationResultException` first for the default
`MutationRetryPolicy.Create`; (b) the in-loop `pending.ProjectId` mismatch check in `ImportAsync` is dead —
`ValidateLogAgainstSnapshot` rejects any cross-project pending entry before the loop starts. Both are pre-existing
production code (not introduced by tests) and are reported as a recommendation, not a test gap, since no test can
reach unreachable code.

### `assertion-quality`, sampled across new files

Reviewed `ProjectVerifierTests` (11 new `Status_update*` tests), `StatusUpdateImporterLogicTests` (20 tests),
`StatusUpdateImporterResumeTests` (12 tests), `ProjectTemplateWriteSessionTests` (9 tests), and the new
`CliImportTests`/integration tests. Assertion diversity is strong: concrete **Equality** (exact expected strings,
counts, ordered ID sequences), **Negative** (`Assert.DoesNotContain`, `Assert.Empty`, `Assert.False`),
**Exception** (`Assert.ThrowsAsync<T>` + message-content checks), **Collection** (`Assert.Single`, ordered sequence
equality), **String** (`StartsWith`/`EndsWith`/`Contains`), and **State/side-effect** (post-call `ImportLog`
re-load and field assertions, mutation-count assertions on the stub handler) categories are all represented. No
assertion-free or trivial-only (null-check-only) new test was found. No self-referential/tautological assertions
were found — round-trip tests (`SnapshotTests.Roundtrip_preserves_status_updates`) assert concrete field values
per property, not just object identity.

### Prompt-scenario coverage check

Every bullet in the issue's acceptance-criteria checklist maps to at least one concretely named test — see the
"Acceptance-criteria → test map" table in `.testagent/plan.md` (§ "Acceptance-criteria → test map (for
`status.md`)"), which was followed exactly; each entry was re-verified against the actual test files (not just
the plan) during this final pass, including the literal scenarios: all 5 statuses
(`Import_accepts_every_supported_status` theory + fixture builder), nullable **and** populated dates
(`Import_sends_the_attributed_body_and_optional_dates`, `Status_update_start_and_target_date_differences_are_errors`),
Markdown/multi-line body (`BuildImportedBody_preserves_multi_line_markdown_body`, fixture ON_TRACK/INACTIVE
updates), descending source order vs. oldest-first import (`Export_captures_status_updates_in_reverse_chronological_order`
vs. `Import_creates_status_updates_oldest_first`), the attribution note (`BuildImportedBody_*`,
`Status_update_body_with_imported_attribution_note_matches_the_original`), rerun non-duplication in both
directions (`Second_run_with_the_log_reports_already_complete_without_sending_mutations` /
`Second_run_without_the_log_creates_duplicates_because_there_is_no_content_dedupe`), and explicit exclusion of
target creator/createdAt from verification (`Status_update_creator_and_created_at_are_not_compared`).

## Step 8 — Coverage gap iteration

No unaddressed checklist item was found; no additional tests were added in this pass. The two production-owned
`ImportLog` failures remain the only open item, and are out of scope for a test-only change.

## Cleanup performed

- Removed a throwaway diagnostic worktree at `../head-check` created during Phase 2/3 investigation
  (`git worktree remove ../head-check`) — no tracked files were affected.
- A local scratch project at `scratch-repro/` (untracked, used by the orchestrator to empirically confirm the
  `ImportLog` JSON-deserialization defect) remains on disk; it is **not** part of the deliverable and should be
  deleted by the user (`Remove-Item -Recurse -Force scratch-repro`) since this environment's guardrails blocked
  automated recursive deletion.

## Final status: SUCCESS with one reported, out-of-scope production defect

All 4 implementation phases are complete, the pre-completion gate passed with empirically verified mutation
kills, and every acceptance-criteria bullet has concrete test evidence. The only remaining red tests are a
genuine, pre-existing production bug in `ImportLog.LoadAsync` (schema-2 backward compatibility for logs that
predate status updates) that is explicitly out of scope for this test-only change and is left failing on purpose
so the signal reaches the production agent.
