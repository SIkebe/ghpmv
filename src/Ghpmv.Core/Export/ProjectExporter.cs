using System.Globalization;
using System.Text.Json;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Core.Export;

/// <summary>
/// Exports an organization project (Projects V2) into a <see cref="ProjectSnapshot"/> (M2).
/// Reads everything the GraphQL API exposes: project metadata, fields
/// (including select options, Issue Field metadata and iteration configuration), views,
/// workflows and all items (archived included) with their field values.
/// UI-only settings (view slice-by/field-sum/roadmap, workflow details) are
/// left null and filled in by the browser module (M6/M7).
/// </summary>
public sealed class ProjectExporter
{
    private const int ItemsPageSize = 50;

    private readonly GitHubGraphQLClient _client;

    public ProjectExporter(GitHubGraphQLClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    /// <summary>Owner type of the source project(s): organization (default) or user.</summary>
    public ProjectOwnerType OwnerType { get; init; } = ProjectOwnerType.Organization;

    /// <summary>
    /// Optional authoritative field provider used instead of the public Projects field
    /// connection. Browser-assisted export supplies the complete UI field catalog here.
    /// The argument is the first Project view number.
    /// </summary>
    public Func<int, CancellationToken, Task<ProjectFieldCatalog>>? CompleteFieldCatalogProviderAsync { get; set; }

    /// <summary>Invoked with a human-readable progress message at each export stage.</summary>
    public Action<string>? OnProgress { get; set; }

    /// <summary>
    /// Optional post-processing hook invoked with the GraphQL snapshot; returns the final
    /// snapshot. Used by the browser module (M6) to fill UI-only view settings without
    /// coupling the GraphQL export path to Playwright.
    /// </summary>
    public Func<ProjectSnapshot, CancellationToken, Task<ProjectSnapshot>>? PostExportAsync { get; set; }

    /// <summary>Exports the project identified by owner login and project number.</summary>
    public async Task<ProjectSnapshot> ExportAsync(string ownerLogin, int projectNumber, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerLogin);

        OnProgress?.Invoke($"Fetching project {ownerLogin}/#{projectNumber.ToString(CultureInfo.InvariantCulture)} metadata (views, workflows)...");
        var data = await _client.QueryAsync(MetadataQuery, new { login = ownerLogin, number = projectNumber }, cancellationToken).ConfigureAwait(false);

        var project = data.GetProperty(OwnerField).GetProperty("projectV2");
        if (project.ValueKind == JsonValueKind.Null)
        {
            throw new GitHubGraphQLException($"Project #{projectNumber.ToString(CultureInfo.InvariantCulture)} was not found in {OwnerDescription} '{ownerLogin}'.");
        }

        var projectInfo = ParseProjectInfo(project);
        var views = ParseViews(project.GetProperty("views"));
        var workflows = ParseWorkflows(project.GetProperty("workflows"));
        var linkedRepositories = ParseLinkedRepositories(project.GetProperty("repositories"));
        OnProgress?.Invoke($"Fetched {views.Count} views and {workflows.Count} workflows. Fetching items...");

        ProjectFieldCatalog? completeFieldCatalog = null;
        if (CompleteFieldCatalogProviderAsync is not null)
        {
            var firstView = views.OrderBy(view => view.Number).FirstOrDefault()
                ?? throw new InvalidOperationException(
                    "The complete browser field catalog requires at least one Project view.");
            completeFieldCatalog = await CompleteFieldCatalogProviderAsync(
                firstView.Number,
                cancellationToken).ConfigureAwait(false);
        }

        var issueFieldNames = new HashSet<string>(StringComparer.Ordinal);
        var issueFieldDatabaseIds = new HashSet<int>();
        var items = await FetchItemsAsync(
            ownerLogin,
            projectNumber,
            issueFieldNames,
            issueFieldDatabaseIds,
            cancellationToken).ConfigureAwait(false);
        var referencedItemFields = items
            .SelectMany(item => item.FieldValues)
            .Select(value => (
                value.FieldName,
                IsIssueField: value.IsIssueField ?? issueFieldNames.Contains(value.FieldName)))
            .ToHashSet();
        var referencedFieldNames = items
            .SelectMany(item => item.FieldValues)
            .Select(value => value.FieldName)
            .Concat(views.SelectMany(view => view.GroupByFields))
            .Concat(views.SelectMany(view => view.VerticalGroupByFields))
            .Concat(views.SelectMany(view => view.SortByFields.Select(sort => sort.Field)))
            .Concat(views.SelectMany(view => view.VisibleFields))
            .ToHashSet(StringComparer.Ordinal);
        List<FieldSnapshot> fields;
        if (completeFieldCatalog is not null)
        {
            fields = await BuildCompleteFieldsAsync(
                ownerLogin,
                completeFieldCatalog,
                issueFieldNames,
                referencedItemFields,
                referencedFieldNames,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            fields = await FetchApiFieldsAsync(
                ownerLogin,
                projectNumber,
                issueFieldNames,
                issueFieldDatabaseIds,
                cancellationToken).ConfigureAwait(false);
        }

        OnProgress?.Invoke(string.Create(
            CultureInfo.InvariantCulture,
            $"Fetched {fields.Count} fields and {items.Count} items."));

        var snapshot = new ProjectSnapshot
        {
            SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
            Project = projectInfo,
            Fields = fields,
            Views = views,
            Workflows = workflows,
            Items = items,
            // Collaborators stay null in the API-only path: the GraphQL API has no
            // read field for project collaborators. The browser post-export hook can
            // populate explicit collaborators from Settings → Manage access.
            Collaborators = null,
            LinkedRepositories = linkedRepositories,
        };

        if (PostExportAsync is not null)
        {
            snapshot = await PostExportAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }

        return snapshot;
    }

    private async Task<List<FieldSnapshot>> FetchApiFieldsAsync(
        string ownerLogin,
        int projectNumber,
        HashSet<string> issueFieldNames,
        HashSet<int> issueFieldDatabaseIds,
        CancellationToken cancellationToken)
    {
        List<JsonElement> fieldNodes;
        try
        {
            fieldNodes = await FetchFieldNodesAsync(ownerLogin, projectNumber, cancellationToken).ConfigureAwait(false);
        }
        catch (GitHubGraphQLException exception)
        {
            throw new GitHubGraphQLException(
                "GitHub's Projects API could not enumerate this project's fields. " +
                "No snapshot was written because field completeness cannot be guaranteed. " +
                "Re-run with --enable-browser-automation to read the complete field catalog from the Projects UI.",
                exception)
            {
                ErrorsJson = exception.ErrorsJson,
                ErrorType = exception.ErrorType,
                StatusCode = exception.StatusCode,
            };
        }

        var issueFields = OwnerType == ProjectOwnerType.Organization
            && issueFieldNames.Count > 0
                ? (await FetchIssueFieldsAsync(ownerLogin, cancellationToken).ConfigureAwait(false))
                    .Where(field => issueFieldNames.Contains(field.Name))
                    .ToList()
                : [];
        return ParseFields(fieldNodes, issueFields, issueFieldDatabaseIds);
    }

    /// <summary>Lists the owner's projects (number, title, closed state) for bulk export.</summary>
    public async Task<IReadOnlyList<ProjectListEntry>> ListProjectsAsync(string ownerLogin, bool includeClosed = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerLogin);

        var entries = new List<ProjectListEntry>();
        await foreach (var node in _client.QueryPaginatedAsync(
            ListProjectsQuery,
            new { login = ownerLogin, first = 50 },
            OwnerField + ".projectsV2",
            cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            var closed = node.GetProperty("closed").GetBoolean();
            if (closed && !includeClosed)
            {
                continue;
            }

            entries.Add(new ProjectListEntry(
                node.GetProperty("number").GetInt32(),
                node.GetProperty("title").GetString() ?? string.Empty,
                closed));
        }

        return entries;
    }

    private string OwnerField => OwnerType == ProjectOwnerType.User ? "user" : "organization";

    private string OwnerDescription => OwnerType == ProjectOwnerType.User ? "user" : "organization";

    private async Task<List<FieldSnapshot>> BuildCompleteFieldsAsync(
        string ownerLogin,
        ProjectFieldCatalog catalog,
        IReadOnlySet<string> observedIssueFieldNames,
        IReadOnlySet<(string FieldName, bool IsIssueField)> referencedItemFields,
        IReadOnlySet<string> referencedFieldNames,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var duplicateField = catalog.Entries
            .GroupBy(entry => (entry.Field.Name, entry.IsIssueField))
            .FirstOrDefault(group => group.Skip(1).Any());
        if (duplicateField is not null)
        {
            throw new GitHubGraphQLException(
                $"The complete field catalog contained duplicate field identity '{duplicateField.Key.Name}' " +
                $"({(duplicateField.Key.IsIssueField ? "linked" : "ordinary")}).");
        }

        var issueFieldNames = catalog.Entries
            .Where(entry => entry.IsIssueField)
            .Select(entry => entry.Field.Name)
            .ToHashSet(StringComparer.Ordinal);
        var catalogIdentities = catalog.Entries
            .Select(entry => (entry.Field.Name, entry.IsIssueField))
            .ToHashSet();
        var missingReferencedItemField = referencedItemFields
            .OrderBy(identity => identity.FieldName, StringComparer.Ordinal)
            .ThenBy(identity => identity.IsIssueField)
            .FirstOrDefault(identity => !catalogIdentities.Contains(identity));
        if (!string.IsNullOrEmpty(missingReferencedItemField.FieldName))
        {
            throw new GitHubGraphQLException(
                $"Project items reference {(missingReferencedItemField.IsIssueField ? "linked" : "ordinary")} " +
                $"field '{missingReferencedItemField.FieldName}', but the complete field catalog did not contain that identity.");
        }

        var catalogNames = catalog.Fields.Select(field => field.Name).ToHashSet(StringComparer.Ordinal);
        var missingReferencedField = referencedFieldNames
            .Order(StringComparer.Ordinal)
            .FirstOrDefault(name => !catalogNames.Contains(name));
        if (missingReferencedField is not null)
        {
            throw new GitHubGraphQLException(
                $"Project items or views reference field '{missingReferencedField}', " +
                "but the complete field catalog did not contain it.");
        }

        var unlinkedObservedIssueField = observedIssueFieldNames
            .FirstOrDefault(name => !issueFieldNames.Contains(name));
        if (unlinkedObservedIssueField is not null)
        {
            throw new GitHubGraphQLException(
                $"Project item data identified linked Issue Field '{unlinkedObservedIssueField}', " +
                "but the complete field catalog did not mark it as linked.");
        }

        if (issueFieldNames.Count == 0)
        {
            return [.. catalog.Fields];
        }

        if (OwnerType != ProjectOwnerType.Organization)
        {
            throw new GitHubGraphQLException(
                "A user-owned Project field catalog unexpectedly contained organization Issue Fields.");
        }

        var issueFields = (await FetchIssueFieldsAsync(ownerLogin, cancellationToken).ConfigureAwait(false))
            .Where(field => issueFieldNames.Contains(field.Name))
            .ToList();
        var duplicateIssueField = issueFields
            .GroupBy(field => field.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Skip(1).Any());
        if (duplicateIssueField is not null)
        {
            throw new GitHubGraphQLException(
                $"Organization Issue Field name '{duplicateIssueField.Key}' is ambiguous. Rename or remove duplicate Issue Fields before exporting.");
        }

        var issueFieldsByName = issueFields.ToDictionary(field => field.Name, StringComparer.Ordinal);
        foreach (var issueFieldName in issueFieldNames)
        {
            if (!issueFieldsByName.ContainsKey(issueFieldName))
            {
                throw new GitHubGraphQLException(
                    $"The Projects UI identified linked Issue Field '{issueFieldName}', but it was not present in the organization Issue Field catalog.");
            }
        }

        var mismatchedIssueField = catalog.Entries.FirstOrDefault(entry =>
            entry.IsIssueField
            && issueFieldsByName.TryGetValue(entry.Field.Name, out var issueField)
            && !string.Equals(entry.Field.DataType, issueField.DataType, StringComparison.Ordinal));
        if (mismatchedIssueField is not null)
        {
            throw new GitHubGraphQLException(
                $"The Projects UI identified linked Issue Field '{mismatchedIssueField.Field.Name}' as " +
                $"{mismatchedIssueField.Field.DataType}, but the organization Issue Field catalog reported " +
                $"{issueFieldsByName[mismatchedIssueField.Field.Name].DataType}.");
        }

        return
        [
            .. catalog.Entries.Select(entry =>
                entry.IsIssueField && issueFieldsByName.TryGetValue(entry.Field.Name, out var issueField)
                    ? BuildIssueFieldSnapshot(issueField)
                    : entry.Field),
        ];
    }

    private async Task<List<ItemSnapshot>> FetchItemsAsync(
        string ownerLogin,
        int projectNumber,
        HashSet<string> issueFieldNames,
        HashSet<int> issueFieldDatabaseIds,
        CancellationToken cancellationToken)
    {
        var items = new List<ItemSnapshot>();
        await foreach (var node in _client.QueryPaginatedAsync(
            ItemsQuery,
            new { login = ownerLogin, number = projectNumber, first = ItemsPageSize },
            OwnerField + ".projectV2.items",
            cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            items.Add(ParseItem(
                node,
                position: items.Count,
                issueFieldNames,
                issueFieldDatabaseIds));
        }

        return items;
    }

    private async Task<List<IssueFieldDefinition>> FetchIssueFieldsAsync(string ownerLogin, CancellationToken cancellationToken)
    {
        var fields = new List<IssueFieldDefinition>();
        await foreach (var node in _client.QueryPaginatedAsync(
            IssueFieldsQuery,
            new { login = ownerLogin, first = 100 },
            "organization.issueFields",
            cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            fields.Add(ParseIssueField(node));
        }

        return fields;
    }

    private async Task<List<JsonElement>> FetchFieldNodesAsync(
        string ownerLogin,
        int projectNumber,
        CancellationToken cancellationToken)
    {
        var data = await _client.QueryAsync(
            FieldsQuery,
            new { login = ownerLogin, number = projectNumber },
            cancellationToken).ConfigureAwait(false);
        return [.. data.GetProperty(OwnerField).GetProperty("projectV2").GetProperty("fields").GetProperty("nodes").EnumerateArray()];
    }

    private static ProjectInfoSnapshot ParseProjectInfo(JsonElement project) => new()
    {
        Title = project.GetProperty("title").GetString() ?? string.Empty,
        ShortDescription = GetOptionalString(project, "shortDescription"),
        Readme = GetOptionalString(project, "readme"),
        Public = project.GetProperty("public").GetBoolean(),
        Closed = project.GetProperty("closed").GetBoolean(),
    };

    private static List<FieldSnapshot> ParseFields(
        IReadOnlyList<JsonElement> fieldNodes,
        IReadOnlyList<IssueFieldDefinition> issueFields,
        HashSet<int> issueFieldDatabaseIds)
    {
        var duplicateIssueField = issueFields
            .GroupBy(field => field.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Skip(1).Any());
        if (duplicateIssueField is not null)
        {
            throw new GitHubGraphQLException(
                $"Organization Issue Field name '{duplicateIssueField.Key}' is ambiguous. Rename or remove duplicate Issue Fields before exporting.");
        }

        var issueFieldsByName = issueFields.ToDictionary(field => field.Name, StringComparer.Ordinal);
        var capturedIssueFieldDatabaseIds = new HashSet<int>();
        var fields = new List<FieldSnapshot>();
        foreach (var node in fieldNodes)
        {
            var name = node.GetProperty("name").GetString() ?? string.Empty;
            var databaseId = node.TryGetProperty("databaseId", out var databaseIdElement)
                && databaseIdElement.ValueKind == JsonValueKind.Number
                    ? databaseIdElement.GetInt32()
                    : (int?)null;
            var dataType = node.GetProperty("dataType").GetString() ?? string.Empty;
            IssueFieldDefinition? issueField = null;
            if (databaseId is { } value
                && issueFieldDatabaseIds.Contains(value)
                && issueFieldsByName.TryGetValue(name, out var matchedIssueField))
            {
                if (string.Equals(dataType, matchedIssueField.DataType, StringComparison.Ordinal)
                    && capturedIssueFieldDatabaseIds.Add(value))
                {
                    issueField = matchedIssueField;
                }
            }

            if (issueField is not null)
            {
                fields.Add(BuildIssueFieldSnapshot(issueField));
                continue;
            }

            fields.Add(new FieldSnapshot
            {
                Name = name,
                DataType = dataType,
                Options = TryGetSelectOptions(node),
                IterationConfiguration = node.TryGetProperty("configuration", out var configuration) && configuration.ValueKind == JsonValueKind.Object
                    ? ParseIterationConfiguration(configuration)
                    : null,
            });
        }

        var unresolvedIssueField = issueFieldDatabaseIds
            .Order()
            .Cast<int?>()
            .FirstOrDefault(id => id is { } value && !capturedIssueFieldDatabaseIds.Contains(value));
        if (unresolvedIssueField is { } unresolvedDatabaseId)
        {
            throw new GitHubGraphQLException(
                $"Project item data identified linked Issue Field database ID '{unresolvedDatabaseId}', " +
                "but the organization Issue Field catalog did not contain a matching field.");
        }

        return fields;
    }

    private static List<SingleSelectOptionSnapshot>? TryGetSelectOptions(JsonElement node)
    {
        if (node.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array)
        {
            return ParseSingleSelectOptions(options);
        }

        return node.TryGetProperty("multiSelectOptions", out options) && options.ValueKind == JsonValueKind.Array
            ? ParseSingleSelectOptions(options)
            : null;
    }

    private static FieldSnapshot BuildIssueFieldSnapshot(IssueFieldDefinition issueField) => new()
    {
        Name = issueField.Name,
        DataType = issueField.DataType,
        Options = issueField.Options,
        IssueField = new IssueFieldConfigurationSnapshot
        {
            Description = issueField.Description,
            Visibility = issueField.Visibility,
        },
    };

    private static IssueFieldDefinition ParseIssueField(JsonElement node)
    {
        var options = node.TryGetProperty("options", out var optionNodes)
            && optionNodes.ValueKind == JsonValueKind.Array
            ? ParseSingleSelectOptions(optionNodes)
            : null;
        return new IssueFieldDefinition(
            node.GetProperty("id").GetString() ?? throw new GitHubGraphQLException("Issue Field id was null."),
            node.GetProperty("name").GetString() ?? string.Empty,
            node.GetProperty("dataType").GetString() ?? string.Empty,
            GetOptionalString(node, "description"),
            node.GetProperty("visibility").GetString() ?? string.Empty,
            options);
    }

    private static List<SingleSelectOptionSnapshot> ParseSingleSelectOptions(JsonElement options)
    {
        var result = new List<SingleSelectOptionSnapshot>();
        foreach (var option in options.EnumerateArray())
        {
            result.Add(new SingleSelectOptionSnapshot
            {
                Id = option.GetProperty("id").GetString() ?? string.Empty,
                Name = option.GetProperty("name").GetString() ?? string.Empty,
                Color = option.GetProperty("color").GetString() ?? string.Empty,
                Description = GetOptionalString(option, "description"),
            });
        }

        return result;
    }

    private static IterationConfigurationSnapshot ParseIterationConfiguration(JsonElement configuration) => new()
    {
        Duration = configuration.GetProperty("duration").GetInt32(),
        StartDay = configuration.GetProperty("startDay").GetInt32(),
        Iterations = ParseIterations(configuration.GetProperty("iterations")),
        CompletedIterations = ParseIterations(configuration.GetProperty("completedIterations")),
    };

    private static List<IterationSnapshot> ParseIterations(JsonElement iterations)
    {
        var result = new List<IterationSnapshot>();
        foreach (var iteration in iterations.EnumerateArray())
        {
            result.Add(new IterationSnapshot
            {
                Id = iteration.GetProperty("id").GetString() ?? string.Empty,
                Title = iteration.GetProperty("title").GetString() ?? string.Empty,
                StartDate = iteration.GetProperty("startDate").GetString() ?? string.Empty,
                Duration = iteration.GetProperty("duration").GetInt32(),
            });
        }

        return result;
    }

    private static List<ViewSnapshot> ParseViews(JsonElement connection)
    {
        var views = new List<ViewSnapshot>();
        foreach (var node in connection.GetProperty("nodes").EnumerateArray())
        {
            views.Add(new ViewSnapshot
            {
                Number = node.GetProperty("number").GetInt32(),
                Name = node.GetProperty("name").GetString() ?? string.Empty,
                Layout = node.GetProperty("layout").GetString() ?? string.Empty,
                Filter = GetOptionalString(node, "filter"),
                GroupByFields = ParseFieldNameConnection(node, "groupByFields"),
                SortByFields = ParseSortByFields(node),
                VerticalGroupByFields = ParseFieldNameConnection(node, "verticalGroupByFields"),
                VisibleFields = ParseVisibleFields(node),
            });
        }

        return views;
    }

    private static List<string> ParseVisibleFields(JsonElement view)
    {
        if (view.TryGetProperty("configuration", out var configuration)
            && configuration.ValueKind == JsonValueKind.Object)
        {
            return ParseFieldNameConnection(configuration, "visibleFields");
        }

        // Backward compatibility for snapshots produced from responses captured
        // before ProjectV2View.configuration was added on 2026-07-30.
        return ParseFieldNameConnection(view, "fields");
    }

    private static List<string> ParseFieldNameConnection(JsonElement view, string propertyName)
    {
        var names = new List<string>();
        if (!view.TryGetProperty(propertyName, out var connection) || connection.ValueKind != JsonValueKind.Object)
        {
            return names;
        }

        foreach (var node in connection.GetProperty("nodes").EnumerateArray())
        {
            if (node.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
            {
                names.Add(name.GetString()!);
            }
        }

        return names;
    }

    private static List<SortByFieldSnapshot> ParseSortByFields(JsonElement view)
    {
        var result = new List<SortByFieldSnapshot>();
        if (!view.TryGetProperty("sortByFields", out var connection) || connection.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var node in connection.GetProperty("nodes").EnumerateArray())
        {
            result.Add(new SortByFieldSnapshot
            {
                Field = node.GetProperty("field").GetProperty("name").GetString() ?? string.Empty,
                Direction = node.GetProperty("direction").GetString() ?? string.Empty,
            });
        }

        return result;
    }

    private static List<string> ParseLinkedRepositories(JsonElement connection)
    {
        var repositories = new List<string>();
        foreach (var node in connection.GetProperty("nodes").EnumerateArray())
        {
            if (node.TryGetProperty("nameWithOwner", out var name) && name.ValueKind == JsonValueKind.String)
            {
                repositories.Add(name.GetString()!);
            }
        }

        return repositories;
    }

    private static List<WorkflowSnapshot> ParseWorkflows(JsonElement connection)
    {
        var workflows = new List<WorkflowSnapshot>();
        foreach (var node in connection.GetProperty("nodes").EnumerateArray())
        {
            workflows.Add(new WorkflowSnapshot
            {
                Number = node.GetProperty("number").GetInt32(),
                Name = node.GetProperty("name").GetString() ?? string.Empty,
                Enabled = node.GetProperty("enabled").GetBoolean(),
            });
        }

        return workflows;
    }

    private static ItemSnapshot ParseItem(
        JsonElement node,
        int position,
        HashSet<string> issueFieldNames,
        HashSet<int> issueFieldDatabaseIds)
    {
        var type = node.GetProperty("type").GetString() ?? string.Empty;
        var content = node.GetProperty("content");

        string? repository = null;
        int? number = null;
        DraftIssueSnapshot? draft = null;

        if (content.ValueKind == JsonValueKind.Object)
        {
            if (content.TryGetProperty("repository", out var repositoryElement) && repositoryElement.ValueKind == JsonValueKind.Object)
            {
                repository = repositoryElement.GetProperty("nameWithOwner").GetString();
                number = content.GetProperty("number").GetInt32();
            }
            else if (content.TryGetProperty("title", out var draftTitle))
            {
                draft = new DraftIssueSnapshot
                {
                    Title = draftTitle.GetString() ?? string.Empty,
                    Body = GetOptionalString(content, "body"),
                    Creator = content.TryGetProperty("creator", out var creator) && creator.ValueKind == JsonValueKind.Object
                        ? GetOptionalString(creator, "login")
                        : null,
                    CreatedAt = GetOptionalString(content, "createdAt"),
                    Assignees = ParseAssignees(content),
                };
            }
        }

        return new ItemSnapshot
        {
            Type = type,
            Position = position,
            IsArchived = node.GetProperty("isArchived").GetBoolean(),
            Repository = repository,
            Number = number,
            Draft = draft,
            FieldValues = ParseFieldValues(
                node.GetProperty("fieldValues"),
                issueFieldNames,
                issueFieldDatabaseIds),
        };
    }

    private static List<string> ParseAssignees(JsonElement content)
    {
        var assignees = new List<string>();
        if (!content.TryGetProperty("assignees", out var connection) || connection.ValueKind != JsonValueKind.Object)
        {
            return assignees;
        }

        foreach (var node in connection.GetProperty("nodes").EnumerateArray())
        {
            if (node.TryGetProperty("login", out var login) && login.ValueKind == JsonValueKind.String)
            {
                assignees.Add(login.GetString()!);
            }
        }

        return assignees;
    }

    private static List<FieldValueSnapshot> ParseFieldValues(
        JsonElement connection,
        HashSet<string> issueFieldNames,
        HashSet<int> issueFieldDatabaseIds)
    {
        var values = new List<FieldValueSnapshot>();
        foreach (var node in connection.GetProperty("nodes").EnumerateArray())
        {
            var typeName = node.GetProperty("__typename").GetString();
            if (typeName is not ("ProjectV2ItemFieldTextValue"
                or "ProjectV2ItemFieldNumberValue"
                or "ProjectV2ItemFieldDateValue"
                or "ProjectV2ItemFieldSingleSelectValue"
                or "ProjectV2ItemFieldIterationValue"
                or "ProjectV2ItemIssueFieldValue"))
            {
                continue;
            }

            var fieldName = node.GetProperty("field").GetProperty("name").GetString() ?? string.Empty;
            if (typeName == "ProjectV2ItemIssueFieldValue")
            {
                issueFieldNames.Add(fieldName);
                var field = node.GetProperty("field");
                if (!field.TryGetProperty("databaseId", out var databaseId)
                    || databaseId.ValueKind != JsonValueKind.Number)
                {
                    throw new GitHubGraphQLException(
                        $"Linked Issue Field '{fieldName}' did not expose a database ID.");
                }

                issueFieldDatabaseIds.Add(databaseId.GetInt32());
                if (node.GetProperty("issueFieldValue") is not { ValueKind: JsonValueKind.Object } issueFieldValue)
                {
                    continue;
                }

                var issueValueType = issueFieldValue.GetProperty("__typename").GetString();
                var issueValue = issueValueType switch
                {
                    "IssueFieldTextValue" => new FieldValueSnapshot
                    {
                        FieldName = fieldName,
                        Text = GetOptionalString(issueFieldValue, "value"),
                    },
                    "IssueFieldNumberValue" => new FieldValueSnapshot
                    {
                        FieldName = fieldName,
                        Number = issueFieldValue.GetProperty("value").ValueKind == JsonValueKind.Number
                            ? issueFieldValue.GetProperty("value").GetDouble()
                            : null,
                    },
                    "IssueFieldDateValue" => new FieldValueSnapshot
                    {
                        FieldName = fieldName,
                        Date = GetOptionalString(issueFieldValue, "value"),
                    },
                    "IssueFieldSingleSelectValue" => new FieldValueSnapshot
                    {
                        FieldName = fieldName,
                        SingleSelectOptionName = GetOptionalString(issueFieldValue, "name"),
                    },
                    "IssueFieldMultiSelectValue" => new FieldValueSnapshot
                    {
                        FieldName = fieldName,
                        MultiSelectOptionNames =
                        [
                            .. issueFieldValue.GetProperty("options").EnumerateArray()
                                .Select(option => option.GetProperty("name").GetString() ?? string.Empty),
                        ],
                    },
                    _ => null,
                };
                if (issueValue is not null)
                {
                    values.Add(issueValue with { IsIssueField = true });
                }

                continue;
            }

            values.Add(typeName switch
            {
                "ProjectV2ItemFieldTextValue" => new FieldValueSnapshot
                {
                    FieldName = fieldName,
                    IsIssueField = false,
                    Text = GetOptionalString(node, "text"),
                },
                "ProjectV2ItemFieldNumberValue" => new FieldValueSnapshot
                {
                    FieldName = fieldName,
                    IsIssueField = false,
                    Number = node.GetProperty("number").ValueKind == JsonValueKind.Number ? node.GetProperty("number").GetDouble() : null,
                },
                "ProjectV2ItemFieldDateValue" => new FieldValueSnapshot
                {
                    FieldName = fieldName,
                    IsIssueField = false,
                    Date = GetOptionalString(node, "date"),
                },
                "ProjectV2ItemFieldSingleSelectValue" => new FieldValueSnapshot
                {
                    FieldName = fieldName,
                    IsIssueField = false,
                    SingleSelectOptionName = GetOptionalString(node, "name"),
                },
                _ => new FieldValueSnapshot
                {
                    FieldName = fieldName,
                    IsIssueField = false,
                    IterationTitle = GetOptionalString(node, "title"),
                },
            });
        }

        return values;
    }

    private sealed record IssueFieldDefinition(
        string Id,
        string Name,
        string DataType,
        string? Description,
        string Visibility,
        IReadOnlyList<SingleSelectOptionSnapshot>? Options);

    private static string? GetOptionalString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private string MetadataQuery => MetadataQueryTemplate.Replace("__OWNER__", OwnerField, StringComparison.Ordinal);

    private string ItemsQuery => ItemsQueryTemplate.Replace("__OWNER__", OwnerField, StringComparison.Ordinal);

    private string FieldsQuery => FieldsQueryTemplate.Replace("__OWNER__", OwnerField, StringComparison.Ordinal);

    private string ListProjectsQuery => ListProjectsQueryTemplate.Replace("__OWNER__", OwnerField, StringComparison.Ordinal);

    private const string ListProjectsQueryTemplate =
        """
        query($login: String!, $first: Int!, $after: String) {
          __OWNER__(login: $login) {
            projectsV2(first: $first, after: $after) {
              nodes { number title closed }
              pageInfo { hasNextPage endCursor }
            }
          }
        }
        """;

    private const string MetadataQueryTemplate =
        """
        query($login: String!, $number: Int!) {
          __OWNER__(login: $login) {
            projectV2(number: $number) {
              title
              shortDescription
              readme
              public
              closed
              views(first: 50) {
                nodes {
                  number
                  name
                  layout
                  filter
                  groupByFields(first: 10) { nodes { ... on ProjectV2FieldCommon { name } } }
                  verticalGroupByFields(first: 10) { nodes { ... on ProjectV2FieldCommon { name } } }
                  sortByFields(first: 10) { nodes { direction field { ... on ProjectV2FieldCommon { name } } } }
                  configuration {
                    visibleFields(first: 50) { nodes { ... on ProjectV2FieldCommon { name } } }
                  }
                }
              }
              workflows(first: 50) {
                nodes { number name enabled }
              }
              repositories(first: 100) {
                nodes { nameWithOwner }
              }
            }
          }
        }
        """;

    private const string FieldsQueryTemplate =
        """
        query($login: String!, $number: Int!) {
          __OWNER__(login: $login) {
            projectV2(number: $number) {
              fields(first: 50) {
                nodes {
                  __typename
                  ... on ProjectV2FieldCommon { id databaseId name dataType }
                  ... on ProjectV2SingleSelectField {
                    options { id name color description }
                  }
                  ... on ProjectV2MultiSelectField {
                    multiSelectOptions { id name color description }
                  }
                  ... on ProjectV2IterationField {
                    configuration {
                      duration
                      startDay
                      iterations { id title startDate duration }
                      completedIterations { id title startDate duration }
                    }
                  }
                }
              }
            }
          }
        }
        """;

    private const string ItemsQueryTemplate =
        """
        query($login: String!, $number: Int!, $first: Int!, $after: String) {
          __OWNER__(login: $login) {
            projectV2(number: $number) {
              items(first: $first, after: $after, archivedStates: [ARCHIVED, NOT_ARCHIVED]) {
                nodes {
                  type
                  isArchived
                  content {
                    ... on Issue { number repository { nameWithOwner } }
                    ... on PullRequest { number repository { nameWithOwner } }
                    ... on DraftIssue { title body createdAt creator { login } assignees(first: 20) { nodes { login } } }
                  }
                  fieldValues(first: 50) {
                    nodes {
                      __typename
                      ... on ProjectV2ItemFieldTextValue { text field { ... on ProjectV2FieldCommon { name } } }
                      ... on ProjectV2ItemFieldNumberValue { number field { ... on ProjectV2FieldCommon { name } } }
                      ... on ProjectV2ItemFieldDateValue { date field { ... on ProjectV2FieldCommon { name } } }
                      ... on ProjectV2ItemFieldSingleSelectValue { name field { ... on ProjectV2FieldCommon { name } } }
                      ... on ProjectV2ItemFieldIterationValue { title field { ... on ProjectV2FieldCommon { name } } }
                      ... on ProjectV2ItemIssueFieldValue {
                        field { ... on ProjectV2FieldCommon { id databaseId name } }
                        issueFieldValue {
                          __typename
                          ... on IssueFieldTextValue { value }
                          ... on IssueFieldNumberValue { value }
                          ... on IssueFieldDateValue { value }
                          ... on IssueFieldSingleSelectValue { name }
                          ... on IssueFieldMultiSelectValue { options { name } }
                        }
                      }
                    }
                  }
                }
                pageInfo { hasNextPage endCursor }
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
}

/// <summary>A project listed by <see cref="ProjectExporter.ListProjectsAsync"/>.</summary>
public sealed record ProjectListEntry(int Number, string Title, bool Closed);
