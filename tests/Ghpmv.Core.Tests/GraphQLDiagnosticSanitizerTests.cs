using System.Text.Json;
using Ghpmv.Core.GitHub;

namespace Ghpmv.Core.Tests;

public sealed class GraphQLDiagnosticSanitizerTests
{
    [Fact]
    public void Paths_do_not_copy_input_literals_comments_or_arbitrary_server_values()
    {
        const string query = """"
            query {
              # commentSecret
              repository(name: "literalSecret", owner: """blockSecret""") {
                issues { nodes { title } }
              }
            }
            """";
        using var document = JsonDocument.Parse("""
            [{"type":"FORBIDDEN","message":"Resource not accessible: ghp_secret password=secret",
              "path":["repository","literalSecret","blockSecret","commentSecret","ghp_secret",0,{},-1],
              "extensions":{"value":"secret"}}]
            """);

        var error = Assert.Single(GraphQLDiagnosticSanitizer.Errors(document.RootElement, query));
        Assert.Equal(
            ["repository", "[redacted]", "[redacted]", "[redacted]", "[redacted]", "0", "[redacted]", "[redacted]"],
            error.Path);
        Assert.Equal("GitHub reported that the resource is not accessible.", error.Message);
    }

    [Theory]
    [InlineData("query { repository(name: \"sensitiveTail")]
    [InlineData("query { repository(name: \"prefix \\\" sensitiveTail")]
    [InlineData("query { repository(name: \"\"\"sensitiveTail")]
    [InlineData("query { repository(name: \"\"\"prefix \\\"\"\" sensitiveTail")]
    public void Unterminated_literals_never_contribute_path_identifiers(string query)
    {
        using var document = JsonDocument.Parse("""[{"path":["repository","sensitiveTail"]}]""");
        var error = Assert.Single(GraphQLDiagnosticSanitizer.Errors(document.RootElement, query));
        Assert.Equal(["repository", "[redacted]"], error.Path);
    }

    [Fact]
    public void Escaped_block_delimiters_do_not_expose_literals_and_preserve_following_aliases()
    {
        const string query = """"
            query {
              repository(name: """prefix \""" firstPrivateValue \\\""" secondPrivateValue""",
                         owner: "prefix \" quotedPrivateValue") {
                selected: id
              }
            }
            """";
        using var document = JsonDocument.Parse("""
            [{"path":["repository","firstPrivateValue","secondPrivateValue","quotedPrivateValue","selected","id"]}]
            """);
        var error = Assert.Single(GraphQLDiagnosticSanitizer.Errors(document.RootElement, query));
        Assert.Equal(["repository", "[redacted]", "[redacted]", "[redacted]", "selected", "id"], error.Path);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("\"secret\"")]
    public void Malformed_error_containers_produce_safe_diagnostics(string json)
    {
        using var document = JsonDocument.Parse(json);
        var error = Assert.Single(GraphQLDiagnosticSanitizer.Errors(document.RootElement, "query { viewer { login } }"));
        Assert.Equal("Malformed GraphQL errors value.", error.Message);
        Assert.Null(error.Type);
        Assert.Empty(error.Path);
    }

    [Fact]
    public void Malformed_entries_do_not_hide_other_errors()
    {
        using var document = JsonDocument.Parse("""
            [null,42,{"type":{},"message":[],"path":{}},
             {"type":"UNPROCESSABLE","message":"temporary conflict: secret"}]
            """);
        var errors = GraphQLDiagnosticSanitizer.Errors(document.RootElement, "query { viewer { login } }");
        Assert.Equal(4, errors.Count);
        Assert.Equal("UNPROCESSABLE", errors[3].Type);
        Assert.Equal("GitHub reported a temporary conflict.", errors[3].Message);
    }

    [Theory]
    [InlineData("ABCD:1234:ef90", "ABCD:1234:ef90")]
    [InlineData(null, null)]
    [InlineData("ghp_secret", "[redacted]")]
    [InlineData("Bearer secret", "[redacted]")]
    [InlineData("ABCD\r\nsecret", "[redacted]")]
    [InlineData("", "[redacted]")]
    public void Request_ids_are_allowlisted(string? input, string? expected) =>
        Assert.Equal(expected, GraphQLDiagnosticSanitizer.RequestId(input));
}
