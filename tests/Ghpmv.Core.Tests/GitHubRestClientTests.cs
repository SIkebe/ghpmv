using System.Net;
using System.Text;
using Ghpmv.Core.GitHub;

namespace Ghpmv.Core.Tests;

public sealed class GitHubRestClientTests
{
    [Theory]
    [InlineData("https://api.github.com/graphql", "https://api.github.com/")]
    [InlineData("https://api.tenant.ghe.com/graphql", "https://api.tenant.ghe.com/")]
    [InlineData("https://api.tenant.ghe.com/api/graphql", "https://api.tenant.ghe.com/api/")]
    public void ToRestBaseUri_returns_graphql_endpoint_parent(string graphQlEndpoint, string expected)
    {
        Assert.Equal(new Uri(expected), GitHubRestClient.ToRestBaseUri(new Uri(graphQlEndpoint)));
    }

    [Fact]
    public async Task GetAsync_uses_configured_rest_base_uri()
    {
        using var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });
        using var client = new GitHubRestClient(
            "dummy-token",
            GitHubRestClient.ToRestBaseUri(new Uri("https://api.tenant.ghe.com/graphql")),
            handler);

        await client.GetAsync("repos/octo/repo", TestContext.Current.CancellationToken);

        Assert.Equal(new Uri("https://api.tenant.ghe.com/repos/octo/repo"), handler.RequestUri);
    }

    [Fact]
    public async Task GetAsync_normalizes_configured_rest_base_uri_trailing_slash()
    {
        using var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });
        using var client = new GitHubRestClient("dummy-token", new Uri("https://api.tenant.ghe.com/api"), handler);

        await client.GetAsync("repos/octo/repo", TestContext.Current.CancellationToken);

        Assert.Equal(new Uri("https://api.tenant.ghe.com/api/repos/octo/repo"), handler.RequestUri);
    }

    [Fact]
    public async Task Rest_failures_throw_HttpRequestException_with_status_code()
    {
        using var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("{\"message\":\"forbidden\"}", Encoding.UTF8, "application/json"),
        });
        using var client = new GitHubRestClient("dummy-token", baseUri: null, handler);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.PostAsync("repos/octo/repo/issues", new { title = "Fixture issue" }, TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.Contains("REST error 403", exception.Message, StringComparison.Ordinal);
        Assert.Contains("forbidden", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validation_probe_preserves_error_response_without_creating_a_resource()
    {
        var response = new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
        {
            Content = new StringContent(
                """{"message":"Invalid request. Invalid input: data cannot be null."}""",
                Encoding.UTF8,
                "application/json"),
        };
        response.Headers.TryAddWithoutValidation(
            "X-Accepted-GitHub-Permissions",
            "issue_fields=write");
        using var handler = new StubHandler(response);
        using var client = new GitHubRestClient("dummy-token", baseUri: null, handler);

        var result = await client.PostValidationProbeAsync(
            "orgs/example/issue-fields",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, result.StatusCode);
        Assert.Equal("issue_fields=write", result.AcceptedPermissions);
        Assert.Contains("data cannot be null", result.Body, StringComparison.Ordinal);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal(2, handler.ContentLength);
        Assert.Equal("2026-03-10", handler.ApiVersion);
    }

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        public HttpMethod? Method { get; private set; }

        public long? ContentLength { get; private set; }

        public string? ApiVersion { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Method = request.Method;
            ContentLength = request.Content?.Headers.ContentLength ?? 0;
            ApiVersion = request.Headers.GetValues("X-GitHub-Api-Version").Single();
            return Task.FromResult(response);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                response.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
