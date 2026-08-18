using System.CommandLine;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Ghpmv.Core;
using Ghpmv.Core.Browser;
using Ghpmv.Core.Export;
using Ghpmv.Core.Fixtures;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Import;
using Ghpmv.Core.Snapshot;
using Ghpmv.Core.Verify;
using Microsoft.Playwright;

var rootCommand = new RootCommand("ghpmv — GitHub Projects Migrator (org-to-org, including Views and Workflows).");

// export
var orgOption = new Option<string>("--org")
{
    Description = "Source organization login (or user login with --owner-type user).",
    Required = true,
};
var projectOption = new Option<int?>("--project")
{
    Description = "Project number in the source organization. When omitted, all projects of the owner are exported to <out>/<number>/ (closed projects excluded unless --include-closed).",
};
var ownerTypeOption = new Option<string>("--owner-type")
{
    Description = "Owner type of the project(s): organization or user.",
    DefaultValueFactory = _ => "organization",
};
ownerTypeOption.Validators.Add(result =>
{
    var value = result.GetValueOrDefault<string>();
    if (!string.Equals(value, "organization", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(value, "user", StringComparison.OrdinalIgnoreCase))
    {
        result.AddError("--owner-type must be one of: organization, user.");
    }
});
var includeClosedOption = new Option<bool>("--include-closed")
{
    Description = "Also export closed projects when --project is omitted.",
};
var apiBaseUrlOption = new Option<string?>("--base-url")
{
    Description = "GraphQL API base URL of the source, e.g. https://api.TENANT.ghe.com or https://api.TENANT.ghe.com/graphql (GHEC with data residency; untested). Defaults to https://api.github.com/graphql.",
};
apiBaseUrlOption.Validators.Add(ValidateBaseUrl);
var targetBaseUrlOption = new Option<string?>("--target-base-url")
{
    Description = "GraphQL API base URL of the target, e.g. https://api.TENANT.ghe.com or https://api.TENANT.ghe.com/graphql (GHEC with data residency; untested). Defaults to https://api.github.com/graphql.",
};
targetBaseUrlOption.Validators.Add(ValidateBaseUrl);
var outOption = new Option<string>("--out")
{
    Description = "Output directory for the snapshot.",
    DefaultValueFactory = _ => "./ghpmv-export",
};
var tokenOption = new Option<string?>("--token")
{
    Description = "GitHub token. Defaults to the GITHUB_TOKEN, then GHPMV_TOKEN environment variable.",
};
var enableBrowserOption = new Option<bool>("--enable-browser-automation")
{
    Description = "Also read or write UI-only project settings with browser automation (requires 'ghpmv setup --browsers' and 'ghpmv login').",
};
var browserProfileOption = new Option<string?>("--browser-profile")
{
    Description = "Named browser profile from 'ghpmv login --profile <name>'. Use different profiles for source and target when they use different accounts (e.g. non-EMU source, EMU target).",
};
var browserBaseUrlOption = new Option<string?>("--browser-base-url")
{
    Description = "GitHub web base URL for browser automation. Derived from the API URL when omitted, e.g. https://TENANT.ghe.com for https://api.TENANT.ghe.com.",
};
browserBaseUrlOption.Validators.Add(ValidateBrowserBaseUrl);
var noUpdateCheckOption = new Option<bool>("--no-update-check")
{
    Description = "Skip the update check against GitHub Releases (also disabled by the GHPMV_NO_UPDATE_CHECK environment variable).",
};

var exportCommand = new Command("export", "Export one project (or all projects of an owner) from the source to JSON snapshots.")
{
    orgOption,
    projectOption,
    ownerTypeOption,
    includeClosedOption,
    outOption,
    tokenOption,
    apiBaseUrlOption,
    enableBrowserOption,
    browserProfileOption,
    browserBaseUrlOption,
    noUpdateCheckOption,
};

exportCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var org = parseResult.GetValue(orgOption)!;
    var projectNumber = parseResult.GetValue(projectOption);
    var ownerType = ParseOwnerType(parseResult.GetValue(ownerTypeOption)!);
    var includeClosed = parseResult.GetValue(includeClosedOption);
    var outDirectory = parseResult.GetValue(outOption)!;
    var baseUrl = parseResult.GetValue(apiBaseUrlOption);
    var updateCheck = StartUpdateCheck(parseResult.GetValue(noUpdateCheckOption));
    var enableBrowserAutomation = parseResult.GetValue(enableBrowserOption);
    var token = parseResult.GetValue(tokenOption)
        ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN")
        ?? Environment.GetEnvironmentVariable("GHPMV_TOKEN");

    if (string.IsNullOrWhiteSpace(token))
    {
        Console.Error.WriteLine("error: no token provided. Use --token or set GITHUB_TOKEN / GHPMV_TOKEN.");
        return 1;
    }

    var graphQlBaseUrl = baseUrl is null ? null : GitHubGraphQLClient.NormalizeBaseUrl(baseUrl);
    using var client = new GitHubGraphQLClient(token, graphQlBaseUrl);
    client.OnRetry = Console.Error.WriteLine;
    var exporter = new ProjectExporter(client) { OnProgress = Console.Error.WriteLine, OwnerType = ownerType };

    BrowserSession? session = null;
    try
    {
        ViewUiExporter? uiExporter = null;
        WorkflowUiExporter? workflowExporter = null;
        CollaboratorUiExporter? collaboratorExporter = null;
        if (enableBrowserAutomation)
        {
            session = new BrowserSession(new BrowserSessionOptions
            {
                BaseUrl = BrowserBaseUrl.Resolve(graphQlBaseUrl, parseResult.GetValue(browserBaseUrlOption)),
                Profile = parseResult.GetValue(browserProfileOption),
            });
            var apiLogin = await client.GetViewerLoginAsync(cancellationToken);
            await session.ValidateAuthenticationAsync(apiLogin, cancellationToken);
            uiExporter = new ViewUiExporter(session) { OnProgress = Console.Error.WriteLine };
            workflowExporter = new WorkflowUiExporter(session) { OnProgress = Console.Error.WriteLine };
            collaboratorExporter = new CollaboratorUiExporter(session) { OnProgress = Console.Error.WriteLine };
        }

        // Installs the browser enrichment hook for one project number.
        void SetBrowserHook(int number)
        {
            if (uiExporter is null || workflowExporter is null || collaboratorExporter is null)
            {
                return;
            }

            exporter.PostExportAsync = async (snapshot, ct) =>
            {
                snapshot = await uiExporter.EnrichAsync(snapshot, org, ownerType, number, ct);
                snapshot = await workflowExporter.EnrichAsync(snapshot, org, ownerType, number, ct);
                return await collaboratorExporter.EnrichAsync(snapshot, org, ownerType, number, ct);
            };
        }

        var snapshots = new List<ProjectSnapshot>();
        if (projectNumber is { } singleNumber)
        {
            SetBrowserHook(singleNumber);
            var snapshot = await exporter.ExportAsync(org, singleNumber, cancellationToken);
            var path = await SnapshotFile.SaveAsync(snapshot, outDirectory, cancellationToken);
            Console.Error.WriteLine($"Snapshot written to {path}");
            snapshots.Add(snapshot);
        }
        else
        {
            var entries = await exporter.ListProjectsAsync(org, includeClosed, cancellationToken);
            if (entries.Count == 0)
            {
                Console.Error.WriteLine($"No projects found for {parseResult.GetValue(ownerTypeOption)} '{org}'.");
                await NotifyUpdateAsync(updateCheck);
                return 0;
            }

            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"({i + 1}/{entries.Count}) Exporting project #{entry.Number} '{entry.Title}'..."));
                SetBrowserHook(entry.Number);
                var snapshot = await exporter.ExportAsync(org, entry.Number, cancellationToken);
                var directory = Path.Combine(outDirectory, entry.Number.ToString(CultureInfo.InvariantCulture));
                var path = await SnapshotFile.SaveAsync(snapshot, directory, cancellationToken);
                Console.Error.WriteLine($"Snapshot written to {path}");
                snapshots.Add(snapshot);
            }
        }

        foreach (var warning in (uiExporter?.Warnings ?? [])
            .Concat(workflowExporter?.Warnings ?? [])
            .Concat(collaboratorExporter?.Warnings ?? []))
        {
            Console.Error.WriteLine($"warning: {warning}");
        }

        await MappingTemplates.WriteAsync(snapshots, outDirectory, Console.Error.WriteLine, cancellationToken);
        await NotifyUpdateAsync(updateCheck);
        return 0;
    }
    catch (Exception exception) when (exception is GitHubGraphQLException or InvalidOperationException or IOException or PlaywrightException or TimeoutException or ArgumentException or FormatException or JsonException or KeyNotFoundException)
    {
        Console.Error.WriteLine($"error: {exception.Message}");
        return 1;
    }
    finally
    {
        if (session is not null)
        {
            await session.DisposeAsync();
        }
    }
});

