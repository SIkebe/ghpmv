using System.Text.Json;
using Ghpmv.Core.GitHub;

namespace Ghpmv.Core.Import;

/// <summary>
/// Temporarily removes an existing target's template flag for writers that GitHub blocks
/// on templates, then either applies the snapshot's final state or restores the original.
/// </summary>
public sealed class ProjectTemplateWriteSession
{
    private readonly GitHubGraphQLClient _client;
    private readonly string _projectId;
    private readonly Func<bool, CancellationToken, Task>? _persistRestorationStateAsync;
    private bool _currentTemplate;
    private bool _restored;

    private ProjectTemplateWriteSession(
        GitHubGraphQLClient client,
        string projectId,
        bool currentTemplate,
        bool restorationRequired,
        Func<bool, CancellationToken, Task>? persistRestorationStateAsync)
    {
        _client = client;
        _projectId = projectId;
        _currentTemplate = currentTemplate;
        RestorationRequired = restorationRequired;
        _persistRestorationStateAsync = persistRestorationStateAsync;
    }

    public bool RestorationRequired { get; private set; }

    public Action<string>? OnProgress { get; init; }

    public static bool RequiresPreparation(ProjectTemplateWriteSession? session)
        => session is null || !session.RestorationRequired;

    public static Task<ProjectTemplateWriteSession> PrepareAsync(
        GitHubGraphQLClient client,
        string projectId,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default)
        => PrepareCoreAsync(
            client,
            projectId,
            restorationWasPending: false,
            persistRestorationStateAsync: null,
            onProgress,
            cancellationToken);

    public static Task<ProjectTemplateWriteSession> PrepareAsync(
        GitHubGraphQLClient client,
        string projectId,
        bool restorationWasPending,
        Func<bool, CancellationToken, Task> persistRestorationStateAsync,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default)
        => PrepareCoreAsync(
            client,
            projectId,
            restorationWasPending,
            persistRestorationStateAsync,
            onProgress,
            cancellationToken);

