using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Ghpmv.Core.GitHub;

namespace Ghpmv.Integration.Tests;

internal sealed class IntegrationApiTrace(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
{
    private static readonly ConditionalWeakTable<GitHubGraphQLClient, IntegrationApiTrace> Traces = new();
    private readonly Queue<string> _recent = new();
    private int _sequence;

    public static GitHubGraphQLClient CreateClient(string token, Uri endpoint, HttpMessageHandler handler)
    {
        var trace = new IntegrationApiTrace(handler);
        var client = new GitHubGraphQLClient(token, endpoint, trace, delayAsync: null)
        {
            OnRetry = message => Console.WriteLine($"Integration API retry: {message}"),
        };
        Traces.Add(client, trace);
        return client;
    }

    public static void WriteRecentRequests(GitHubGraphQLClient client, string stage, Action<string> write)
    {
        if (!Traces.TryGetValue(client, out var trace))
        {
            return;
        }

        lock (trace._recent)
        {
            foreach (var entry in trace._recent)
            {
                write($"Project item API trace ({stage}): {entry}");
            }

            trace._recent.Clear();
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        var startedUtc = DateTimeOffset.UtcNow;
        var started = Stopwatch.GetTimestamp();
        HttpResponseMessage? response = null;
        try
        {
            response = await base.SendAsync(request, cancellationToken);
            return response;
        }
        finally
        {
            var entry = JsonSerializer.Serialize(new
            {
                sequence,
                startedUtc,
                elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                host = request.RequestUri?.Host,
                status = response is null ? (int?)null : (int)response.StatusCode,
                serverDate = Header(response, "Date"),
                rateLimitRemaining = Header(response, "X-RateLimit-Remaining"),
                rateLimitReset = Header(response, "X-RateLimit-Reset"),
                retryAfter = Header(response, "Retry-After"),
            });
            lock (_recent)
            {
                _recent.Enqueue(entry);
                while (_recent.Count > 5)
                {
                    _recent.Dequeue();
                }
            }
        }
    }

    private static string? Header(HttpResponseMessage? response, string name)
    {
        var value = response is not null && response.Headers.TryGetValues(name, out var values)
            ? string.Join(",", values)
            : null;
        return value is { Length: > 128 } ? value[..128] : value;
    }
}