rootCommand.Subcommands.Add(exportCommand);

// import
var importOrgOption = new Option<string>("--org")
{
    Description = "Target organization login (or user login with --owner-type user).",
    Required = true,
};
var inOption = new Option<string>("--in")
{
    Description = "Directory containing the snapshot.",
    DefaultValueFactory = _ => "./ghpmv-export",
};
var projectNumberOption = new Option<int?>("--project-number")
{
    Description = "Import into this existing project number instead of searching by title or creating a new project (mutually exclusive with --on-conflict and --project-title).",
};
var projectTitleOption = new Option<string?>("--project-title")
{
    Description = "Override the snapshot's project title (mutually exclusive with --project-number).",
};
var onConflictOption = new Option<string>("--on-conflict")
{
    Description = "What to do when a project with the same title already exists: skip, update or fail.",
    DefaultValueFactory = _ => "fail",
};
onConflictOption.Validators.Add(result =>
{
    if (!ConflictActions.TryParse(result.GetValueOrDefault<string>(), out _))
    {
        result.AddError("--on-conflict must be one of: skip, update, fail.");
    }
});
var repoMappingOption = new Option<string?>("--repo-mapping")
{
    Description = "CSV file mapping source repositories to target repositories (header: source,target; rows: org/repo,org/repo).",
};
var userMappingOption = new Option<string?>("--user-mapping")
{
    Description = "CSV file mapping source user logins to target logins (header: mannequin-user,mannequin-id,target-user; mannequin-id is ignored).",
};
var organizationMappingOption = new Option<string?>("--org-mapping")
{
    Description = "CSV file mapping source organizations to target organizations (header: source,target). Repository owner mappings are inferred when unambiguous.",
};
var teamMappingOption = new Option<string?>("--team-mapping")
{
    Description = "CSV file mapping linked Teams (header: source,target; rows: organization/slug,organization/slug). Unmapped Teams keep their slug in the target organization.",
};

var importCommand = new Command("import", "Import a JSON snapshot into the target organization or user.")
{
    importOrgOption,
    ownerTypeOption,
    inOption,
    projectNumberOption,
    projectTitleOption,
    onConflictOption,
    repoMappingOption,
    userMappingOption,
    organizationMappingOption,
    teamMappingOption,
    tokenOption,
    targetBaseUrlOption,
    enableBrowserOption,
    browserProfileOption,
    browserBaseUrlOption,
    noUpdateCheckOption,
};

importCommand.Validators.Add(result =>
{
    if (result.GetResult(projectNumberOption) is null)
    {
        return;
    }

    if (result.GetResult(onConflictOption) is { Implicit: false })
    {
        result.AddError("--project-number cannot be combined with --on-conflict (the existing project is always updated).");
    }

    if (result.GetResult(projectTitleOption) is not null)
    {
        result.AddError("--project-number cannot be combined with --project-title (the existing project keeps its title).");
    }
});

importCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var org = parseResult.GetValue(importOrgOption)!;
    var ownerType = ParseOwnerType(parseResult.GetValue(ownerTypeOption)!);
    var inDirectory = parseResult.GetValue(inOption)!;
    var projectNumber = parseResult.GetValue(projectNumberOption);
    var projectTitle = parseResult.GetValue(projectTitleOption);
    var baseUrl = parseResult.GetValue(targetBaseUrlOption);
    var updateCheck = StartUpdateCheck(parseResult.GetValue(noUpdateCheckOption));
    var enableBrowserAutomation = parseResult.GetValue(enableBrowserOption);
    if (!ConflictActions.TryParse(parseResult.GetValue(onConflictOption), out var onConflict))
    {
        Console.Error.WriteLine("error: --on-conflict must be one of: skip, update, fail.");
        return 1;
    }

    var token = parseResult.GetValue(tokenOption)
        ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN")
        ?? Environment.GetEnvironmentVariable("GHPMV_TOKEN");

    if (string.IsNullOrWhiteSpace(token))
    {
        Console.Error.WriteLine("error: no token provided. Use --token or set GITHUB_TOKEN / GHPMV_TOKEN.");
        return 1;
    }

    var graphQlBaseUrl = baseUrl is null ? null : GitHubGraphQLClient.NormalizeBaseUrl(baseUrl);
    using var client = new GitHubGraphQLClient(token, graphQlBaseUrl);
    using var rest = new GitHubRestClient(
        token,
        graphQlBaseUrl is null ? null : GitHubRestClient.ToRestBaseUri(graphQlBaseUrl));
    client.OnRetry = Console.Error.WriteLine;
    BrowserSession? session = null;
    ProjectTemplateWriteSession? templateWriteSession = null;

    try
    {
        var repoMappingPath = parseResult.GetValue(repoMappingOption);
        var userMappingPath = parseResult.GetValue(userMappingOption);
        var repoMapping = repoMappingPath is null
            ? System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty
            : CsvMapping.Load(repoMappingPath);
        var userMapping = userMappingPath is null
            ? System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty
            : CsvMapping.LoadUserMapping(userMappingPath);
        var organizationMappingPath = parseResult.GetValue(organizationMappingOption);
        var organizationMapping = organizationMappingPath is null
            ? System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty
            : CsvMapping.Load(organizationMappingPath);
        var teamMappingPath = parseResult.GetValue(teamMappingOption);
        var teamMapping = teamMappingPath is null
            ? System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty
            : CsvMapping.Load(teamMappingPath);

        var snapshot = await SnapshotFile.LoadAsync(inDirectory, cancellationToken);
        if (projectTitle is not null)
        {
            snapshot = snapshot with { Project = snapshot.Project with { Title = projectTitle } };
        }

        if (ownerType == ProjectOwnerType.User && snapshot.Project.Template is true)
        {
            throw new InvalidOperationException(
                "A user-owned Project cannot be marked as a template. Import this snapshot into an organization-owned Project.");
        }

        var capabilityPlan = ImportCapabilityAnalyzer.Analyze(snapshot, enableBrowserAutomation, ownerType);
        if (ownerType == ProjectOwnerType.User && capabilityPlan.RequiresOrganizationAdministrator)
        {
            throw new InvalidOperationException(
                "Snapshots containing organization Issue Fields cannot be imported into a user-owned Project.");
        }

        async Task ValidateBrowserBeforeWriteAsync(CancellationToken ct)
        {
            var filterTransforms = ProjectFilterTransformer.AnalyzeSnapshot(
                snapshot,
                userMapping,
                repoMapping,
                organizationMapping);
            foreach (var transform in filterTransforms)
            {
                Console.Error.WriteLine(
                    $"Filter preflight {transform.Location}: '{transform.Result.Original}' -> '{transform.Result.Transformed}'");

                foreach (var identifier in transform.Result.Unresolved)
                {
                    Console.Error.WriteLine(
                        $"warning: Filter preflight {transform.Location}: unmapped {identifier.Qualifier} value '{identifier.Value}'");
                }

                foreach (var identifier in transform.Result.Unchanged)
                {
                    Console.Error.WriteLine(
                        $"Filter preflight {transform.Location}: mapping not required for {identifier.Qualifier} value '{identifier.Value}'");
                }

                foreach (var identifier in transform.Result.Unsupported)
                {
                    Console.Error.WriteLine(
                        $"warning: Filter preflight {transform.Location}: unsupported qualifier '{identifier.Qualifier}' was left unchanged");
                }
            }

            var repositoryResolutions = ProjectFilterTransformer.AnalyzeAutoAddRepositories(snapshot, repoMapping);
            foreach (var repository in repositoryResolutions.Where(result =>
                         result.Resolution.Status != RepositoryResolutionStatus.Mapped))
            {
                Console.Error.WriteLine(
                    $"warning: Filter preflight {repository.Location}: {repository.Resolution.Status.ToString().ToLowerInvariant()} Auto-add repository '{repository.Resolution.Source}'");
            }

            if (filterTransforms.Any(transform => transform.Result.Unresolved.Count > 0)
                || repositoryResolutions.Any(result => result.Resolution.Status != RepositoryResolutionStatus.Mapped))
            {
                throw new InvalidOperationException(
                    "Filter mapping preflight failed; fill the generated mapping CSV rows before importing.");
            }

            session = new BrowserSession(new BrowserSessionOptions
            {
                BaseUrl = BrowserBaseUrl.Resolve(graphQlBaseUrl, parseResult.GetValue(browserBaseUrlOption)),
                Profile = parseResult.GetValue(browserProfileOption),
            });
            var apiLogin = await client.GetViewerLoginAsync(ct);
            await session.ValidateAuthenticationAsync(apiLogin, ct);
        }

        var importPreflightCompleted = false;
        async Task ValidateImportBeforeWriteAsync(CancellationToken ct)
        {
            if (importPreflightCompleted)
            {
                return;
            }

            await ImportCapabilityPreflight.ValidateAsync(
                ownerType == ProjectOwnerType.Organization
                    ? capabilityPlan
                    : capabilityPlan with { RequiresOrganizationAdministrator = false },
                org,
                repoMapping,
                rest,
                ct);
            if (enableBrowserAutomation)
            {
                await ValidateBrowserBeforeWriteAsync(ct);
            }

            importPreflightCompleted = true;
        }

        var itemLog = await ImportLog.LoadAsync(inDirectory, cancellationToken);
        var projectLog = await ProjectImportLog.LoadAsync(inDirectory, cancellationToken);
        async Task PersistUnresolvedWarningsAsync(
            int projectWarningCount,
            int itemWarningCount,
            int viewWarningCount,
            int workflowWarningCount)
        {
            if (projectWarningCount == 0
                && itemWarningCount == 0
                && (!enableBrowserAutomation || (viewWarningCount == 0 && workflowWarningCount == 0)))
            {
                return;
            }

            var warningLog = await ProjectImportLog.LoadAsync(inDirectory, CancellationToken.None);
            var previousWarningState = warningLog.HasUnresolvedWarnings;
            warningLog.TryMarkImportCompleted(
                enableBrowserAutomation,
                projectWarningCount,
                itemWarningCount,
                viewWarningCount,
                workflowWarningCount);
            if (warningLog.HasUnresolvedWarnings != previousWarningState)
            {
                await warningLog.SaveAsync(inDirectory, CancellationToken.None);
            }
        }

        ImportLog? templateLog = itemLog;
        async Task PersistTemplateRestorationAsync(bool required, CancellationToken ct)
        {
            var latestLog = await ImportLog.LoadAsync(inDirectory, ct)
                ?? templateLog
                ?? throw new InvalidOperationException(
                    $"{ImportLog.FileName} was unavailable while persisting template restoration state.");
            latestLog.TemplateRestorationRequired = required;
            await latestLog.SaveAsync(inDirectory, ct);
            templateLog = latestLog;
        }

        if (itemLog is { TemplateRestorationRequired: true })
        {
            templateWriteSession = await ProjectTemplateWriteSession.PrepareAsync(
                client,
                itemLog.ProjectId,
                restorationWasPending: true,
                PersistTemplateRestorationAsync,
                Console.Error.WriteLine,
                cancellationToken);
        }

        if (itemLog is not null
            && !string.Equals(
                itemLog.SourceSnapshotFingerprint,
                ImportLog.ComputeSnapshotFingerprint(snapshot),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{ImportLog.FileName} in '{inDirectory}' belongs to a different source snapshot.");
        }

        var hasIncompleteItemWork = itemLog is { PendingDrafts.Count: > 0 }
            || itemLog is { PendingContents.Count: > 0 }
            || itemLog is { HasIncompleteItems: true };
        var hasIncompleteStatusUpdateWork = itemLog is not null
            && snapshot.StatusUpdates is { } capturedStatusUpdates
            && (itemLog.PendingStatusUpdates.Count > 0
                || itemLog.StatusUpdates.Count < capturedStatusUpdates.Count
                || itemLog.TemplateRestorationRequired);
        var hasIncompleteProjectWork = projectLog.PendingProject is not null
            || projectLog.PendingFields.Count > 0
            || projectLog.PendingIssueFields.Count > 0
            || projectLog.PendingIssueFieldLinks.Count > 0
            || projectLog.PendingViews.Count > 0
            || (projectLog.CreatedProjectId is not null && projectLog.ImportCompleted is false);
        var pendingItemProjectId = itemLog?.ProjectId;
        var importer = new ProjectImporter(client)
        {
            OnConflict = hasIncompleteItemWork || hasIncompleteStatusUpdateWork || hasIncompleteProjectWork
                ? ConflictAction.Update
                : onConflict,
            OwnerType = ownerType,
            RepositoryMapping = repoMapping,
            UserMapping = userMapping,
            OrganizationMapping = organizationMapping,
            TeamMapping = teamMapping,
            BrowserViewEnrichmentPlanned = enableBrowserAutomation,
            OnProgress = Console.Error.WriteLine,
            BeforeWriteAsync = ValidateImportBeforeWriteAsync,
            OperationLogDirectory = inDirectory,
            PendingItemProjectId = pendingItemProjectId,
        };

        var result = projectNumber is { } number
            ? await importer.ImportIntoAsync(snapshot, org, number, cancellationToken)
            : await importer.ImportAsync(snapshot, org, cancellationToken);
        await PersistUnresolvedWarningsAsync(
            importer.Warnings.Count,
            itemWarningCount: 0,
            viewWarningCount: result.ViewWarningCount,
            workflowWarningCount: 0);

        if (result.Outcome == ProjectImportOutcome.Skipped)
        {
            Console.Error.WriteLine("Project already exists; skipped without making changes.");
            Console.WriteLine(result.Url);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"result={FormatProjectImportOutcome(result.Outcome)} project={result.ProjectNumber}"));
            await NotifyUpdateAsync(updateCheck);
            return 0;
        }

        var itemImporter = new ItemImporter(client)
        {
            RepositoryMapping = repoMapping,
            UserMapping = userMapping,
            OnProgress = Console.Error.WriteLine,
        };
        var itemResult = await itemImporter.ImportAsync(snapshot, result, inDirectory, cancellationToken);
        await PersistUnresolvedWarningsAsync(
            importer.Warnings.Count,
            itemResult.Warnings.Count,
            viewWarningCount: result.ViewWarningCount,
            workflowWarningCount: 0);
        var statusUpdateResult = new StatusUpdateImportResult
        {
            Created = 0,
            Resumed = 0,
            AlreadyComplete = 0,
        };
        if (snapshot.StatusUpdates is { Count: > 0 })
        {
            templateLog = await ImportLog.LoadAsync(inDirectory, cancellationToken)
                ?? new ImportLog
                {
                    ProjectId = result.ProjectId,
                    SourceSnapshotFingerprint = ImportLog.ComputeSnapshotFingerprint(snapshot),
                };
            if (ProjectTemplateWriteSession.RequiresPreparation(templateWriteSession))
            {
                templateWriteSession = await ProjectTemplateWriteSession.PrepareAsync(
                    client,
                    result.ProjectId,
                    templateLog.TemplateRestorationRequired,
                    PersistTemplateRestorationAsync,
                    Console.Error.WriteLine,
                    cancellationToken);
            }

            var statusUpdateImporter = new StatusUpdateImporter(client)
            {
                OnProgress = Console.Error.WriteLine,
            };
            statusUpdateResult = await statusUpdateImporter.ImportAsync(
                snapshot,
                result,
                inDirectory,
                cancellationToken);
        }

        var viewWarnings = result.ViewWarningCount;
        var workflowWarnings = 0;
        var workflowsImported = 0;
        if (enableBrowserAutomation)
        {
            System.Diagnostics.Debug.Assert(session is not null);
            var viewImporter = new ViewUiImporter(session, client)
            {
                OnProgress = Console.Error.WriteLine,
            };
            await viewImporter.EnrichAsync(
                snapshot,
                org,
                ownerType,
                result.ProjectNumber,
                result.ViewNumbers,
                cancellationToken);
            foreach (var warning in viewImporter.Warnings)
            {
                Console.Error.WriteLine($"warning: {warning}");
            }

            viewWarnings += viewImporter.Warnings.Count;
            await PersistUnresolvedWarningsAsync(
                importer.Warnings.Count,
                itemResult.Warnings.Count,
                viewWarnings,
                workflowWarningCount: 0);

            var workflowImporter = new WorkflowUiImporter(session)
            {
                RepositoryMapping = repoMapping,
                UserMapping = userMapping,
                OrganizationMapping = organizationMapping,
                OnProgress = Console.Error.WriteLine,
            };
            await workflowImporter.ImportAsync(snapshot, org, result.ProjectNumber, cancellationToken);
            foreach (var warning in workflowImporter.Warnings)
            {
                Console.Error.WriteLine($"warning: {warning}");
            }

            workflowWarnings = workflowImporter.Warnings.Count;
            workflowsImported = workflowImporter.ImportedCount;
            await PersistUnresolvedWarningsAsync(
                importer.Warnings.Count,
                itemResult.Warnings.Count,
                viewWarnings,
                workflowWarnings);
        }

        if (templateWriteSession is not null)
        {
            await templateWriteSession.CompleteAsync(snapshot.Project.Template, cancellationToken);
        }
        else if (snapshot.Project.Template is { } desiredTemplate)
        {
            await ProjectTemplateWriteSession.SetFinalStateAsync(
                client,
                result.ProjectId,
                desiredTemplate,
                Console.Error.WriteLine,
                cancellationToken);
        }

        var completedProjectLog = await ProjectImportLog.LoadAsync(inDirectory, cancellationToken);
        var previousWarningState = completedProjectLog.HasUnresolvedWarnings;
        var importMarkedComplete = completedProjectLog.TryMarkImportCompleted(
                enableBrowserAutomation,
                importer.Warnings.Count,
                itemResult.Warnings.Count,
                viewWarnings,
                workflowWarnings);
        if (importMarkedComplete
            || completedProjectLog.HasUnresolvedWarnings != previousWarningState)
        {
            await completedProjectLog.SaveAsync(inDirectory, cancellationToken);
        }

        Console.WriteLine(result.Url);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"result={FormatProjectImportOutcome(result.Outcome)} project={result.ProjectNumber}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"items: created={itemResult.Created} resumed={itemResult.Resumed} already-complete={itemResult.AlreadyComplete} skipped={itemResult.Skipped} warnings={itemResult.Warnings.Count}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"status-updates: created={statusUpdateResult.Created} resumed={statusUpdateResult.Resumed} already-complete={statusUpdateResult.AlreadyComplete}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"views: imported={result.ViewNumbers.Count} warnings={viewWarnings}"));
        if (enableBrowserAutomation)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"workflows: imported={workflowsImported} warnings={workflowWarnings}"));
        }

        await NotifyUpdateAsync(updateCheck);
        return 0;
    }
    catch (Exception exception) when (exception is GitHubGraphQLException or HttpRequestException or InvalidOperationException or IOException or FormatException or PlaywrightException or ArgumentException or System.Text.Json.JsonException)
    {
        Console.Error.WriteLine($"error: {exception.Message}");
        return 1;
    }
    finally
    {
        if (templateWriteSession is { RestorationRequired: true })
        {
            try
            {
                await templateWriteSession.RestoreAsync(CancellationToken.None);
            }
            catch (Exception exception) when (exception is GitHubGraphQLException or HttpRequestException or IOException)
            {
                Console.Error.WriteLine($"error: failed to restore the target project's template state: {exception.Message}");
            }
        }

        if (session is not null)
        {
            await session.DisposeAsync();
        }
    }
});

