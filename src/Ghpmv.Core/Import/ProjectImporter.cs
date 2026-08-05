using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Core.Import;

/// <summary>
/// Imports a <see cref="ProjectSnapshot"/> into a target organization (M3):
/// creates the project, applies metadata (README, description, visibility, closed state),
/// creates all custom fields (TEXT/NUMBER/DATE/SINGLE_SELECT/MULTI_SELECT/ITERATION), recreates and
/// links organization Issue Fields (including MULTI_SELECT), overwrites the built-in
/// Status field options, and recreates API-writable View settings.
/// Completed iterations are recreated as past-dated iterations; the API accepts past
/// start dates and reclassifies them into <c>completedIterations</c> on read (verified by PoC).
/// </summary>
public sealed class ProjectImporter
{
    private const string StatusFieldName = "Status";

    /// <summary>Data types that <c>createProjectV2Field</c> supports; everything else is a built-in field.</summary>
    private static readonly HashSet<string> CreatableDataTypes =
        new(["TEXT", "NUMBER", "DATE", "SINGLE_SELECT", "MULTI_SELECT", "ITERATION"], StringComparer.Ordinal);

    private readonly GitHubGraphQLClient _client;
    private readonly List<string> _warnings = [];
    private ProjectImportLog? _operationLog;
    private HashSet<string> _snapshotNormalFieldNames = [];
    private HashSet<string> _snapshotMultiSelectNormalFieldNames = [];
    private HashSet<string> _snapshotIssueFieldNames = [];
    private HashSet<string> _snapshotMultiSelectIssueFieldNames = [];
    private HashSet<string> _targetIssueFieldNames = [];
    private HashSet<string> _targetMultiSelectIssueFieldNames = [];