    private static async Task<ProjectTemplateWriteSession> PrepareCoreAsync(
        GitHubGraphQLClient client,
        string projectId,
        bool restorationWasPending,
        Func<bool, CancellationToken, Task>? persistRestorationStateAsync,
        Action<string>? onProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var wasTemplate = await ReadTemplateStateAsync(client, projectId, cancellationToken).ConfigureAwait(false);
        if (restorationWasPending && wasTemplate)
        {
            await persistRestorationStateAsync!(false, cancellationToken).ConfigureAwait(false);
            return new ProjectTemplateWriteSession(
                client,
                projectId,
                currentTemplate: true,
                restorationRequired: false,
                persistRestorationStateAsync: persistRestorationStateAsync)
            {
                OnProgress = onProgress,
            };
        }

        var session = new ProjectTemplateWriteSession(
            client,
            projectId,
            currentTemplate: wasTemplate,
            restorationRequired: restorationWasPending || wasTemplate,
            persistRestorationStateAsync: persistRestorationStateAsync)
        {
            OnProgress = onProgress,
        };
        if (!wasTemplate)
        {
            return session;
        }

        onProgress?.Invoke("Temporarily unmarking the target project as a template before status update writes...");
        if (!restorationWasPending && persistRestorationStateAsync is not null)
        {
            await persistRestorationStateAsync(true, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await session.SetTemplateAsync(mark: false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception unmarkException) when (unmarkException is GitHubGraphQLException or OperationCanceledException)
        {
            try
            {
                // The unmark result may be ambiguous, and the caller cannot receive this
                // session until preparation succeeds. Restore here so no failure path can
                // leave an existing target silently unmarked.
                await session.SetTemplateAsync(mark: true, CancellationToken.None).ConfigureAwait(false);
                if (persistRestorationStateAsync is not null)
                {
                    await persistRestorationStateAsync(false, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception restoreException) when (restoreException is GitHubGraphQLException or OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"Could not confirm or restore template state for target project '{projectId}'. Inspect the target before resuming.",
                    new AggregateException(unmarkException, restoreException));
            }

            throw;
        }

        return session;
    }

    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        if (!RestorationRequired || _restored)
        {
            return;
        }

        OnProgress?.Invoke("Restoring the target project's template state as the final import stage...");
        await SetTemplateAsync(mark: true, cancellationToken).ConfigureAwait(false);
        if (_persistRestorationStateAsync is not null)
        {
            await _persistRestorationStateAsync(false, cancellationToken).ConfigureAwait(false);
        }

        _restored = true;
    }

    /// <summary>
    /// Applies a captured template state as the final successful import stage. A null
    /// snapshot value restores a temporarily unmarked legacy target without otherwise
    /// changing its state.
    /// </summary>
    public async Task CompleteAsync(bool? desiredTemplate, CancellationToken cancellationToken = default)
    {
        if (desiredTemplate is null)
        {
            await RestoreAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_currentTemplate != desiredTemplate.Value)
        {
            OnProgress?.Invoke(desiredTemplate.Value
                ? "Marking the target project as a template as the final import stage..."
                : "Unmarking the target project as a template as the final import stage...");
            await SetTemplateAsync(desiredTemplate.Value, cancellationToken).ConfigureAwait(false);
        }

        if (RestorationRequired && _persistRestorationStateAsync is not null)
        {
            await _persistRestorationStateAsync(false, cancellationToken).ConfigureAwait(false);
        }

        RestorationRequired = false;
        _restored = true;
    }

    /// <summary>Applies a non-null template state when no temporary write session was needed.</summary>
    public static async Task SetFinalStateAsync(
        GitHubGraphQLClient client,
        string projectId,
        bool desiredTemplate,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var currentTemplate = await ReadTemplateStateAsync(client, projectId, cancellationToken).ConfigureAwait(false);
        var session = new ProjectTemplateWriteSession(
            client,
            projectId,
            currentTemplate,
            restorationRequired: false,
            persistRestorationStateAsync: null)
        {
            OnProgress = onProgress,
        };
        await session.CompleteAsync(desiredTemplate, cancellationToken).ConfigureAwait(false);
    }

    private async Task SetTemplateAsync(bool mark, CancellationToken cancellationToken)
    {
        var operationName = mark ? "markProjectV2AsTemplate" : "unmarkProjectV2AsTemplate";
        var mutation = mark
            ? """
              mutation($projectId: ID!, $clientMutationId: String!) {
                markProjectV2AsTemplate(input: { projectId: $projectId, clientMutationId: $clientMutationId }) {
                  projectV2 { id template }
                }
              }
              """
            : """
              mutation($projectId: ID!, $clientMutationId: String!) {
                unmarkProjectV2AsTemplate(input: { projectId: $projectId, clientMutationId: $clientMutationId }) {
                  projectV2 { id template }
                }
              }
              """;
        await _client.MutationAsync(
            operationName,
            mutation,
            new { projectId = _projectId },
            MutationRetryPolicy.Idempotent,
            target: _projectId,
            requiredResultPath: "projectV2.id",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        _currentTemplate = mark;
    }

    private static async Task<bool> ReadTemplateStateAsync(
        GitHubGraphQLClient client,
        string projectId,
        CancellationToken cancellationToken)
    {
        var data = await client.QueryAsync(
            """
            query($projectId: ID!) {
              node(id: $projectId) {
                ... on ProjectV2 { id template }
              }
            }
            """,
            new { projectId },
            cancellationToken).ConfigureAwait(false);
        var node = data.GetProperty("node");
        if (node.ValueKind != JsonValueKind.Object)
        {
            throw new GitHubGraphQLException($"Target project '{projectId}' was not found while checking template state.");
        }

        return node.GetProperty("template").GetBoolean();
    }
}