rootCommand.Subcommands.Add(importCommand);

var requirementsCommand = new Command("requirements", "Inspect a snapshot and print the target capabilities required before import.")
{
    inOption,
    ownerTypeOption,
    enableBrowserOption,
};
requirementsCommand.SetAction(async (parseResult, cancellationToken) =>
{
    try
    {
        var directory = parseResult.GetValue(inOption)!;
        var snapshot = await SnapshotFile.LoadAsync(directory, cancellationToken);
        var plan = ImportCapabilityAnalyzer.Analyze(
            snapshot,
            parseResult.GetValue(enableBrowserOption),
            ParseOwnerType(parseResult.GetValue(ownerTypeOption)!));
        Console.WriteLine($"requires-organization-administrator={plan.RequiresOrganizationAdministrator.ToString().ToLowerInvariant()}");
        Console.WriteLine($"requires-project-administrator={plan.RequiresProjectAdministrator.ToString().ToLowerInvariant()}");
        Console.WriteLine($"requires-members-read={plan.RequiresMembersRead.ToString().ToLowerInvariant()}");
        Console.WriteLine($"requires-team-administrator={plan.RequiresTeamAdministrator.ToString().ToLowerInvariant()}");
        Console.WriteLine($"requires-visibility-management={plan.RequiresVisibilityManagement.ToString().ToLowerInvariant()}");
        foreach (var repository in plan.Repositories)
        {
            Console.WriteLine(
                $"repository={repository.SourceRepository} capabilities={ImportCapabilityPreflight.Format(repository.Capabilities)}");
        }

        return 0;
    }
    catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException)
    {
        Console.Error.WriteLine($"error: {exception.Message}");
        return 1;
    }
});
rootCommand.Subcommands.Add(requirementsCommand);