    public ProjectImporter(GitHubGraphQLClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    /// <summary>Behavior when the target owner already has a project with the snapshot's title.</summary>
    public ConflictAction OnConflict { get; init; } = ConflictAction.Fail;

    /// <summary>Owner type of the target: organization (default) or user.</summary>
    public ProjectOwnerType OwnerType { get; init; } = ProjectOwnerType.Organization;

    /// <summary>Source "org/repo" → target "org/repo" mapping for linked repositories. Unmapped repositories are linked by their source name.</summary>
    public IReadOnlyDictionary<string, string> RepositoryMapping { get; init; } = ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Source login → target login mapping for user collaborators. Unmapped logins are resolved as-is.</summary>
    public IReadOnlyDictionary<string, string> UserMapping { get; init; } = ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Source organization login → target organization login mapping for View filters.</summary>
    public IReadOnlyDictionary<string, string> OrganizationMapping { get; init; } = ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Whether Playwright will apply View settings that the GraphQL API cannot write.</summary>
    public bool BrowserViewEnrichmentPlanned { get; init; }

    /// <summary>Warnings accumulated by the last import (unresolvable collaborators, unlinkable repositories).</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Invoked with a human-readable progress message at each import stage.</summary>
    public Action<string>? OnProgress { get; set; }

    /// <summary>Invoked after conflict resolution and immediately before the first mutation.</summary>
    public Func<CancellationToken, Task>? BeforeWriteAsync { get; set; }

    /// <summary>Directory for durable project and field creation operation state.</summary>
    public required string OperationLogDirectory { get; init; }

    /// <summary>Target project required by pending item operations loaded before project-stage writes.</summary>
    public string? PendingItemProjectId { get; init; }

    internal async Task<bool> ReserveProjectAsync(
        string ownerLogin,
        string title,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerLogin);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        await LoadOperationLogAsync(cancellationToken).ConfigureAwait(false);
        await ReconcilePendingProjectDeletionAsync(cancellationToken).ConfigureAwait(false);

        OnProgress?.Invoke($"Reserving project title '{title}' in {OwnerDescription} '{ownerLogin}'...");
        var matches = await FindProjectsByTitleAsync(ownerLogin, title, cancellationToken).ConfigureAwait(false);
        if (_operationLog?.PendingProject is { } pendingProject)
        {
            if (!string.Equals(pendingProject.OwnerLogin, ownerLogin, StringComparison.Ordinal)
                || !string.Equals(pendingProject.Title, title, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Pending project operation '{pendingProject.OperationId}' does not match the current import target.");
            }

            if (_operationLog.CreatedProjectId is not { } createdProjectId)
            {
                throw new InvalidOperationException(
                    $"Pending project operation '{pendingProject.OperationId}' has no recorded Project ID and cannot be reconciled safely for strict fixture setup.");
            }

            var reconciled = matches.FirstOrDefault(
                project => string.Equals(project.Id, createdProjectId, StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    $"The project '{createdProjectId}' created by this operation was not found.");
            if (matches.Any(project => !string.Equals(project.Id, reconciled.Id, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"Project '{title}' has an unrelated same-title Project in {OwnerDescription} '{ownerLogin}'.");
            }

            _operationLog.PendingProject = null;
            await SaveOperationLogAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        var existing = SelectExistingProject(matches);
        if (existing is not null)
        {
            if (string.Equals(_operationLog?.CreatedProjectId, existing.Id, StringComparison.Ordinal))
            {
                if (matches.Any(project => !string.Equals(project.Id, existing.Id, StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException(
                        $"Project '{title}' has an unrelated same-title Project in {OwnerDescription} '{ownerLogin}'.");
                }

                return false;
            }

            throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture,
                    $"A project titled '{title}' already exists in {OwnerDescription} '{ownerLogin}' (#{existing.Number})."));
        }

        await CreateAndRecordProjectAsync(ownerLogin, title, matches, cancellationToken).ConfigureAwait(false);
        if (_operationLog is not null)
        {
            _operationLog.PendingProject = null;
            await SaveOperationLogAsync(cancellationToken).ConfigureAwait(false);
        }

        var reservedProjectId = _operationLog?.CreatedProjectId
            ?? throw new InvalidOperationException("The reserved Project ID was not recorded.");
        var confirmedMatches = await FindProjectsByTitleAsync(ownerLogin, title, cancellationToken).ConfigureAwait(false);
        if (confirmedMatches.Any(project => !string.Equals(project.Id, reservedProjectId, StringComparison.Ordinal)))
        {
            await ReleaseReservedProjectAsync(CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Project '{title}' has an unrelated same-title Project in {OwnerDescription} '{ownerLogin}'. The Project created by this operation was removed.");
        }

        return true;
    }

    internal async Task ReleaseReservedProjectAsync(CancellationToken cancellationToken = default)
    {
        await LoadOperationLogAsync(cancellationToken).ConfigureAwait(false);
        if (_operationLog?.PendingProjectDeletionId is not null)
        {
            await ReconcilePendingProjectDeletionAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var projectId = _operationLog?.CreatedProjectId
            ?? throw new InvalidOperationException("This operation has no reserved Project to release.");
        if (_operationLog.PendingProject is not null
            || _operationLog.PendingFields.Count > 0
            || _operationLog.PendingIssueFields.Count > 0
            || _operationLog.PendingIssueFieldLinks.Count > 0)
        {
            throw new InvalidOperationException(
                $"Project '{projectId}' has pending import operations and cannot be released automatically.");
        }

        _operationLog.PendingProjectDeletionId = projectId;
        await SaveOperationLogAsync(cancellationToken).ConfigureAwait(false);
        await DeleteProjectAndReconcileAsync(projectId, cancellationToken).ConfigureAwait(false);
        _operationLog.CreatedProjectId = null;
        _operationLog.ImportCompleted = null;
        _operationLog.PendingProjectDeletionId = null;
        await SaveOperationLogAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteProjectAndReconcileAsync(string projectId, CancellationToken cancellationToken)
    {
        try
        {
            await _client.MutationAsync(
                "deleteProjectV2",
                """
                mutation($projectId: ID!) {
                  deleteProjectV2(input: { projectId: $projectId }) {
                    projectV2 { id }
                  }
                }
                """,
                new { projectId },
                MutationRetryPolicy.Create,
                target: projectId,
                requiredResultPath: "projectV2.id",
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (AmbiguousMutationResultException exception)
        {
            bool projectStillExists;
            try
            {
                projectStillExists = await ProjectExistsAsync(projectId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception reconciliationException)
            {
                throw new InvalidOperationException(
                    $"Could not reconcile deletion of reserved Project '{projectId}'.",
                    new AggregateException(exception, reconciliationException));
            }

            if (projectStillExists)
            {
                throw;
            }
        }
        catch (GitHubGraphQLException exception) when (
            string.Equals(exception.ErrorType, "NOT_FOUND", StringComparison.Ordinal))
        {
        }
    }

    private async Task<bool> ProjectExistsAsync(string projectId, CancellationToken cancellationToken)
    {
        var data = await _client.QueryAsync(
            """
            query($projectId: ID!) {
              node(id: $projectId) {
                ... on ProjectV2 { id }
              }
            }
            """,
            new { projectId },
            cancellationToken).ConfigureAwait(false);
        return data.TryGetProperty("node", out var node)
            && node.ValueKind == JsonValueKind.Object
            && node.TryGetProperty("id", out var id)
            && string.Equals(id.GetString(), projectId, StringComparison.Ordinal);
    }

    private async Task ReconcilePendingProjectDeletionAsync(CancellationToken cancellationToken)
    {
        if (_operationLog?.PendingProjectDeletionId is not { } projectId)
        {
            return;
        }

        if (!string.Equals(_operationLog.CreatedProjectId, projectId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Pending Project deletion '{projectId}' does not match this operation's created Project.");
        }

        if (await ProjectExistsAsync(projectId, cancellationToken).ConfigureAwait(false))
        {
            await DeleteProjectAndReconcileAsync(projectId, cancellationToken).ConfigureAwait(false);
        }

        _operationLog.CreatedProjectId = null;
        _operationLog.ImportCompleted = null;
        _operationLog.PendingProjectDeletionId = null;
        await SaveOperationLogAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Imports the snapshot into <paramref name="ownerLogin"/> and returns the target project identity and field mappings.</summary>
    public async Task<ImportResult> ImportAsync(ProjectSnapshot snapshot, string ownerLogin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerLogin);
        ValidateProjectFieldContracts(snapshot);
        InitializeSnapshotFieldNames(snapshot);
        await LoadOperationLogAsync(cancellationToken).ConfigureAwait(false);
        await ReconcilePendingProjectDeletionAsync(cancellationToken).ConfigureAwait(false);

        var title = snapshot.Project.Title;
        OnProgress?.Invoke($"Checking {OwnerDescription} '{ownerLogin}' for an existing project titled '{title}'...");
        var matches = await FindProjectsByTitleAsync(ownerLogin, title, cancellationToken).ConfigureAwait(false);
        ProjectRef? existing;
        if (_operationLog?.PendingProject is { } pendingProject)
        {
            if (!string.Equals(pendingProject.OwnerLogin, ownerLogin, StringComparison.Ordinal)
                || !string.Equals(pendingProject.Title, title, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Pending project operation '{pendingProject.OperationId}' does not match the current import target.");
            }

            existing = await ReconcilePendingProjectAsync(pendingProject, matches, cancellationToken).ConfigureAwait(false);
            _operationLog.CreatedProjectId = existing.Id;
            _operationLog.ImportCompleted = false;
            await SaveOperationLogAsync(cancellationToken).ConfigureAwait(false);
            ValidatePendingItemProject(existing.Id);
            ValidatePendingFieldOperations(snapshot, existing.Id);
            ValidatePendingViewOperations(snapshot, existing.Id);
            await InvokeBeforeWriteAsync(cancellationToken).ConfigureAwait(false);
            var resumedResult = await ApplySnapshotAsync(
                snapshot,
                ownerLogin,
                existing,
                ProjectImportOutcome.Created,
                cancellationToken).ConfigureAwait(false);
            _operationLog.PendingProject = null;
            await SaveOperationLogAsync(cancellationToken).ConfigureAwait(false);
            return resumedResult;
        }

        existing = SelectExistingProject(matches);

        if (existing is not null)
        {
            ValidatePendingItemProject(existing.Id);
            switch (OnConflict)
            {
                case ConflictAction.Fail:
                    throw new InvalidOperationException(
                        string.Create(CultureInfo.InvariantCulture,
                            $"A project titled '{title}' already exists in {OwnerDescription} '{ownerLogin}' (#{existing.Number}). Use --on-conflict skip or update to proceed."));

                case ConflictAction.Skip:
                    OnProgress?.Invoke(string.Create(CultureInfo.InvariantCulture,
                        $"Project '{title}' already exists (#{existing.Number}); skipping (on-conflict=skip)."));
                    return BuildSkippedResult(existing);

                case ConflictAction.Update:
                    ValidatePendingFieldOperations(snapshot, existing.Id);
                    ValidatePendingViewOperations(snapshot, existing.Id);
                    OnProgress?.Invoke(string.Create(CultureInfo.InvariantCulture,
                        $"Project '{title}' already exists (#{existing.Number}); applying snapshot to it (on-conflict=update)."));
                    await InvokeBeforeWriteAsync(cancellationToken).ConfigureAwait(false);
                    return await ApplySnapshotAsync(snapshot, ownerLogin, existing, ProjectImportOutcome.Updated, cancellationToken).ConfigureAwait(false);
            }
        }

        ValidatePendingItemProject(projectId: null);
        ValidatePendingFieldOperations(snapshot, projectId: null);
        ValidatePendingViewOperations(snapshot, projectId: null);
        var project = await CreateAndRecordProjectAsync(
            ownerLogin,
            title,
            matches,
            cancellationToken,
            invokeBeforeWrite: true).ConfigureAwait(false);
        var result = await ApplySnapshotAsync(snapshot, ownerLogin, project, ProjectImportOutcome.Created, cancellationToken).ConfigureAwait(false);
        if (_operationLog is not null)
        {
            _operationLog.PendingProject = null;
            await SaveOperationLogAsync(cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Imports the snapshot into an existing project identified by number: skips the
    /// title lookup/creation and merges fields like the on-conflict=update path
    /// (the existing project keeps its title).
    /// </summary>
    public async Task<ImportResult> ImportIntoAsync(ProjectSnapshot snapshot, string ownerLogin, int projectNumber, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerLogin);
        ValidateProjectFieldContracts(snapshot);
        InitializeSnapshotFieldNames(snapshot);
        await LoadOperationLogAsync(cancellationToken).ConfigureAwait(false);
        await ReconcilePendingProjectDeletionAsync(cancellationToken).ConfigureAwait(false);

        OnProgress?.Invoke(string.Create(CultureInfo.InvariantCulture,
            $"Looking up project #{projectNumber} in {OwnerDescription} '{ownerLogin}'..."));
        var project = await FindProjectByNumberAsync(ownerLogin, projectNumber, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"Project #{projectNumber} was not found in {OwnerDescription} '{ownerLogin}'."));

        if (_operationLog?.PendingProject is { } pendingProject)
        {
            throw new InvalidOperationException(
                $"Pending project operation '{pendingProject.OperationId}' must be resumed by project title before importing into project #{projectNumber}.");
        }

        if (_operationLog is { CreatedProjectId: { } createdProjectId, ImportCompleted: false }
            && !string.Equals(createdProjectId, project.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{ProjectImportLog.FileName} contains an incomplete import for project '{createdProjectId}', but project #{projectNumber} has id '{project.Id}'. Resume the recorded project or use a separate import directory.");
        }

        ValidatePendingItemProject(project.Id);
        ValidatePendingFieldOperations(snapshot, project.Id);
        ValidatePendingViewOperations(snapshot, project.Id);
        OnProgress?.Invoke(string.Create(CultureInfo.InvariantCulture,
            $"Applying snapshot to existing project #{project.Number}..."));
        await InvokeBeforeWriteAsync(cancellationToken).ConfigureAwait(false);
        return await ApplySnapshotAsync(snapshot, ownerLogin, project, ProjectImportOutcome.Updated, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Imports only Views into an existing project. Used by fixture setup so View creation
    /// still goes through GraphQL while workflows and unsupported View settings use Playwright.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, int>> ImportViewsIntoAsync(
        ProjectSnapshot snapshot,
        string ownerLogin,
        int projectNumber,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerLogin);
        InitializeSnapshotFieldNames(snapshot);
        await LoadOperationLogAsync(cancellationToken).ConfigureAwait(false);

        var project = await FindProjectByNumberAsync(ownerLogin, projectNumber, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"Project #{projectNumber} was not found in {OwnerDescription} '{ownerLogin}'."));
        ValidatePendingViewOperations(snapshot, project.Id);
        await InvokeBeforeWriteAsync(cancellationToken).ConfigureAwait(false);

        _warnings.Clear();
        var maps = new FieldMaps();
        await FetchFieldListAsync(project.Id, maps, cancellationToken).ConfigureAwait(false);
        var viewImporter = CreateViewImporter();
        var viewNumbers = await viewImporter.ImportAsync(
            snapshot.Views,
            project.Id,
            maps.FieldIds,
            ProjectImportOutcome.Updated,
            cancellationToken).ConfigureAwait(false);
        _warnings.AddRange(viewImporter.Warnings);
        return viewNumbers;
    }

    private Task InvokeBeforeWriteAsync(CancellationToken cancellationToken)
        => BeforeWriteAsync?.Invoke(cancellationToken) ?? Task.CompletedTask;

    private static void ValidateProjectFieldContracts(ProjectSnapshot snapshot)
    {
        var invalidMultiSelect = snapshot.Fields.FirstOrDefault(field =>
            field.IssueField is null
            && string.Equals(field.DataType, "MULTI_SELECT", StringComparison.Ordinal)
            && field.Options is not { Count: > 0 });
        if (invalidMultiSelect is not null)
        {
            throw new InvalidDataException(
                $"Snapshot Project multi-select field '{invalidMultiSelect.Name}' must define at least one option. " +
                "GitHub requires at least one option when creating the field and ignores empty option updates.");
        }
    }

    private ProjectRef? SelectExistingProject(IReadOnlyList<ProjectRef> matches)
    {
        if (_operationLog?.CreatedProjectId is not { } createdProjectId)
        {
            return matches.Count > 0 ? matches[0] : null;
        }

        return matches.FirstOrDefault(project => string.Equals(project.Id, createdProjectId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"The project '{createdProjectId}' created by this operation was not found.");
    }

    private async Task<ProjectRef> CreateAndRecordProjectAsync(
        string ownerLogin,
        string title,
        IReadOnlyList<ProjectRef> matches,
        CancellationToken cancellationToken,
        bool invokeBeforeWrite = false)
    {
        var ownerId = await GetOwnerIdAsync(ownerLogin, cancellationToken).ConfigureAwait(false);
        if (invokeBeforeWrite)
        {
            await InvokeBeforeWriteAsync(cancellationToken).ConfigureAwait(false);
        }

        OnProgress?.Invoke($"Creating project '{title}' in '{ownerLogin}'...");
        var operationId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        if (_operationLog is not null)
        {
            _operationLog.PendingProject = new PendingProjectOperation
            {
                OperationId = operationId,
                OwnerLogin = ownerLogin,
                Title = title,
                ExistingProjectIds = [.. matches.Select(project => project.Id)],
            };
            await SaveOperationLogAsync(cancellationToken).ConfigureAwait(false);
        }

        JsonElement createData;
        try
        {
            createData = await CreateProjectAsync(ownerId, title, operationId, cancellationToken).ConfigureAwait(false);
        }
        catch (AmbiguousMutationResultException)
        {
            throw;
        }
        catch
        {
            if (_operationLog is not null)
            {
                _operationLog.PendingProject = null;
                await SaveOperationLogAsync(CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }

        var project = ParseProjectRef(createData.GetProperty("createProjectV2").GetProperty("projectV2"));
        if (_operationLog is not null)
        {
            _operationLog.CreatedProjectId = project.Id;
            _operationLog.ImportCompleted = false;
            await SaveOperationLogAsync(CancellationToken.None).ConfigureAwait(false);
        }
        return project;
        return project;
    }

    private void InitializeSnapshotFieldNames(ProjectSnapshot snapshot)
    {
        _snapshotNormalFieldNames = snapshot.Fields
            .Where(field => field.IssueField is null)
            .Select(field => field.Name)
            .ToHashSet(StringComparer.Ordinal);
        _snapshotMultiSelectNormalFieldNames = snapshot.Fields
            .Where(field => field.IssueField is null
                && string.Equals(field.DataType, "MULTI_SELECT", StringComparison.Ordinal))
            .Select(field => field.Name)
            .ToHashSet(StringComparer.Ordinal);
        _snapshotIssueFieldNames = snapshot.Fields
            .Where(field => field.IssueField is not null)
            .Select(field => field.Name)
            .ToHashSet(StringComparer.Ordinal);
        _snapshotMultiSelectIssueFieldNames = snapshot.Fields
            .Where(field => field.IssueField is not null
                && string.Equals(field.DataType, "MULTI_SELECT", StringComparison.Ordinal))
            .Select(field => field.Name)
            .ToHashSet(StringComparer.Ordinal);
    }

    private void ValidatePendingItemProject(string? projectId)
    {
        if (PendingItemProjectId is not null
            && !string.Equals(PendingItemProjectId, projectId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{ImportLog.FileName} contains pending operations for project '{PendingItemProjectId}'. Resume that project or reconcile it manually before writing project '{projectId ?? "(new project)"}'.");
        }
    }

    private void ValidatePendingFieldOperations(ProjectSnapshot snapshot, string? projectId)
    {
        if (_operationLog is null
            || (_operationLog.PendingFields.Count == 0
                && _operationLog.PendingIssueFields.Count == 0
                && _operationLog.PendingIssueFieldLinks.Count == 0))
        {
            return;
        }

        var snapshotProjectFields = snapshot.Fields
            .Where(field => field.IssueField is null)
            .ToDictionary(field => field.Name, StringComparer.Ordinal);
        var snapshotIssueFields = snapshot.Fields
            .Where(field => field.IssueField is not null)
            .ToDictionary(field => field.Name, StringComparer.Ordinal);
        foreach (var (name, pending) in _operationLog.PendingFields)
        {
            if (projectId is null
                || !string.Equals(pending.ProjectId, projectId, StringComparison.Ordinal)
                || !snapshotProjectFields.TryGetValue(name, out var field)
                || !string.Equals(pending.Name, field.Name, StringComparison.Ordinal)
                || !string.Equals(pending.DataType, field.DataType, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Pending field operation '{pending.OperationId}' does not match the selected project and snapshot. Resume the original import or reconcile it manually.");
            }
        }

        foreach (var (name, pending) in _operationLog.PendingIssueFields)
        {
            if (projectId is null
                || !string.Equals(pending.ProjectId, projectId, StringComparison.Ordinal)
                || !snapshotIssueFields.TryGetValue(name, out var field)
                || !string.Equals(pending.Name, field.Name, StringComparison.Ordinal)
                || !string.Equals(pending.DataType, field.DataType, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Pending Issue Field operation '{pending.OperationId}' does not match the selected project and snapshot. Resume the original import or reconcile it manually.");
            }
        }

        foreach (var (name, pending) in _operationLog.PendingIssueFieldLinks)
        {
            if (projectId is null
                || !string.Equals(pending.ProjectId, projectId, StringComparison.Ordinal)
                || !snapshotIssueFields.ContainsKey(name))
            {
                throw new InvalidOperationException(
                    $"Pending Issue Field link operation '{pending.OperationId}' does not match the selected project and snapshot. Resume the original import or reconcile it manually.");
            }
        }
    }

    private void ValidatePendingViewOperations(ProjectSnapshot snapshot, string? projectId)
    {
        if (_operationLog is null || _operationLog.PendingViews.Count == 0)
        {
            return;
        }

        var viewsByNumber = snapshot.Views.ToDictionary(view => view.Number);
        foreach (var (sourceNumber, pending) in _operationLog.PendingViews)
        {
            if (projectId is null
                || !string.Equals(pending.ProjectId, projectId, StringComparison.Ordinal)
                || pending.SourceNumber != sourceNumber
                || !viewsByNumber.TryGetValue(sourceNumber, out var view)
                || !string.Equals(pending.Name, view.Name, StringComparison.Ordinal)
                || !string.Equals(pending.Layout, view.Layout, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Pending view operation '{pending.OperationId}' does not match the selected project and snapshot. Resume the original import or reconcile it manually.");
            }
        }
    }

    /// <summary>Applies metadata, custom fields and Status options to the target project and builds the result.</summary>
    private async Task<ImportResult> ApplySnapshotAsync(
        ProjectSnapshot snapshot,
        string ownerLogin,
        ProjectRef project,
        ProjectImportOutcome outcome,
        CancellationToken cancellationToken)
    {
        _warnings.Clear();
        OnProgress?.Invoke("Applying project metadata (description, README, visibility, closed state)...");
        await UpdateProjectMetadataAsync(project.Id, snapshot.Project, cancellationToken).ConfigureAwait(false);
        if (ShouldUpdateVisibility(project.Public, snapshot.Project.Public))
        {
            await UpdateProjectVisibilityAsync(project.Id, snapshot.Project.Public, cancellationToken).ConfigureAwait(false);
        }

        List<TargetIssueField> targetIssueFields = [];
        if (OwnerType == ProjectOwnerType.Organization
            && (_snapshotIssueFieldNames.Count > 0 || _snapshotMultiSelectNormalFieldNames.Count > 0))
        {
            targetIssueFields = await FetchIssueFieldListAsync(ownerLogin, cancellationToken).ConfigureAwait(false);
        }

        _targetIssueFieldNames = targetIssueFields
            .Select(field => field.Name)
            .ToHashSet(StringComparer.Ordinal);
        _targetMultiSelectIssueFieldNames = targetIssueFields
            .Where(field => string.Equals(field.DataType, "MULTI_SELECT", StringComparison.Ordinal))
            .Select(field => field.Name)
            .ToHashSet(StringComparer.Ordinal);

        OnProgress?.Invoke("Reading existing project fields...");
        var maps = new FieldMaps();
        var existingFieldList = await FetchFieldListAsync(project.Id, maps, cancellationToken).ConfigureAwait(false);
        var existingFields = new Dictionary<string, TargetField>(StringComparer.Ordinal);
        foreach (var existingField in existingFieldList)
        {
            if (string.Equals(existingField.TypeName, "ProjectV2Field", StringComparison.Ordinal)
                && string.IsNullOrEmpty(existingField.DataType)
                && _snapshotIssueFieldNames.Contains(existingField.Name)
                && _targetIssueFieldNames.Contains(existingField.Name))
            {
                continue;
            }

            existingFields[existingField.Name] = existingField;
        }

        foreach (var field in snapshot.Fields)
        {
            if (field.IssueField is not null)
            {
                continue;
            }

            if (!CreatableDataTypes.Contains(field.DataType))
            {
                continue; // Built-in field (Title, Assignees, Labels, Repository, Milestone, Reviewers, ...).
            }

            if (_operationLog?.PendingFields.TryGetValue(field.Name, out var pendingField) == true)
            {
                if (!string.Equals(pendingField.ProjectId, project.Id, StringComparison.Ordinal)
                    || !string.Equals(pendingField.DataType, field.DataType, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Pending field operation '{pendingField.OperationId}' does not match field '{field.Name}'.");
                }

                var candidates = existingFieldList.Where(candidate =>
                    string.Equals(candidate.Name, field.Name, StringComparison.Ordinal)
                    && string.Equals(candidate.DataType, pendingField.DataType, StringComparison.Ordinal)
                    && !pendingField.ExistingFieldIds.Contains(candidate.Id, StringComparer.Ordinal)).ToArray();
                TargetField reconciled;
                if (candidates.Length > 1)
                {
                    throw new InvalidOperationException(
                        $"Pending field operation '{pendingField.OperationId}' matches multiple new fields. Reconcile the target manually.");
                }

                if (candidates.Length == 1)
                {
                    reconciled = candidates[0];
                }
                else
                {
                    reconciled = await ReconcilePendingFieldAsync(project.Id, field, maps, pendingField, cancellationToken).ConfigureAwait(false);
                }

                existingFields[field.Name] = reconciled;
                _operationLog.PendingFields.Remove(field.Name);
                await SaveOperationLogAsync(cancellationToken).ConfigureAwait(false);
            }

            if (existingFields.TryGetValue(field.Name, out var target))
            {
                if (!string.Equals(target.DataType, field.DataType, StringComparison.Ordinal))
                {
                    OnProgress?.Invoke($"warning: field '{field.Name}' exists with data type {target.DataType} (snapshot: {field.DataType}); leaving it unchanged.");
                }
                else if (field.Options is { } selectOptions
                    && (field.DataType == "SINGLE_SELECT"
                        || (field.DataType == "MULTI_SELECT" && selectOptions.Count > 0)))
                {
                    OnProgress?.Invoke(string.Create(CultureInfo.InvariantCulture,
                        $"Overwriting options of existing field '{field.Name}' with {selectOptions.Count} snapshot options..."));
                    await UpdateSelectOptionsAsync(target.Id, field.Name, field.DataType, selectOptions, maps, cancellationToken).ConfigureAwait(false);
                }
                else if (field.DataType == "ITERATION")
                {
                    OnProgress?.Invoke($"warning: iteration field '{field.Name}' already exists; iterations are not merged, leaving it unchanged.");
                }
                else
                {
                    OnProgress?.Invoke($"Field '{field.Name}' already exists; skipping.");
                }
            }
            else
            {
                OnProgress?.Invoke($"Creating {field.DataType} field '{field.Name}'...");
                var operationId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
                if (_operationLog is not null)
                {
                    _operationLog.PendingFields[field.Name] = new PendingFieldOperation
                    {
                        OperationId = operationId,
                        ProjectId = project.Id,
                        Name = field.Name,
                        DataType = field.DataType,
                        ExistingFieldIds = [],
                    };
                    await SaveOperationLogAsync(cancellationToken).ConfigureAwait(false);
                }

                JsonElement createData;
                try
                {
                    createData = await CreateFieldAsync(project.Id, field, operationId, cancellationToken).ConfigureAwait(false);
                }
                catch (AmbiguousMutationResultException)
                {
                    throw;
                }
                catch
                {
                    if (_operationLog is not null)
                    {
                        _operationLog.PendingFields.Remove(field.Name);
                        await SaveOperationLogAsync(CancellationToken.None).ConfigureAwait(false);
                    }

                    throw;
                }

                var createdField = maps.Register(createData.GetProperty("createProjectV2Field").GetProperty("projectV2Field"));
                existingFieldList.Add(createdField);
                existingFields[createdField.Name] = createdField;
                if (_operationLog is not null)
                {
                    _operationLog.PendingFields.Remove(field.Name);
                    await SaveOperationLogAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        await ApplyIssueFieldsAsync(
            snapshot.Fields.Where(field => field.IssueField is not null).ToList(),
            ownerLogin,
            project.Id,
            existingFieldList,
            existingFields,
            maps,
            targetIssueFields,
            cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<int, int> viewNumbers = ReadOnlyDictionary<int, int>.Empty;
        var viewWarningCount = 0;
        if (snapshot.Views.Count > 0)
        {
            var viewImporter = CreateViewImporter();
            viewNumbers = await viewImporter.ImportAsync(
                snapshot.Views,
                project.Id,
                maps.FieldIds,
                outcome,
                cancellationToken).ConfigureAwait(false);
            foreach (var warning in viewImporter.Warnings)
            {
                _warnings.Add(warning);
            }

            viewWarningCount = viewImporter.Warnings.Count;
        }

        await ApplyCollaboratorsAsync(project.Id, ownerLogin, snapshot.Collaborators, cancellationToken).ConfigureAwait(false);
        await ApplyLinkedRepositoriesAsync(project.Id, snapshot.LinkedRepositories, cancellationToken).ConfigureAwait(false);

        OnProgress?.Invoke(string.Create(CultureInfo.InvariantCulture,
            $"Import finished: project #{project.Number}, {maps.FieldIds.Count} fields mapped."));
        return maps.ToResult(project, outcome, viewNumbers, viewWarningCount);
    }

    private ProjectViewImporter CreateViewImporter() => new(
        _client,
        _operationLog ?? throw new InvalidOperationException("The project operation log was not initialized."),
        SaveOperationLogAsync)
    {
        RepositoryMapping = RepositoryMapping,
        UserMapping = UserMapping,
        OrganizationMapping = OrganizationMapping,
        BrowserEnrichmentPlanned = BrowserViewEnrichmentPlanned,
        OnProgress = OnProgress,
    };

    private async Task ApplyIssueFieldsAsync(
        List<FieldSnapshot> fields,
        string ownerLogin,
        string projectId,
        List<TargetField> projectFields,
        Dictionary<string, TargetField> projectFieldsByName,
        FieldMaps maps,
        List<TargetIssueField> issueFields,
        CancellationToken cancellationToken)
    {
        if (fields.Count == 0)
        {
            return;
        }

        if (OwnerType == ProjectOwnerType.User)
        {
            Warn("organization Issue Fields cannot be imported into a user-owned project; skipping linked Issue Fields.");
            return;
        }

        var issueFieldGroups = issueFields.GroupBy(field => field.Name, StringComparer.Ordinal).ToList();
        var duplicateIssueFieldNames = issueFieldGroups
            .Where(group => group.Skip(1).Any())
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        var issueFieldsByName = issueFieldGroups
            .Where(group => !duplicateIssueFieldNames.Contains(group.Key))
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        string? ownerId = null;

        foreach (var field in fields)
        {
            TargetIssueField targetIssueField;
            if (_operationLog?.PendingIssueFields.TryGetValue(field.Name, out var pendingField) == true)
            {
                if (!string.Equals(pendingField.ProjectId, projectId, StringComparison.Ordinal)
                    || !string.Equals(pendingField.OwnerLogin, ownerLogin, StringComparison.Ordinal)
                    || !string.Equals(pendingField.DataType, field.DataType, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Pending Issue Field operation '{pendingField.OperationId}' does not match field '{field.Name}'.");
                }

                targetIssueField = await ReconcilePendingIssueFieldAsync(
                    ownerLogin,
                    field,
                    issueFields,
                    pendingField,
                    cancellationToken).ConfigureAwait(false);
                if (IssueFieldNeedsUpdate(field, targetIssueField))
                {
                    targetIssueField = await UpdateIssueFieldAsync(
                        targetIssueField.Id,
                        field,
                        cancellationToken).ConfigureAwait(false);
                }

                issueFields.Add(targetIssueField);
                issueFieldsByName[field.Name] = targetIssueField;
                _operationLog.PendingIssueFields.Remove(field.Name);
                await SaveOperationLogAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (duplicateIssueFieldNames.Contains(field.Name))
            {
                throw new InvalidOperationException(
                    $"Multiple organization Issue Fields named '{field.Name}' exist in the target. Reconcile them before importing.");
            }
            else if (issueFieldsByName.TryGetValue(field.Name, out var existing))
            {
                if (!string.Equals(existing.DataType, field.DataType, StringComparison.Ordinal))
                {
                    Warn($"Issue Field '{field.Name}' exists with data type {existing.DataType} (snapshot: {field.DataType}); leaving it unchanged and skipping its values.");
                    continue;
                }

                targetIssueField = IssueFieldNeedsUpdate(field, existing)
                    ? await UpdateIssueFieldAsync(existing.Id, field, cancellationToken).ConfigureAwait(false)
                    : existing;
                issueFieldsByName[field.Name] = targetIssueField;
            }
            else
            {
                OnProgress?.Invoke($"Creating organization Issue Field {field.DataType} '{field.Name}'...");
                ownerId ??= await GetOwnerIdAsync(ownerLogin, cancellationToken).ConfigureAwait(false);
                var operationId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
                if (_operationLog is not null)
                {
                    _operationLog.PendingIssueFields[field.Name] = new PendingIssueFieldOperation
                    {
                        OperationId = operationId,
                        ProjectId = projectId,
                        OwnerLogin = ownerLogin,
                        Name = field.Name,
                        DataType = field.DataType,
                        ExistingIssueFieldIds = [.. issueFields.Select(candidate => candidate.Id)],
                    };
                    await SaveOperationLogAsync(cancellationToken).ConfigureAwait(false);
                }

                try
                {
                    targetIssueField = await CreateIssueFieldAsync(ownerId, field, operationId, cancellationToken).ConfigureAwait(false);
                }
                catch (AmbiguousMutationResultException)
                {
                    throw;
                }
                catch
                {
                    if (_operationLog is not null)
                    {
                        _operationLog.PendingIssueFields.Remove(field.Name);
                        await SaveOperationLogAsync(CancellationToken.None).ConfigureAwait(false);
                    }

                    throw;
                }

                issueFields.Add(targetIssueField);
                issueFieldsByName[field.Name] = targetIssueField;
                if (_operationLog is not null)
                {
                    _operationLog.PendingIssueFields.Remove(field.Name);
                    await SaveOperationLogAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            maps.RegisterIssueField(targetIssueField);
            _targetIssueFieldNames.Add(targetIssueField.Name);
            await EnsureIssueFieldLinkedAsync(
                projectId,
                targetIssueField,
                projectFields,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<List<TargetIssueField>> FetchIssueFieldListAsync(
        string ownerLogin,
        CancellationToken cancellationToken)
    {
        var fields = new List<TargetIssueField>();
        await foreach (var node in _client.QueryPaginatedAsync(
            IssueFieldsQuery,
            new { login = ownerLogin, first = 100 },
            "organization.issueFields",
            cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            fields.Add(ParseTargetIssueField(node));
        }

        return fields;
    }

    private async Task<TargetIssueField> CreateIssueFieldAsync(
        string ownerId,
        FieldSnapshot field,
        string clientMutationId,
        CancellationToken cancellationToken)
    {
        var data = await _client.MutationAsync(
            "createIssueField",
            CreateIssueFieldMutation,
            new
            {
                ownerId,
                name = field.Name,
                description = field.IssueField?.Description,
                dataType = field.DataType,
                options = IsSelectIssueField(field.DataType) ? BuildIssueFieldOptionInputs(field.Options ?? []) : null,
                visibility = field.IssueField?.Visibility,
            },
            target: ownerId,
            clientMutationId: clientMutationId,
            requiredResultPath: "issueField.id",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseTargetIssueField(data.GetProperty("createIssueField").GetProperty("issueField"));
    }

    private async Task<TargetIssueField> UpdateIssueFieldAsync(
        string issueFieldId,
        FieldSnapshot field,
        CancellationToken cancellationToken)
    {
        OnProgress?.Invoke($"Updating organization Issue Field '{field.Name}' metadata and options...");
        var data = await _client.MutationAsync(
            "updateIssueField",
            UpdateIssueFieldMutation,
            new
            {
                id = issueFieldId,
                description = field.IssueField?.Description,
                options = IsSelectIssueField(field.DataType) ? BuildIssueFieldOptionInputs(field.Options ?? []) : null,
                visibility = field.IssueField?.Visibility,
            },
            MutationRetryPolicy.Idempotent,
            target: issueFieldId,
            requiredResultPath: "issueField.id",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseTargetIssueField(data.GetProperty("updateIssueField").GetProperty("issueField"));
    }

    private async Task EnsureIssueFieldLinkedAsync(
        string projectId,
        TargetIssueField issueField,
        List<TargetField> projectFields,
        CancellationToken cancellationToken)
    {
        if (_operationLog?.PendingIssueFieldLinks.TryGetValue(issueField.Name, out var pendingLink) == true)
        {
            if (!string.Equals(pendingLink.ProjectId, projectId, StringComparison.Ordinal)
                || !string.Equals(pendingLink.IssueFieldId, issueField.Id, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Pending Issue Field link operation '{pendingLink.OperationId}' does not match field '{issueField.Name}'.");
            }

            OnProgress?.Invoke($"Resuming organization Issue Field link '{issueField.Name}' with an idempotent mutation...");
            await CreateProjectIssueFieldAsync(
                projectId,
                issueField.Id,
                pendingLink.OperationId,
                MutationRetryPolicy.Idempotent,
                cancellationToken).ConfigureAwait(false);
            _operationLog.PendingIssueFieldLinks.Remove(issueField.Name);
            await SaveOperationLogAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        OnProgress?.Invoke($"Ensuring organization Issue Field '{issueField.Name}' is linked to the project...");
        var operationId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        if (_operationLog is not null)
        {
            _operationLog.PendingIssueFieldLinks[issueField.Name] = new PendingIssueFieldLinkOperation
            {
                OperationId = operationId,
                ProjectId = projectId,
                IssueFieldId = issueField.Id,
                Name = issueField.Name,
                ExistingFieldIds = [.. projectFields.Select(field => field.Id)],
            };
            await SaveOperationLogAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await CreateProjectIssueFieldAsync(
                projectId,
                issueField.Id,
                operationId,
                MutationRetryPolicy.Create,
                cancellationToken).ConfigureAwait(false);
        }
        catch (AmbiguousMutationResultException)
        {
            throw;
        }
        catch
        {
            if (_operationLog is not null)
            {
                _operationLog.PendingIssueFieldLinks.Remove(issueField.Name);
                await SaveOperationLogAsync(CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }

        if (_operationLog is not null)
        {
            _operationLog.PendingIssueFieldLinks.Remove(issueField.Name);
            await SaveOperationLogAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CreateProjectIssueFieldAsync(
        string projectId,
        string issueFieldId,
        string clientMutationId,
        MutationRetryPolicy retryPolicy,
        CancellationToken cancellationToken)
        => await _client.MutationAsync(
            "createProjectV2IssueField",
            """
            mutation($projectId: ID!, $issueFieldId: ID!, $clientMutationId: String!) {
              createProjectV2IssueField(input: { projectId: $projectId, issueFieldId: $issueFieldId, clientMutationId: $clientMutationId }) {
                clientMutationId
              }
            }
            """,
            new { projectId, issueFieldId },
            retryPolicy,
            target: projectId,
            clientMutationId: clientMutationId,
            requiredResultPath: "clientMutationId",
            cancellationToken: cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Applies snapshot collaborators through a single <c>updateProjectV2Collaborators</c>
    /// call. User logins go through <see cref="UserMapping"/> (unmapped logins are used
    /// as-is); team slugs are resolved in the target organization. Unresolvable
    /// collaborators are skipped with a warning. Note: exports never populate
    /// collaborators (the API has no read field), so this only runs for hand-authored
    /// snapshots.
    /// </summary>
    private async Task ApplyCollaboratorsAsync(string projectId, string ownerLogin, IReadOnlyList<CollaboratorSnapshot>? collaborators, CancellationToken cancellationToken)
    {
        if (collaborators is not { Count: > 0 })
        {
            return;
        }

        var inputs = new List<object>();
        foreach (var collaborator in collaborators)
        {
            if (string.Equals(collaborator.Type, "USER", StringComparison.OrdinalIgnoreCase))
            {
                var login = UserMapping.TryGetValue(collaborator.Login, out var mapped) ? mapped : collaborator.Login;
                var userId = await ResolveUserIdAsync(login, cancellationToken).ConfigureAwait(false);
                if (userId is null)
                {
                    Warn($"collaborator user '{login}' was not found; skipping.");
                    continue;
                }

                inputs.Add(new { userId, role = collaborator.Role });
            }
            else if (string.Equals(collaborator.Type, "TEAM", StringComparison.OrdinalIgnoreCase))
            {
                if (OwnerType == ProjectOwnerType.User)
                {
                    Warn($"collaborator team '{collaborator.Login}': team collaborators are not supported on user projects; skipping.");
                    continue;
                }

                var teamId = await ResolveTeamIdAsync(ownerLogin, collaborator.Login, cancellationToken).ConfigureAwait(false);
                if (teamId is null)
                {
                    Warn($"collaborator team '{collaborator.Login}' was not found in organization '{ownerLogin}'; skipping.");
                    continue;
                }

                inputs.Add(new { teamId, role = collaborator.Role });
            }
            else
            {
                Warn($"collaborator '{collaborator.Login}': unknown type '{collaborator.Type}'; skipping.");
            }
        }

        if (inputs.Count == 0)
        {
            return; // The mutation rejects an empty collaborators list.
        }

        OnProgress?.Invoke(string.Create(CultureInfo.InvariantCulture,
            $"Applying {inputs.Count} project collaborators..."));
        await _client.MutationAsync(
            "updateProjectV2Collaborators",
            """
            mutation($projectId: ID!, $collaborators: [ProjectV2Collaborator!]!, $clientMutationId: String!) {
              updateProjectV2Collaborators(input: { projectId: $projectId, collaborators: $collaborators, clientMutationId: $clientMutationId }) {
                collaborators(first: 100) { nodes { __typename } }
              }
            }
            """,
            new { projectId, collaborators = inputs.ToArray() },
            MutationRetryPolicy.Idempotent,
            target: projectId,
            requiredResultPath: "collaborators.nodes",
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Links the snapshot's linked repositories to the target project via
    /// <c>linkProjectV2ToRepository</c>. Repository names go through
    /// <see cref="RepositoryMapping"/> (unmapped names are used as-is); repositories
    /// that cannot be resolved or linked (e.g. not visible to the target account, or
    /// outside a GHEC-DR tenant) are skipped with a warning.
    /// </summary>
    private async Task ApplyLinkedRepositoriesAsync(string projectId, IReadOnlyList<string>? repositories, CancellationToken cancellationToken)
    {
        if (repositories is not { Count: > 0 })
        {
            return;
        }

        foreach (var repository in repositories)
        {
            var mapped = RepositoryMapping.TryGetValue(repository, out var target) ? target : repository;
            var separator = mapped.IndexOf('/', StringComparison.Ordinal);
            if (separator <= 0 || separator == mapped.Length - 1)
            {
                Warn($"linked repository '{mapped}' is not in 'owner/name' form; skipping.");
                continue;
            }

            var repositoryId = await ResolveRepositoryIdAsync(mapped[..separator], mapped[(separator + 1)..], cancellationToken).ConfigureAwait(false);
            if (repositoryId is null)
            {
                Warn($"linked repository '{mapped}' was not found; skipping.");
                continue;
            }

            try
            {
                await _client.MutationAsync(
                    "linkProjectV2ToRepository",
                    """
                    mutation($projectId: ID!, $repositoryId: ID!, $clientMutationId: String!) {
                      linkProjectV2ToRepository(input: { projectId: $projectId, repositoryId: $repositoryId, clientMutationId: $clientMutationId }) {
                        repository { nameWithOwner }
                      }
                    }
                    """,
                    new { projectId, repositoryId },
                    MutationRetryPolicy.Idempotent,
                    target: projectId,
                    requiredResultPath: "repository.nameWithOwner",
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                OnProgress?.Invoke($"Linked repository '{mapped}'.");
            }
            catch (GitHubGraphQLException exception)
            {
                Warn($"could not link repository '{mapped}': {exception.Message}");
            }
        }
    }

    private async Task<string?> ResolveUserIdAsync(string login, CancellationToken cancellationToken)
    {
        try
        {
            var data = await _client.QueryAsync(
                "query($login: String!) { user(login: $login) { id } }",
                new { login },
                cancellationToken).ConfigureAwait(false);

            var user = data.GetProperty("user");
            return user.ValueKind == JsonValueKind.Object ? user.GetProperty("id").GetString() : null;
        }
        catch (GitHubGraphQLException exception) when (exception.ErrorType == "NOT_FOUND")
        {
            return null;
        }
    }

    private async Task<string?> ResolveTeamIdAsync(string organizationLogin, string teamSlug, CancellationToken cancellationToken)
    {
        try
        {
            var data = await _client.QueryAsync(
                "query($login: String!, $slug: String!) { organization(login: $login) { team(slug: $slug) { id } } }",
                new { login = organizationLogin, slug = teamSlug },
                cancellationToken).ConfigureAwait(false);

            var organization = data.GetProperty("organization");
            if (organization.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var team = organization.GetProperty("team");
            return team.ValueKind == JsonValueKind.Object ? team.GetProperty("id").GetString() : null;
        }
        catch (GitHubGraphQLException exception) when (exception.ErrorType == "NOT_FOUND")
        {
            return null;
        }
    }

    private async Task<string?> ResolveRepositoryIdAsync(string owner, string name, CancellationToken cancellationToken)
    {
        try
        {
            var data = await _client.QueryAsync(
                "query($owner: String!, $name: String!) { repository(owner: $owner, name: $name) { id } }",
                new { owner, name },
                cancellationToken).ConfigureAwait(false);

            var repository = data.GetProperty("repository");
            return repository.ValueKind == JsonValueKind.Object ? repository.GetProperty("id").GetString() : null;
        }
        catch (GitHubGraphQLException exception) when (exception.ErrorType == "NOT_FOUND")
        {
            return null;
        }
    }

    private void Warn(string message)
    {
        _warnings.Add(message);
        OnProgress?.Invoke("warning: " + message);
    }

    private static ImportResult BuildSkippedResult(ProjectRef project) => new()
    {
        ProjectId = project.Id,
        ProjectNumber = project.Number,
        Url = project.Url,
        Outcome = ProjectImportOutcome.Skipped,
        FieldIds = ReadOnlyDictionary<string, string>.Empty,
        OptionIds = ReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>.Empty,
        IterationIds = ReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>.Empty,
    };

    private string OwnerField => OwnerType == ProjectOwnerType.User ? "user" : "organization";

    private string OwnerDescription => OwnerType == ProjectOwnerType.User ? "user" : "organization";

    private async Task<string> GetOwnerIdAsync(string ownerLogin, CancellationToken cancellationToken)
    {
        var query = OwnerType == ProjectOwnerType.User
            ? "query($login: String!) { user(login: $login) { id } }"
            : "query($login: String!) { organization(login: $login) { id } }";

        var data = await _client.QueryAsync(query, new { login = ownerLogin }, cancellationToken).ConfigureAwait(false);

        return data.GetProperty(OwnerField).GetProperty("id").GetString()
            ?? throw new GitHubGraphQLException($"{(OwnerType == ProjectOwnerType.User ? "User" : "Organization")} '{ownerLogin}' was not found.");
    }

    private async Task<List<ProjectRef>> FindProjectsByTitleAsync(string ownerLogin, string title, CancellationToken cancellationToken)
    {
        var projects = new List<ProjectRef>();
        await foreach (var node in _client.QueryPaginatedAsync(
            FindProjectQueryTemplate.Replace("__OWNER__", OwnerField, StringComparison.Ordinal),
            new { login = ownerLogin, first = 50 },
            OwnerField + ".projectsV2",
            cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(node.GetProperty("title").GetString(), title, StringComparison.Ordinal))
            {
                projects.Add(ParseProjectRef(node));
            }
        }

        return projects;
    }

    private async Task<ProjectRef?> FindProjectByNumberAsync(string ownerLogin, int projectNumber, CancellationToken cancellationToken)
    {
        try
        {
            var data = await _client.QueryAsync(
                FindProjectByNumberQueryTemplate.Replace("__OWNER__", OwnerField, StringComparison.Ordinal),
                new { login = ownerLogin, number = projectNumber },
                cancellationToken).ConfigureAwait(false);

            var project = data.GetProperty(OwnerField).GetProperty("projectV2");
            return project.ValueKind == JsonValueKind.Object ? ParseProjectRef(project) : null;
        }
        catch (GitHubGraphQLException exception) when (exception.ErrorType == "NOT_FOUND")
        {
            return null;
        }
    }

    private async Task<JsonElement> CreateProjectAsync(
        string ownerId,
        string title,
        string clientMutationId,
        CancellationToken cancellationToken)
    {
        return await _client.MutationAsync(
            "createProjectV2",
            """
            mutation($ownerId: ID!, $title: String!, $clientMutationId: String!) {
              createProjectV2(input: { ownerId: $ownerId, title: $title, clientMutationId: $clientMutationId }) {
                projectV2 { id number title url public }
              }
            }
            """,
            new { ownerId, title },
            target: ownerId,
            clientMutationId: clientMutationId,
            requiredResultPath: "projectV2.id",
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task UpdateProjectMetadataAsync(string projectId, ProjectInfoSnapshot info, CancellationToken cancellationToken)
    {
        await _client.MutationAsync(
            "updateProjectV2",
            """
            mutation($projectId: ID!, $shortDescription: String, $readme: String, $closed: Boolean, $clientMutationId: String!) {
              updateProjectV2(input: { projectId: $projectId, shortDescription: $shortDescription, readme: $readme, closed: $closed, clientMutationId: $clientMutationId }) {
                projectV2 { id }
              }
            }
            """,
            new
            {
                projectId,
                shortDescription = info.ShortDescription,
                readme = info.Readme,
                closed = info.Closed,
            },
            MutationRetryPolicy.Idempotent,
            target: projectId,
            requiredResultPath: "projectV2.id",
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task UpdateProjectVisibilityAsync(string projectId, bool isPublic, CancellationToken cancellationToken)
    {
        await _client.MutationAsync(
            "updateProjectV2",
            """
            mutation($projectId: ID!, $public: Boolean, $clientMutationId: String!) {
              updateProjectV2(input: { projectId: $projectId, public: $public, clientMutationId: $clientMutationId }) {
                projectV2 { id }
              }
            }
            """,
            new { projectId, @public = isPublic },
            MutationRetryPolicy.Idempotent,
            target: projectId,
            requiredResultPath: "projectV2.id",
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<TargetField>> FetchFieldListAsync(string projectId, FieldMaps maps, CancellationToken cancellationToken)
    {
        if (_snapshotIssueFieldNames.Count == 0)
        {
            var data = await _client.QueryAsync(FieldsQuery, new { id = projectId }, cancellationToken).ConfigureAwait(false);
            var directNodes = data.GetProperty("node").GetProperty("fields").GetProperty("nodes").EnumerateArray().ToArray();
            ThrowIfAmbiguousTargetMultiSelectFields(directNodes);
            return [.. directNodes.Select(maps.Register)];
        }

        List<JsonElement> nodes;
        if (_snapshotMultiSelectIssueFieldNames.Count > 0)
        {
            OnProgress?.Invoke(
                "Reading normal project fields individually; linked multi-select Issue Fields are reconciled with an idempotent link mutation.");
            nodes = await FetchFieldNodesByNameAsync(projectId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            try
            {
                var safeData = await _client.QueryWithoutInternalErrorRetryAsync(
                    FieldsWithIssueFieldsQuery,
                    new { id = projectId },
                    cancellationToken).ConfigureAwait(false);
                nodes = [.. safeData.GetProperty("node").GetProperty("fields").GetProperty("nodes").EnumerateArray()];
            }
            catch (GitHubGraphQLException exception) when (IsPreviewFieldInternalError(exception))
            {
                OnProgress?.Invoke(
                    "GitHub's preview API could not enumerate this project's linked Issue Fields. " +
                    "Querying normal project fields individually instead.");
                nodes = await FetchFieldNodesByNameAsync(projectId, cancellationToken).ConfigureAwait(false);
            }
        }

        ThrowIfAmbiguousTargetMultiSelectFields(nodes);
        Dictionary<string, JsonElement> details = [];
        var detailIds = nodes
            .Where(node => node.TryGetProperty("__typename", out var typeName)
                && typeName.GetString() is "ProjectV2SingleSelectField"
                    or "ProjectV2MultiSelectField"
                    or "ProjectV2IterationField")
            .Select(node => node.GetProperty("id").GetString() ?? string.Empty)
            .ToArray();
        if (detailIds.Length > 0)
        {
            var detailData = await _client.QueryAsync(
                FieldDetailsQuery,
                new { ids = detailIds },
                cancellationToken).ConfigureAwait(false);
            details = detailData.GetProperty("nodes").EnumerateArray()
                .Where(node => node.ValueKind == JsonValueKind.Object)
                .ToDictionary(
                    node => node.GetProperty("id").GetString() ?? string.Empty,
                    StringComparer.Ordinal);
        }

        var candidates = nodes
            .Where(node => node.TryGetProperty("__typename", out var typeName)
                && typeName.GetString() == "ProjectV2Field"
                && !node.TryGetProperty("dataType", out _))
            .ToArray();
        Dictionary<string, string> dataTypes = [];
        var unambiguousIds = candidates
            .Where(candidate => !_targetMultiSelectIssueFieldNames.Contains(
                candidate.GetProperty("name").GetString() ?? string.Empty))
            .Select(candidate => candidate.GetProperty("id").GetString() ?? string.Empty)
            .ToArray();
        if (unambiguousIds.Length > 0)
        {
            AddFieldDataTypes(
                dataTypes,
                await _client.QueryAsync(
                    FieldDataTypesQuery,
                    new { ids = unambiguousIds },
                    cancellationToken).ConfigureAwait(false));
        }

        foreach (var candidate in candidates.Where(candidate =>
                     _targetMultiSelectIssueFieldNames.Contains(
                         candidate.GetProperty("name").GetString() ?? string.Empty)))
        {
            var candidateId = candidate.GetProperty("id").GetString() ?? string.Empty;
            var candidateName = candidate.GetProperty("name").GetString() ?? string.Empty;
            try
            {
                AddFieldDataTypes(
                    dataTypes,
                    await _client.QueryAsync(
                        FieldDataTypesQuery,
                        new { ids = new[] { candidateId } },
                        cancellationToken).ConfigureAwait(false));
            }
            catch (GitHubGraphQLException exception) when (
                _targetMultiSelectIssueFieldNames.Contains(candidateName)
                && IsPreviewFieldInternalError(exception))
            {
                // GitHub's preview schema cannot resolve dataType for a linked multi-select Issue Field.
            }
        }

        var unresolvedSameNamedNormalField = _snapshotNormalFieldNames
            .Intersect(_targetIssueFieldNames, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .FirstOrDefault(name =>
            {
                var sameNamedCandidates = candidates.Where(candidate =>
                    string.Equals(candidate.GetProperty("name").GetString(), name, StringComparison.Ordinal)).ToArray();
                return sameNamedCandidates.Length > 0
                    && (!_targetMultiSelectIssueFieldNames.Contains(name)
                        || !sameNamedCandidates.Any(candidate =>
                            dataTypes.ContainsKey(candidate.GetProperty("id").GetString() ?? string.Empty)));
            });
        if (unresolvedSameNamedNormalField is not null)
        {
            throw new GitHubGraphQLException(
                $"GitHub's preview API could not identify ordinary field '{unresolvedSameNamedNormalField}' separately " +
                "from a same-named linked Issue Field. Reconcile the target manually before importing.");
        }

        return
        [
            .. nodes.Select(node =>
            {
                var id = node.GetProperty("id").GetString() ?? string.Empty;
                var fieldNode = details.TryGetValue(id, out var detail) ? detail : node;
                var isIssueFieldLink = node.TryGetProperty("__typename", out var typeName)
                    && typeName.GetString() == "ProjectV2Field"
                    && _targetIssueFieldNames.Contains(node.GetProperty("name").GetString() ?? string.Empty)
                    && !node.TryGetProperty("dataType", out _)
                    && !dataTypes.ContainsKey(id);
                return isIssueFieldLink
                    ? ParseIssueFieldLink(fieldNode)
                    : maps.Register(fieldNode, dataTypes);
            }),
        ];
    }

    private void ThrowIfAmbiguousTargetMultiSelectFields(IEnumerable<JsonElement> nodes)
    {
        var ambiguousMultiSelect = nodes
            .Where(node => node.TryGetProperty("__typename", out var typeName)
                && string.Equals(typeName.GetString(), "ProjectV2MultiSelectField", StringComparison.Ordinal))
            .Select(node => node.GetProperty("name").GetString() ?? string.Empty)
            .FirstOrDefault(name =>
                _snapshotMultiSelectNormalFieldNames.Contains(name)
                && _targetMultiSelectIssueFieldNames.Contains(name));
        if (ambiguousMultiSelect is not null)
        {
            throw new GitHubGraphQLException(
                $"Target project field '{ambiguousMultiSelect}' is ambiguous: GitHub's GraphQL API cannot distinguish an ordinary MULTI_SELECT field from a same-named linked organization Issue Field. Rename or remove the target collision before importing.");
        }
    }

    private async Task<List<JsonElement>> FetchFieldNodesByNameAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        var nodes = new List<JsonElement>();
        foreach (var name in _snapshotNormalFieldNames)
        {
            JsonElement data;
            try
            {
                data = await _client.QueryAsync(
                    FieldByNameQuery,
                    new { id = projectId, name },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (GitHubGraphQLException exception) when (IsMissingProjectFieldError(exception))
            {
                continue;
            }

            var node = data.GetProperty("node").GetProperty("field");
            if (node.ValueKind == JsonValueKind.Object)
            {
                nodes.Add(node);
            }
        }

        return nodes;
    }

    private static bool IsPreviewFieldInternalError(GitHubGraphQLException exception)
        => exception.ErrorsJson?.Contains(
            "Something went wrong while executing your query",
            StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsMissingProjectFieldError(GitHubGraphQLException exception)
        => exception.ErrorsJson?.Contains("\"type\":\"NOT_FOUND\"", StringComparison.Ordinal) == true
            && exception.ErrorsJson.Contains("ProjectV2FieldConfiguration", StringComparison.Ordinal);

    private static void AddFieldDataTypes(Dictionary<string, string> result, JsonElement data)
    {
        foreach (var node in data.GetProperty("nodes").EnumerateArray().Where(node => node.ValueKind == JsonValueKind.Object))
        {
            result[node.GetProperty("id").GetString() ?? string.Empty] =
                node.GetProperty("dataType").GetString() ?? string.Empty;
        }
    }

    private async Task<JsonElement> CreateFieldAsync(
        string projectId,
        FieldSnapshot field,
        string clientMutationId,
        CancellationToken cancellationToken)
    {
        return await _client.MutationAsync(
            "createProjectV2Field",
            CreateFieldMutation,
            new
            {
                projectId,
                name = field.Name,
                dataType = field.DataType,
                options = field.DataType == "SINGLE_SELECT" ? BuildOptionInputs(field.Options ?? []) : null,
                multiSelectOptions = field.DataType == "MULTI_SELECT" ? BuildOptionInputs(field.Options ?? []) : null,
                iterationConfiguration = field.DataType == "ITERATION" && field.IterationConfiguration is { } configuration
                    ? BuildIterationConfigurationInput(field.Name, configuration)
                    : null,
            },
            target: projectId,
            clientMutationId: clientMutationId,
            requiredResultPath: "projectV2Field.id",
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task LoadOperationLogAsync(CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(OperationLogDirectory);
        _operationLog = await ProjectImportLog.LoadAsync(OperationLogDirectory, cancellationToken).ConfigureAwait(false);
    }

    private Task SaveOperationLogAsync(CancellationToken cancellationToken)
        => _operationLog is not null
            ? _operationLog.SaveAsync(OperationLogDirectory, cancellationToken)
            : Task.CompletedTask;

    private async Task<ProjectRef> ReconcilePendingProjectAsync(
        PendingProjectOperation pending,
        IReadOnlyList<ProjectRef> initialMatches,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ProjectRef> matches = initialMatches;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var baseline = new HashSet<string>(pending.ExistingProjectIds, StringComparer.Ordinal);
            var candidates = matches.Where(project => !baseline.Contains(project.Id)).ToArray();
            if (candidates.Length == 1)
            {
                return candidates[0];
            }

            if (candidates.Length > 1)
            {
                throw new InvalidOperationException(
                    $"Pending project operation '{pending.OperationId}' matches multiple new projects. Reconcile the target manually.");
            }

            if (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken).ConfigureAwait(false);
                matches = await FindProjectsByTitleAsync(pending.OwnerLogin, pending.Title, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException(
            $"Pending project operation '{pending.OperationId}' is not visible after reconciliation polling. Do not resend it until the target is reconciled manually.");
    }

    private async Task<TargetIssueField> ReconcilePendingIssueFieldAsync(
        string ownerLogin,
        FieldSnapshot field,
        IReadOnlyList<TargetIssueField> initialFields,
        PendingIssueFieldOperation pending,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<TargetIssueField> fields = initialFields;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var baseline = new HashSet<string>(pending.ExistingIssueFieldIds, StringComparer.Ordinal);
            var candidates = fields.Where(candidate =>
                string.Equals(candidate.Name, field.Name, StringComparison.Ordinal)
                && string.Equals(candidate.DataType, field.DataType, StringComparison.Ordinal)
                && !baseline.Contains(candidate.Id)).ToArray();
            if (candidates.Length == 1)
            {
                return candidates[0];
            }

            if (candidates.Length > 1)
            {
                throw new InvalidOperationException(
                    $"Pending Issue Field operation '{pending.OperationId}' matches multiple new Issue Fields. Reconcile the target organization manually.");
            }

            if (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken).ConfigureAwait(false);
                fields = await FetchIssueFieldListAsync(ownerLogin, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException(
            $"Pending Issue Field operation '{pending.OperationId}' is not visible after reconciliation polling. Do not resend it until the target organization is reconciled manually.");
    }

    private async Task<TargetField> ReconcilePendingFieldAsync(
        string projectId,
        FieldSnapshot field,
        FieldMaps maps,
        PendingFieldOperation pending,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), cancellationToken).ConfigureAwait(false);
            }

            var fields = await FetchFieldListAsync(projectId, maps, cancellationToken).ConfigureAwait(false);
            var candidates = fields.Where(candidate =>
                string.Equals(candidate.Name, field.Name, StringComparison.Ordinal)
                && string.Equals(candidate.DataType, field.DataType, StringComparison.Ordinal)
                && !pending.ExistingFieldIds.Contains(candidate.Id, StringComparer.Ordinal)).ToArray();
            if (candidates.Length == 1)
            {
                return candidates[0];
            }

            if (candidates.Length > 1)
            {
                throw new InvalidOperationException(
                    $"Pending field operation '{pending.OperationId}' matches multiple new fields. Reconcile the target manually.");
            }
        }

        throw new InvalidOperationException(
            $"Pending field operation '{pending.OperationId}' is not visible after reconciliation polling. Do not resend it until the target is reconciled manually.");
    }

    private async Task UpdateSelectOptionsAsync(
        string fieldId,
        string fieldName,
        string dataType,
        IReadOnlyList<SingleSelectOptionSnapshot> options,
        FieldMaps maps,
        CancellationToken cancellationToken)
    {
        var mutation = dataType == "MULTI_SELECT"
            ? """
              mutation($fieldId: ID!, $options: [ProjectV2MultiSelectFieldOptionInput!]!, $clientMutationId: String!) {
                updateProjectV2Field(input: { fieldId: $fieldId, multiSelectOptions: $options, clientMutationId: $clientMutationId }) {
                  projectV2Field {
                    __typename
                    ... on ProjectV2FieldCommon { id name dataType }
                    ... on ProjectV2MultiSelectField { multiSelectOptions { id name } }
                  }
                }
              }
              """
            : """
              mutation($fieldId: ID!, $options: [ProjectV2SingleSelectFieldOptionInput!]!, $clientMutationId: String!) {
                updateProjectV2Field(input: { fieldId: $fieldId, singleSelectOptions: $options, clientMutationId: $clientMutationId }) {
                  projectV2Field {
                    __typename
                    ... on ProjectV2FieldCommon { id name dataType }
                    ... on ProjectV2SingleSelectField { options { id name } }
                  }
                }
              }
              """;
        IReadOnlyDictionary<string, string>? existingOptionIds = null;
        if (dataType == "MULTI_SELECT")
        {
            maps.OptionIds.TryGetValue(fieldName, out existingOptionIds);
        }

        var data = await _client.MutationAsync(
            "updateProjectV2Field",
            mutation,
            new { fieldId, options = BuildOptionInputs(options, existingOptionIds) },
            MutationRetryPolicy.Idempotent,
            target: fieldId,
            requiredResultPath: "projectV2Field.id",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        maps.Register(data.GetProperty("updateProjectV2Field").GetProperty("projectV2Field"));
    }

    private static object[] BuildOptionInputs(
        IReadOnlyList<SingleSelectOptionSnapshot> options,
        IReadOnlyDictionary<string, string>? existingOptionIds = null)
        =>
        [
            .. options.Select(option =>
            {
                var input = new Dictionary<string, object?>
                {
                    ["name"] = option.Name,
                    ["color"] = option.Color,
                    ["description"] = option.Description ?? string.Empty,
                };
                if (existingOptionIds?.TryGetValue(option.Name, out var existingOptionId) == true)
                {
                    input["id"] = existingOptionId;
                }

                return (object)input;
            }),
        ];

    private static object[] BuildIssueFieldOptionInputs(IReadOnlyList<SingleSelectOptionSnapshot> options)
        => [.. options.Select((option, priority) => new
        {
            name = option.Name,
            color = option.Color,
            description = option.Description ?? string.Empty,
            priority,
        })];

    private static bool IsSelectIssueField(string dataType)
        => dataType is "SINGLE_SELECT" or "MULTI_SELECT";

    private static bool IssueFieldNeedsUpdate(FieldSnapshot source, TargetIssueField target)
    {
        if (!string.Equals(source.IssueField?.Description, target.Description, StringComparison.Ordinal)
            || !string.Equals(source.IssueField?.Visibility, target.Visibility, StringComparison.Ordinal))
        {
            return true;
        }

        var sourceOptions = source.Options ?? [];
        var targetOptions = target.Options ?? [];
        return sourceOptions.Count != targetOptions.Count
            || sourceOptions.Zip(targetOptions).Any(pair =>
                !string.Equals(pair.First.Name, pair.Second.Name, StringComparison.Ordinal)
                || !string.Equals(pair.First.Color, pair.Second.Color, StringComparison.Ordinal)
                || !string.Equals(pair.First.Description, pair.Second.Description, StringComparison.Ordinal));
    }

    private static TargetIssueField ParseTargetIssueField(JsonElement node)
    {
        var options = node.TryGetProperty("options", out var optionNodes)
            && optionNodes.ValueKind == JsonValueKind.Array
            ? optionNodes.EnumerateArray().Select(option => new SingleSelectOptionSnapshot
            {
                Id = option.GetProperty("id").GetString() ?? string.Empty,
                Name = option.GetProperty("name").GetString() ?? string.Empty,
                Color = option.GetProperty("color").GetString() ?? string.Empty,
                Description = option.TryGetProperty("description", out var description)
                    && description.ValueKind == JsonValueKind.String
                    ? description.GetString()
                    : null,
            }).ToList()
            : null;
        return new TargetIssueField(
            node.GetProperty("id").GetString() ?? throw new GitHubGraphQLException("Issue Field id was null."),
            node.GetProperty("name").GetString() ?? throw new GitHubGraphQLException("Issue Field name was null."),
            node.GetProperty("dataType").GetString() ?? string.Empty,
            node.TryGetProperty("description", out var description)
                && description.ValueKind == JsonValueKind.String
                ? description.GetString()
                : null,
            node.GetProperty("visibility").GetString() ?? string.Empty,
            options);
    }

    private static TargetField ParseIssueFieldLink(JsonElement node)
    {
        var id = node.GetProperty("id").GetString() ?? throw new GitHubGraphQLException("Field id was null.");
        var name = node.GetProperty("name").GetString() ?? throw new GitHubGraphQLException("Field name was null.");
        var typeName = node.TryGetProperty("__typename", out var typeElement)
            ? typeElement.GetString() ?? "ProjectV2Field"
            : "ProjectV2Field";
        return new TargetField(id, name, string.Empty, typeName);
    }

    /// <summary>
    /// Builds the iteration configuration input. All iterations (completed included) are
    /// recreated in chronological order; the API accepts past start dates and reclassifies
    /// them as completed on read (verified by PoC against the real API).
    /// </summary>
    private object BuildIterationConfigurationInput(string fieldName, IterationConfigurationSnapshot configuration)
    {
        // completedIterations are returned newest-first by the API; order everything chronologically.
        var ordered = configuration.CompletedIterations
            .Concat(configuration.Iterations)
            .OrderBy(i => i.StartDate, StringComparer.Ordinal)
            .ToList();

        if (configuration.CompletedIterations.Count > 0)
        {
            OnProgress?.Invoke(string.Create(CultureInfo.InvariantCulture,
                $"Field '{fieldName}': recreating {configuration.CompletedIterations.Count} completed iterations as past-dated iterations."));
        }

        var startDate = ordered.Count > 0
            ? ordered[0].StartDate
            : DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        return new
        {
            duration = configuration.Duration,
            startDate,
            iterations = ordered.Select(i => new { title = i.Title, startDate = i.StartDate, duration = i.Duration }).ToArray(),
        };
    }

    internal static bool ShouldUpdateVisibility(bool currentPublic, bool desiredPublic)
        => currentPublic != desiredPublic;

    private static ProjectRef ParseProjectRef(JsonElement node) => new(
        node.GetProperty("id").GetString() ?? throw new GitHubGraphQLException("Project id was null."),
        node.GetProperty("number").GetInt32(),
        node.GetProperty("url").GetString() ?? string.Empty,
        node.TryGetProperty("public", out var visibility) && visibility.GetBoolean());

    private sealed record ProjectRef(string Id, int Number, string Url, bool Public);

    private sealed record TargetField(string Id, string Name, string DataType, string TypeName);

    private sealed record TargetIssueField(
        string Id,
        string Name,
        string DataType,
        string? Description,
        string Visibility,
        IReadOnlyList<SingleSelectOptionSnapshot>? Options);

    /// <summary>Accumulates fieldName → id, optionName → id and iterationTitle → id mappings.</summary>
    private sealed class FieldMaps
    {
        public Dictionary<string, string> FieldIds { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, IReadOnlyDictionary<string, string>> OptionIds { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, IReadOnlyDictionary<string, string>> IterationIds { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> IssueFieldIds { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, IReadOnlyDictionary<string, string>> IssueFieldOptionIds { get; } = new(StringComparer.Ordinal);

        /// <summary>Registers a field node (from a query or mutation response) and returns its identity.</summary>
        public TargetField Register(JsonElement node)
            => Register(node, null);

        public TargetField Register(JsonElement node, Dictionary<string, string>? dataTypes)
        {
            var id = node.GetProperty("id").GetString() ?? throw new GitHubGraphQLException("Field id was null.");
            var name = node.GetProperty("name").GetString() ?? throw new GitHubGraphQLException("Field name was null.");
            var typeName = node.TryGetProperty("__typename", out var typeElement)
                ? typeElement.GetString() ?? string.Empty
                : string.Empty;
            var dataType = node.TryGetProperty("dataType", out var dataTypeElement)
                ? dataTypeElement.GetString() ?? string.Empty
                : typeName switch
                {
                    "ProjectV2SingleSelectField" => "SINGLE_SELECT",
                    "ProjectV2MultiSelectField" => "MULTI_SELECT",
                    "ProjectV2IterationField" => "ITERATION",
                    _ when dataTypes?.TryGetValue(id, out var value) == true => value,
                    _ => string.Empty,
                };

            FieldIds[name] = id;

            if ((node.TryGetProperty("options", out var options)
                    || node.TryGetProperty("multiSelectOptions", out options))
                && options.ValueKind == JsonValueKind.Array)
            {
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var option in options.EnumerateArray())
                {
                    map[option.GetProperty("name").GetString() ?? string.Empty] = option.GetProperty("id").GetString() ?? string.Empty;
                }

                OptionIds[name] = map;
            }

            if (node.TryGetProperty("configuration", out var configuration) && configuration.ValueKind == JsonValueKind.Object)
            {
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var propertyName in (string[])["iterations", "completedIterations"])
                {
                    foreach (var iteration in configuration.GetProperty(propertyName).EnumerateArray())
                    {
                        map[iteration.GetProperty("title").GetString() ?? string.Empty] = iteration.GetProperty("id").GetString() ?? string.Empty;
                    }
                }

                IterationIds[name] = map;
            }

            return new TargetField(id, name, dataType, typeName);
        }

        public void RegisterIssueField(TargetIssueField field)
        {
            IssueFieldIds[field.Name] = field.Id;
            if (field.Options is not null)
            {
                IssueFieldOptionIds[field.Name] = field.Options.ToDictionary(
                    option => option.Name,
                    option => option.Id,
                    StringComparer.Ordinal);
            }
        }

        public ImportResult ToResult(
            ProjectRef project,
            ProjectImportOutcome outcome,
            IReadOnlyDictionary<int, int> viewNumbers,
            int viewWarningCount) => new()
        {
            ProjectId = project.Id,
            ProjectNumber = project.Number,
            Url = project.Url,
            Outcome = outcome,
            FieldIds = FieldIds,
            OptionIds = OptionIds,
            IterationIds = IterationIds,
            IssueFieldIds = IssueFieldIds,
            IssueFieldOptionIds = IssueFieldOptionIds,
            ViewNumbers = viewNumbers,
            ViewWarningCount = viewWarningCount,
        };
    }

    private const string FindProjectQueryTemplate =
        """
        query($login: String!, $first: Int!, $after: String) {
          __OWNER__(login: $login) {
            projectsV2(first: $first, after: $after) {
              nodes { id number title url public }
              pageInfo { hasNextPage endCursor }
            }
          }
        }
        """;

    private const string FindProjectByNumberQueryTemplate =
        """
        query($login: String!, $number: Int!) {
          __OWNER__(login: $login) {
            projectV2(number: $number) { id number title url public }
          }
        }
        """;

    private const string FieldsQuery =
        """
        query($id: ID!) {
          node(id: $id) {
            ... on ProjectV2 {
              fields(first: 50) {
                nodes {
                  __typename
                  ... on ProjectV2FieldCommon { id name dataType }
                  ... on ProjectV2SingleSelectField { options { id name } }
                  ... on ProjectV2MultiSelectField { multiSelectOptions { id name } }
                  ... on ProjectV2IterationField {
                    configuration {
                      iterations { id title }
                      completedIterations { id title }
                    }
                  }
                }
              }
            }
          }
        }
        """;

    private const string FieldsWithIssueFieldsQuery =
        """
        query($id: ID!) {
          node(id: $id) {
            ... on ProjectV2 {
              fields(first: 50) {
                nodes {
                  __typename
                  ... on ProjectV2FieldCommon { id name }
                }
              }
            }
          }
        }
        """;

    private const string FieldDetailsQuery =
        """
        query($ids: [ID!]!) {
          nodes(ids: $ids) {
            __typename
            ... on ProjectV2FieldCommon { id name }
            ... on ProjectV2SingleSelectField { options { id name } }
            ... on ProjectV2MultiSelectField { multiSelectOptions { id name } }
            ... on ProjectV2IterationField {
              configuration {
                iterations { id title }
                completedIterations { id title }
              }
            }
          }
        }
        """;

    private const string FieldByNameQuery =
        """
        query($id: ID!, $name: String!) {
          node(id: $id) {
            ... on ProjectV2 {
              field(name: $name) {
                __typename
                ... on ProjectV2FieldCommon { id name dataType }
                ... on ProjectV2Field { id name }
                ... on ProjectV2SingleSelectField { id name }
                ... on ProjectV2IterationField { id name }
                ... on ProjectV2MultiSelectField {
                  multiSelectOptions { id name }
                }
              }
            }
          }
        }
        """;

    private const string FieldDataTypesQuery =
        """
        query($ids: [ID!]!) {
          nodes(ids: $ids) {
            ... on ProjectV2Field { id dataType }
          }
        }
        """;

    private const string CreateFieldMutation =
        """
        mutation($projectId: ID!, $name: String!, $dataType: ProjectV2CustomFieldType!, $options: [ProjectV2SingleSelectFieldOptionInput!], $multiSelectOptions: [ProjectV2MultiSelectFieldOptionInput!], $iterationConfiguration: ProjectV2IterationFieldConfigurationInput, $clientMutationId: String!) {
          createProjectV2Field(input: { projectId: $projectId, name: $name, dataType: $dataType, singleSelectOptions: $options, multiSelectOptions: $multiSelectOptions, iterationConfiguration: $iterationConfiguration, clientMutationId: $clientMutationId }) {
            projectV2Field {
              ... on ProjectV2FieldCommon { id name dataType }
              ... on ProjectV2SingleSelectField { options { id name } }
              ... on ProjectV2MultiSelectField { multiSelectOptions { id name } }
              ... on ProjectV2IterationField {
                configuration {
                  iterations { id title }
                  completedIterations { id title }
                }
              }
            }
          }
        }
        """;

    private const string IssueFieldsQuery =
        """
        query($login: String!, $first: Int!, $after: String) {
          organization(login: $login) {
            issueFields(first: $first, after: $after, orderBy: { field: NAME, direction: ASC }) {
              nodes {
                __typename
                ... on IssueFieldCommon { name dataType description visibility }
                ... on IssueFieldText { id }
                ... on IssueFieldNumber { id }
                ... on IssueFieldDate { id }
                ... on IssueFieldSingleSelect {
                  id
                  options { id name color description }
                }
                ... on IssueFieldMultiSelect {
                  id
                  options { id name color description }
                }
              }
              pageInfo { hasNextPage endCursor }
            }
          }
        }
        """;

    private const string CreateIssueFieldMutation =
        """
        mutation($ownerId: ID!, $name: String!, $description: String, $dataType: IssueFieldDataType!, $options: [IssueFieldSingleSelectOptionInput!], $visibility: IssueFieldVisibility, $clientMutationId: String!) {
          createIssueField(input: { ownerId: $ownerId, name: $name, description: $description, dataType: $dataType, options: $options, visibility: $visibility, clientMutationId: $clientMutationId }) {
            issueField {
              __typename
              ... on IssueFieldCommon { name dataType description visibility }
              ... on IssueFieldText { id }
              ... on IssueFieldNumber { id }
              ... on IssueFieldDate { id }
              ... on IssueFieldSingleSelect {
                id
                options { id name color description }
              }
              ... on IssueFieldMultiSelect {
                id
                options { id name color description }
              }
            }
          }
        }
        """;

    private const string UpdateIssueFieldMutation =
        """
        mutation($id: ID!, $description: String, $options: [IssueFieldSingleSelectOptionInput!], $visibility: IssueFieldVisibility, $clientMutationId: String!) {
          updateIssueField(input: { id: $id, description: $description, options: $options, visibility: $visibility, clientMutationId: $clientMutationId }) {
            issueField {
              __typename
              ... on IssueFieldCommon { name dataType description visibility }
              ... on IssueFieldText { id }
              ... on IssueFieldNumber { id }
              ... on IssueFieldDate { id }
              ... on IssueFieldSingleSelect {
                id
                options { id name color description }
              }
              ... on IssueFieldMultiSelect {
                id
                options { id name color description }
              }
            }
          }
        }
        """;
}
