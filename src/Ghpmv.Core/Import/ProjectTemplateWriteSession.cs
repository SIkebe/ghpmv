using System.Text.Json;
using Ghpmv.Core.GitHub;

namespace Ghpmv.Core.Import;

/// <summary>
/// Temporarily removes an existing target's template flag and restores it after all
/// migration writers finish. This is the final-stage orchestration seam shared with #47.
/// </summary>
public sealed class ProjectTemplateWriteSession
{
    private readonly GitHubGraphQLClient _client;
    private readonly string _projectId;
    private readonly Func<bool, CancellationToken, Task>? _persistRestorationStateAsync;
    private bool _restored;

    private ProjectTemplateWriteSession(
        GitHubGraphQLClient client,
        string projectId,
        bool restorationRequired,
        Func<bool, CancellationToken, Task>? persistRestorationStateAsync)
    {
        _client = client;
        _projectId = projectId;
        RestorationRequired = restorationRequired;
        _persistRestorationStateAsync = persistRestorationStateAsync;
    }

    public bool RestorationRequired { get; }

    public Action<string>? OnProgress { get; init; }

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

        var wasTemplate = node.GetProperty("template").GetBoolean();
        var session = new ProjectTemplateWriteSession(
            client,
            projectId,
            restorationWasPending || wasTemplate,
            persistRestorationStateAsync)
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
    }
}