// verify
var verifyOrgOption = new Option<string>("--org")
{
    Description = "Target organization login (or user login with --owner-type user).",
    Required = true,
};
var verifyProjectOption = new Option<int>("--project")
{
    Description = "Project number in the target organization.",
    Required = true,
};
var failOnWarningOption = new Option<bool>("--fail-on-warning")
{
    Description = "Exit with an error when any verification warning is reported.",
};
var reportJsonOption = new Option<string?>("--report-json")
{
    Description = "Write the complete verification result as JSON to this path.",
};

var verifyCommand = new Command("verify", "Verify a migrated project against the source snapshot and report differences.")
{
    verifyOrgOption,
    verifyProjectOption,
    ownerTypeOption,
    inOption,
    repoMappingOption,
    userMappingOption,
    organizationMappingOption,
    teamMappingOption,
    tokenOption,
    targetBaseUrlOption,
    enableBrowserOption,
    browserProfileOption,
    browserBaseUrlOption,
    failOnWarningOption,
    reportJsonOption,
    noUpdateCheckOption,
};

verifyCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var org = parseResult.GetValue(verifyOrgOption)!;
    var projectNumber = parseResult.GetValue(verifyProjectOption);
    var ownerType = ParseOwnerType(parseResult.GetValue(ownerTypeOption)!);
    var inDirectory = parseResult.GetValue(inOption)!;
    var baseUrl = parseResult.GetValue(targetBaseUrlOption);
    var updateCheck = StartUpdateCheck(parseResult.GetValue(noUpdateCheckOption));
    var enableBrowserAutomation = parseResult.GetValue(enableBrowserOption);
    var failOnWarning = parseResult.GetValue(failOnWarningOption);
    var reportJsonPath = parseResult.GetValue(reportJsonOption);
    var token = parseResult.GetValue(tokenOption)
        ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN")
        ?? Environment.GetEnvironmentVariable("GHPMV_TOKEN");

    if (string.IsNullOrWhiteSpace(token))
    {
        Console.Error.WriteLine("error: no token provided. Use --token or set GITHUB_TOKEN / GHPMV_TOKEN.");
        return 1;
    }

    var graphQlBaseUrl = baseUrl is null ? null : GitHubGraphQLClient.NormalizeBaseUrl(baseUrl);
    using var client = new GitHubGraphQLClient(token, graphQlBaseUrl);
    client.OnRetry = Console.Error.WriteLine;

    try
    {
        await using var session = enableBrowserAutomation
            ? new BrowserSession(new BrowserSessionOptions
            {
                BaseUrl = BrowserBaseUrl.Resolve(graphQlBaseUrl, parseResult.GetValue(browserBaseUrlOption)),
                Profile = parseResult.GetValue(browserProfileOption),
            })
            : null;
        if (session is not null)
        {
            var apiLogin = await client.GetViewerLoginAsync(cancellationToken);
            await session.ValidateAuthenticationAsync(apiLogin, cancellationToken);
        }

        var repoMappingPath = parseResult.GetValue(repoMappingOption);
        var repoMapping = repoMappingPath is null
            ? System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty
            : CsvMapping.Load(repoMappingPath);
        var userMappingPath = parseResult.GetValue(userMappingOption);
        var userMapping = userMappingPath is null
            ? System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty
            : CsvMapping.LoadUserMapping(userMappingPath);
        var organizationMappingPath = parseResult.GetValue(organizationMappingOption);
        var organizationMapping = organizationMappingPath is null
            ? System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty
            : CsvMapping.Load(organizationMappingPath);
        var teamMappingPath = parseResult.GetValue(teamMappingOption);
        var teamMapping = teamMappingPath is null
            ? System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty
            : CsvMapping.Load(teamMappingPath);
        var verifier = new ProjectVerifier(client)
        {
            OnProgress = Console.Error.WriteLine,
            OwnerType = ownerType,
            RepositoryMapping = repoMapping,
            UserMapping = userMapping,
            OrganizationMapping = organizationMapping,
            TeamMapping = teamMapping,
        };
        ViewUiExporter? viewExporter = null;
        WorkflowUiExporter? workflowExporter = null;
        CollaboratorUiExporter? collaboratorExporter = null;
        if (session is not null)
        {
            viewExporter = new ViewUiExporter(session) { OnProgress = Console.Error.WriteLine };
            workflowExporter = new WorkflowUiExporter(session) { OnProgress = Console.Error.WriteLine };
            collaboratorExporter = new CollaboratorUiExporter(session) { OnProgress = Console.Error.WriteLine };
            verifier.PostExportAsync = async (target, ct) =>
            {
                target = await viewExporter.EnrichAsync(target, org, ownerType, projectNumber, ct);
                target = await workflowExporter.EnrichAsync(target, org, ownerType, projectNumber, ct);
                return await collaboratorExporter.EnrichAsync(target, org, ownerType, projectNumber, ct);
            };
        }

        var snapshot = await SnapshotFile.LoadAsync(inDirectory, cancellationToken);
        var report = await verifier.VerifyAsync(snapshot, org, projectNumber, cancellationToken);
        var viewWarnings = viewExporter?.Warnings ?? [];
        var workflowWarnings = workflowExporter?.Warnings ?? [];
        var collaboratorWarnings = collaboratorExporter?.Warnings ?? [];
        report = report
            .WithWarnings("View", viewWarnings)
            .WithWarnings("Workflow", workflowWarnings)
            .WithWarnings("Collaborator", collaboratorWarnings);
        foreach (var warning in viewWarnings.Concat(workflowWarnings).Concat(collaboratorWarnings))
        {
            Console.Error.WriteLine($"warning: {warning}");
        }

        if (reportJsonPath is not null)
        {
            await VerifyReportFile.SaveAsync(report, reportJsonPath, cancellationToken);
        }

        WriteVerifyReport(report);
        await NotifyUpdateAsync(updateCheck);
        return report.ShouldFail(failOnWarning) ? 1 : 0;
    }
    catch (Exception exception) when (exception is GitHubGraphQLException or InvalidOperationException or IOException or FormatException or PlaywrightException or TimeoutException or ArgumentException or JsonException or KeyNotFoundException)
    {
        Console.Error.WriteLine($"error: {exception.Message}");
        return 1;
    }
});

rootCommand.Subcommands.Add(verifyCommand);

// login: interactive browser sign-in, storage state saved for later automation (M6).
var baseUrlOption = new Option<string>("--base-url")
{
    Description = "GitHub web base URL (e.g. https://TENANT.ghe.com for GHEC with data residency).",
    DefaultValueFactory = _ => "https://github.com",
};
baseUrlOption.Validators.Add(ValidateBrowserBaseUrl);
var statePathOption = new Option<string?>("--state-path")
{
    Description = "File to store the browser sign-in state. Defaults to GHPMV_BROWSER_STATE, then %APPDATA%/ghpmv/browser-state.json.",
};
var profileOption = new Option<string?>("--profile")
{
    Description = "Named profile for the sign-in state (e.g. 'source', 'target'). Stored as %APPDATA%/ghpmv/browser-state.<profile>.json. Use with cross-account migrations.",
};
var expectedLoginOption = new Option<string?>("--expected-login")
{
    Description = "Expected GitHub login. Refuses to save the browser state when a different account signs in.",
};

var loginCommand = new Command("login", "Sign in interactively and store browser state for UI automation.")
{
    baseUrlOption,
    statePathOption,
    profileOption,
    expectedLoginOption,
};

loginCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var baseUrl = parseResult.GetValue(baseUrlOption)!;
    var statePath = parseResult.GetValue(statePathOption);
    var expectedLogin = parseResult.GetValue(expectedLoginOption);

    await using var session = new BrowserSession(new BrowserSessionOptions
    {
        Headless = false,
        BaseUrl = BrowserBaseUrl.NormalizeStandalone(baseUrl),
        StatePath = statePath,
        Profile = parseResult.GetValue(profileOption),
        LoadStoredState = false,
    });

    try
    {
        var accountPrompt = string.IsNullOrWhiteSpace(expectedLogin)
            ? "Complete the GitHub sign-in there"
            : $"Sign in as '{expectedLogin}'";
        Console.Error.WriteLine(
            $"Opening a fresh browser session. {accountPrompt} (2FA/SSO/passkey included)...");
        var timeout = TimeSpan.FromMinutes(5);
        var login = string.IsNullOrWhiteSpace(expectedLogin)
            ? await session.LoginAsync(timeout, cancellationToken)
            : await session.LoginAsAsync(timeout, expectedLogin, cancellationToken);
        Console.Error.WriteLine($"Signed in as '{login}'. Browser state saved to {session.StatePath}");
        return 0;
    }
    catch (Exception exception) when (exception is PlaywrightException or InvalidOperationException or System.TimeoutException)
    {
        Console.Error.WriteLine($"error: {exception.Message}");
        return 1;
    }
});

rootCommand.Subcommands.Add(loginCommand);

// setup: install prerequisites (Playwright Chromium).
var browsersOption = new Option<bool>("--browsers")
{
    Description = "Install the Playwright Chromium browser used for UI automation.",
};

var setupCommand = new Command("setup", "Install prerequisites such as Playwright browsers.")
{
    browsersOption,
};

var fixtureUiOption = new Option<bool>("--fixture-ui")
{
    Description = "Create the standard test fixture Views and Workflows on an existing project using browser automation.",
};
var fixtureOption = new Option<bool>("--fixture")
{
    Description = "Create the standard API-backed test fixture repository/project.",
};
var fixtureOrgOption = new Option<string?>("--fixture-org")
{
    Description = "Organization login that owns the fixture used with --fixture or --fixture-ui.",
};
var fixtureProjectOption = new Option<int?>("--fixture-project")
{
    Description = "Project number to configure with the standard fixture Views and Workflows used with --fixture-ui.",
};
var fixtureTitleOption = new Option<string>("--fixture-title")
{
    Description = "Project title used with --fixture.",
    DefaultValueFactory = _ => "gpm-fixture",
};
var fixtureRepoOption = new Option<string>("--fixture-repo")
{
    Description = "Repository short name used by the fixture Auto-add workflows.",
    DefaultValueFactory = _ => "fixture-repo",
};
var fixtureRequireNewOption = new Option<bool>("--fixture-require-new")
{
    Description = "Fail before writing when the fixture project title or repository already exists.",
};
var fixtureAllowExistingEmptyRepoOption = new Option<bool>("--fixture-allow-existing-empty-repo")
{
    Description = "With --fixture-require-new, permit only an explicitly prepared repository with no contents or issues.",
};
var fixtureTeamOption = new Option<string?>("--fixture-team")
{
    Description = "Existing dedicated Team slug to link to the API-backed fixture Project.",
};
var setupBrowserProfileOption = new Option<string?>("--browser-profile")
{
    Description = "Named browser profile from 'ghpmv login --profile <name>' used with --fixture-ui.",
};
var setupApiBaseUrlOption = new Option<string?>("--api-base-url")
{
    Description = "GraphQL API base URL used with --fixture, e.g. https://api.TENANT.ghe.com or https://api.TENANT.ghe.com/graphql. Defaults to https://api.github.com/graphql.",
};
setupApiBaseUrlOption.Validators.Add(ValidateBaseUrl);

setupCommand.Options.Add(fixtureOption);
setupCommand.Options.Add(fixtureUiOption);
setupCommand.Options.Add(fixtureOrgOption);
setupCommand.Options.Add(fixtureProjectOption);
setupCommand.Options.Add(fixtureTitleOption);
setupCommand.Options.Add(fixtureRepoOption);
setupCommand.Options.Add(fixtureRequireNewOption);
setupCommand.Options.Add(fixtureAllowExistingEmptyRepoOption);
setupCommand.Options.Add(fixtureTeamOption);
setupCommand.Options.Add(setupBrowserProfileOption);
setupCommand.Options.Add(baseUrlOption);
setupCommand.Options.Add(browserBaseUrlOption);
setupCommand.Options.Add(tokenOption);
setupCommand.Options.Add(setupApiBaseUrlOption);

setupCommand.Validators.Add(result =>
{
    if (result.GetValue(fixtureRequireNewOption) && !result.GetValue(fixtureOption))
    {
        result.AddError("--fixture-require-new requires --fixture.");
    }

    if (result.GetValue(fixtureAllowExistingEmptyRepoOption) && !result.GetValue(fixtureRequireNewOption))
    {
        result.AddError("--fixture-allow-existing-empty-repo requires --fixture-require-new.");
    }

    if (result.GetValue(fixtureOption) && string.IsNullOrWhiteSpace(result.GetValue(fixtureOrgOption)))
    {
        result.AddError("--fixture requires --fixture-org.");
    }

    if (!result.GetValue(fixtureUiOption))
    {
        return;
    }

    if (result.GetResult(baseUrlOption) is { Implicit: false }
        && result.GetResult(browserBaseUrlOption) is { Implicit: false })
    {
        result.AddError("--fixture-ui accepts either --browser-base-url or the legacy --base-url, not both.");
    }

    if (string.IsNullOrWhiteSpace(result.GetValue(fixtureOrgOption)))
    {
        result.AddError("--fixture-ui requires --fixture-org.");
    }

    if (!result.GetValue(fixtureOption) && result.GetValue(fixtureProjectOption) is null)
    {
        result.AddError("--fixture-ui requires --fixture-project unless it is combined with --fixture.");
    }
});

setupCommand.SetAction(async (parseResult, cancellationToken) =>
{
    if (!parseResult.GetValue(browsersOption) && !parseResult.GetValue(fixtureOption) && !parseResult.GetValue(fixtureUiOption))
    {
        Console.Error.WriteLine("Nothing to install. Use 'ghpmv setup --browsers' to install the Playwright Chromium browser, 'ghpmv setup --fixture' to create the API-backed test fixture, or 'ghpmv setup --fixture-ui' to create test fixture Views/Workflows.");
        return 1;
    }

    if (parseResult.GetValue(browsersOption))
    {
        Console.Error.WriteLine("Installing the Playwright Chromium browser...");
        var exitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
        Console.Error.WriteLine(exitCode == 0
            ? "Chromium installed."
            : string.Create(CultureInfo.InvariantCulture, $"Playwright install failed with exit code {exitCode}."));
        if (exitCode != 0)
        {
            return exitCode;
        }
    }

    BrowserSession? authenticatedFixtureUiSession = null;
    int? createdFixtureProjectNumber = null;
    FixtureProjectSetupResult? fixtureResult = null;
    if (parseResult.GetValue(fixtureOption))
    {
        var token = parseResult.GetValue(tokenOption)
            ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN")
            ?? Environment.GetEnvironmentVariable("GHPMV_TOKEN")
            ?? Environment.GetEnvironmentVariable("GHPMV_TEST_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.Error.WriteLine("error: no token provided. Use --token or set GITHUB_TOKEN / GHPMV_TOKEN / GHPMV_TEST_TOKEN.");
            return 1;
        }

        var baseUrl = parseResult.GetValue(setupApiBaseUrlOption);
        var graphQlBaseUri = baseUrl is null ? null : GitHubGraphQLClient.NormalizeBaseUrl(baseUrl);
        using var graphQl = new GitHubGraphQLClient(token, graphQlBaseUri);
        graphQl.OnRetry = Console.Error.WriteLine;
        using var rest = new GitHubRestClient(token, graphQlBaseUri is null ? null : GitHubRestClient.ToRestBaseUri(graphQlBaseUri));
        var fixturePreflightCompleted = false;
        async Task ValidateFixtureBeforeWriteAsync(CancellationToken ct)
        {
            if (fixturePreflightCompleted)
            {
                return;
            }

            await ImportCapabilityPreflight.ValidateAsync(
                new ImportCapabilityPlan(
                    RequiresOrganizationAdministrator: true,
                    RequiresProjectAdministrator: false,
                    RequiresMembersRead: false,
                    RequiresVisibilityManagement: false,
                    []),
                parseResult.GetValue(fixtureOrgOption)!,
                System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty,
                rest,
                ct);
            if (parseResult.GetValue(fixtureUiOption) && authenticatedFixtureUiSession is null)
            {
                var legacyBrowserBaseUrl = parseResult.GetResult(baseUrlOption) is { Implicit: false }
                    ? parseResult.GetValue(baseUrlOption)
                    : null;
                var browserSession = new BrowserSession(new BrowserSessionOptions
                {
                    BaseUrl = BrowserBaseUrl.Resolve(
                        graphQlBaseUri,
                        parseResult.GetValue(browserBaseUrlOption) ?? legacyBrowserBaseUrl),
                    Profile = parseResult.GetValue(setupBrowserProfileOption),
                });
                try
                {
                    var apiLogin = await graphQl.GetViewerLoginAsync(ct);
                    await browserSession.ValidateAuthenticationAsync(apiLogin, ct);
                    authenticatedFixtureUiSession = browserSession;
                }
                catch
                {
                    await browserSession.DisposeAsync();
                    throw;
                }
            }

            fixturePreflightCompleted = true;
        }

        var fixtureOperationDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ghpmv",
            "fixture-operations");
        var builder = new FixtureProjectBuilder(graphQl, rest)
        {
            OnProgress = Console.Error.WriteLine,
            OperationLogDirectory = fixtureOperationDirectory,
            RequireNewResources = parseResult.GetValue(fixtureRequireNewOption),
            AllowExistingEmptyRepository = parseResult.GetValue(fixtureAllowExistingEmptyRepoOption),
            BeforeWriteAsync = ValidateFixtureBeforeWriteAsync,
        };
        try
        {
            fixtureResult = await builder.CreateAsync(
                parseResult.GetValue(fixtureOrgOption)!,
                parseResult.GetValue(fixtureTitleOption) ?? "gpm-fixture",
                parseResult.GetValue(fixtureRepoOption) ?? "fixture-repo",
                parseResult.GetValue(fixtureTeamOption),
                cancellationToken);
            Console.WriteLine(fixtureResult.Url);
            var fixtureDisposition = fixtureResult.Created
                ? "created"
                : fixtureResult.OwnedByOperation
                    ? "resumed"
                    : "already existed";
            Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Fixture project {fixtureDisposition}: #{fixtureResult.ProjectNumber}"));
            createdFixtureProjectNumber = fixtureResult.ProjectNumber;
        }
        catch (Exception exception) when (exception is PlaywrightException or GitHubGraphQLException or InvalidOperationException or IOException or HttpRequestException or TimeoutException or ArgumentException or FormatException or System.Text.Json.JsonException)
        {
            if (authenticatedFixtureUiSession is not null)
            {
                await authenticatedFixtureUiSession.DisposeAsync();
                authenticatedFixtureUiSession = null;
            }

            Console.Error.WriteLine($"error: {exception.Message}");
            return 1;
        }
    }

    if (!parseResult.GetValue(fixtureUiOption))
    {
        return 0;
    }

    try
    {
        var org = parseResult.GetValue(fixtureOrgOption)!;
        var projectNumber = parseResult.GetValue(fixtureProjectOption) ?? createdFixtureProjectNumber;
        if (projectNumber is null)
        {
            Console.Error.WriteLine("error: --fixture-ui requires --fixture-project unless it is combined with --fixture.");
            return 1;
        }

        var snapshot = FixtureUiSnapshotFactory.Create(parseResult.GetValue(fixtureRepoOption) ?? "fixture-repo");
        var fixtureUiFingerprint = ImportLog.ComputeSnapshotFingerprint(snapshot);

        var apiBaseUrl = parseResult.GetValue(setupApiBaseUrlOption);
        var deployment = apiBaseUrl is null
            ? "api.github.com"
            : GitHubGraphQLClient.NormalizeBaseUrl(apiBaseUrl).Host;
        var fixtureViewOperationDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ghpmv",
            "fixture-view-operations",
            deployment,
            org.ToLowerInvariant(),
            projectNumber.Value.ToString(CultureInfo.InvariantCulture));
        var uiCompletionPath = Path.Combine(fixtureViewOperationDirectory, "fixture-ui-complete");
        using var fixtureUiOperationLock = FixtureUiOperation.AcquireLock(fixtureViewOperationDirectory);
        if (fixtureResult?.ShouldSkipUiSetup(
                projectExplicitlySelected: parseResult.GetValue(fixtureProjectOption) is not null,
                uiSetupCompleted: FixtureUiOperation.IsCompleted(
                    uiCompletionPath,
                    projectNumber.Value,
                    fixtureUiFingerprint)) is true)
        {
            if (authenticatedFixtureUiSession is not null)
            {
                await authenticatedFixtureUiSession.DisposeAsync();
                authenticatedFixtureUiSession = null;
            }

            Console.Error.WriteLine(fixtureResult.OwnedByOperation
                ? "Fixture UI setup already completed; skipping --fixture-ui to avoid duplicating workflows. To force UI setup, run setup --fixture-ui with --fixture-project <number> explicitly."
                : "Fixture project already existed and is not owned by this operation; skipping --fixture-ui to avoid modifying an unrelated Project. To force UI setup, run setup --fixture-ui with --fixture-project <number> explicitly.");
            return 0;
        }

        var token = parseResult.GetValue(tokenOption)
            ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN")
            ?? Environment.GetEnvironmentVariable("GHPMV_TOKEN")
            ?? Environment.GetEnvironmentVariable("GHPMV_TEST_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.Error.WriteLine("error: no token provided. Use --token or set GITHUB_TOKEN / GHPMV_TOKEN / GHPMV_TEST_TOKEN.");
            return 1;
        }

        var legacyBrowserBaseUrl = parseResult.GetResult(baseUrlOption) is { Implicit: false }
            ? parseResult.GetValue(baseUrlOption)
            : null;
        var graphQlBaseUri = apiBaseUrl is null ? null : GitHubGraphQLClient.NormalizeBaseUrl(apiBaseUrl);
        var fixtureUiSession = authenticatedFixtureUiSession
            ?? new BrowserSession(new BrowserSessionOptions
            {
                BaseUrl = BrowserBaseUrl.Resolve(
                    graphQlBaseUri,
                    parseResult.GetValue(browserBaseUrlOption) ?? legacyBrowserBaseUrl),
                Profile = parseResult.GetValue(setupBrowserProfileOption),
            });
        await using var fixtureUiSessionScope = fixtureUiSession;
        using var fixtureUiClient = new GitHubGraphQLClient(token, graphQlBaseUri);
        fixtureUiClient.OnRetry = Console.Error.WriteLine;
        if (authenticatedFixtureUiSession is null)
        {
            var apiLogin = await fixtureUiClient.GetViewerLoginAsync(cancellationToken);
            await fixtureUiSession.ValidateAuthenticationAsync(apiLogin, cancellationToken);
        }

        File.Delete(uiCompletionPath);

        var apiViewImporter = new ProjectImporter(fixtureUiClient)
        {
            OperationLogDirectory = fixtureViewOperationDirectory,
            BrowserViewEnrichmentPlanned = true,
            OnProgress = Console.Error.WriteLine,
        };
        var viewNumbers = await apiViewImporter.ImportViewsIntoAsync(
            snapshot,
            org,
            projectNumber.Value,
            cancellationToken);
        var viewImporter = new ViewUiImporter(fixtureUiSession, fixtureUiClient) { OnProgress = Console.Error.WriteLine };
        await viewImporter.EnrichAsync(
            snapshot,
            org,
            ProjectOwnerType.Organization,
            projectNumber.Value,
            viewNumbers,
            cancellationToken);
        foreach (var warning in viewImporter.Warnings)
        {
            Console.Error.WriteLine($"warning: {warning}");
        }

        var workflowImporter = new WorkflowUiImporter(fixtureUiSession) { OnProgress = Console.Error.WriteLine };
        await workflowImporter.ImportAsync(snapshot, org, projectNumber.Value, cancellationToken);
        foreach (var warning in workflowImporter.Warnings)
        {
            Console.Error.WriteLine($"warning: {warning}");
        }

        Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Fixture UI applied: views={viewNumbers.Count} workflows={workflowImporter.ImportedCount} viewWarnings={apiViewImporter.Warnings.Count + viewImporter.Warnings.Count} workflowWarnings={workflowImporter.Warnings.Count}"));
        var uiSetupSucceeded = apiViewImporter.Warnings.Count == 0
            && viewImporter.Warnings.Count == 0
            && workflowImporter.Warnings.Count == 0;
        if (uiSetupSucceeded)
        {
            await FixtureUiOperation.MarkCompletedAsync(
                uiCompletionPath,
                projectNumber.Value,
                fixtureUiFingerprint,
                CancellationToken.None);
        }

        return uiSetupSucceeded ? 0 : 1;
    }
    catch (Exception exception) when (exception is PlaywrightException or InvalidOperationException or IOException or TimeoutException or GitHubGraphQLException or ArgumentException or FormatException)
    {
        Console.Error.WriteLine($"error: {exception.Message}");
        return 1;
    }
});

rootCommand.Subcommands.Add(setupCommand);

return await rootCommand.Parse(args).InvokeAsync();

// Maps the validated --owner-type value to the core enum.
static ProjectOwnerType ParseOwnerType(string value)
    => string.Equals(value, "user", StringComparison.OrdinalIgnoreCase) ? ProjectOwnerType.User : ProjectOwnerType.Organization;

// Rejects --base-url / --target-base-url values that cannot be normalized to a GraphQL endpoint.
static void ValidateBaseUrl(System.CommandLine.Parsing.OptionResult result)
{
    var value = result.GetValueOrDefault<string?>();
    if (value is null)
    {
        return;
    }

    try
    {
        GitHubGraphQLClient.NormalizeBaseUrl(value);
    }
    catch (Exception exception) when (exception is FormatException or ArgumentException)
    {
        result.AddError($"{result.IdentifierToken?.Value ?? "--base-url"}: {exception.Message}");
    }
}

// Rejects malformed web origins. API/web pairing is validated after both values are parsed.
static void ValidateBrowserBaseUrl(System.CommandLine.Parsing.OptionResult result)
{
    var value = result.GetValueOrDefault<string?>();
    if (value is null)
    {
        return;
    }

    try
    {
        BrowserBaseUrl.NormalizeStandalone(value);
    }
    catch (Exception exception) when (exception is FormatException or ArgumentException)
    {
        result.AddError($"{result.IdentifierToken?.Value ?? "--browser-base-url"}: {exception.Message}");
    }
}

// Starts a fire-and-forget update check against GitHub Releases (opt-out via
// --no-update-check or GHPMV_NO_UPDATE_CHECK). Never throws; sends no telemetry.
static Task<string?> StartUpdateCheck(bool disabled)
{
    if (disabled || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GHPMV_NO_UPDATE_CHECK")))
    {
        return Task.FromResult<string?>(null);
    }

    var current = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
    return UpdateChecker.CheckForNewerVersionAsync(current);
}

// Prints a one-line notice to stderr when the update check found a newer version.
static async Task NotifyUpdateAsync(Task<string?> updateCheck)
{
    var latest = await updateCheck;
    if (latest is not null)
    {
        Console.Error.WriteLine($"note: ghpmv {latest} is available: https://github.com/SIkebe/ghpmv/releases/latest (disable this check with --no-update-check or GHPMV_NO_UPDATE_CHECK=1)");
    }
}

// Writes the verify report to stdout as a human-readable table plus a summary line.
static void WriteVerifyReport(VerifyReport report)
{
    if (report.Status == VerifyStatus.Match && report.Differences.Count == 0)
    {
        Console.WriteLine("OK: the target project matches the snapshot.");
    }

    if (report.Differences.Count > 0)
    {
        const string SeverityHeader = "SEVERITY";
        const string CategoryHeader = "CATEGORY";
        var severityWidth = Math.Max(SeverityHeader.Length, report.Differences.Max(d => d.Severity.ToString().Length));
        var categoryWidth = Math.Max(CategoryHeader.Length, report.Differences.Max(d => d.Category.Length));

        Console.WriteLine($"{SeverityHeader.PadRight(severityWidth)}  {CategoryHeader.PadRight(categoryWidth)}  MESSAGE");
        foreach (var difference in report.Differences)
        {
            Console.WriteLine($"{difference.Severity.ToString().PadRight(severityWidth)}  {difference.Category.PadRight(categoryWidth)}  {difference.Message}");
        }

        Console.WriteLine();
    }

    Console.WriteLine("CATEGORY STATUS");
    foreach (var category in report.Categories)
    {
        Console.WriteLine($"{category.Category}: {category.Status}");
    }

    Console.WriteLine();
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"{report.ErrorCount} error(s), {report.WarningCount} warning(s), {report.InfoCount} info(s), {report.NotVerifiedCount} not verified. {report.Status}."));
}

static string FormatProjectImportOutcome(ProjectImportOutcome outcome) => outcome switch
{
    ProjectImportOutcome.Created => "created",
    ProjectImportOutcome.Updated => "updated",
    ProjectImportOutcome.Skipped => "skipped",
    _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unsupported project import outcome."),
};
