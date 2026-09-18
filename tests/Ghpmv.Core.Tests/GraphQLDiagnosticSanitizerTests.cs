using System.Text.Json;
using Ghpmv.Core.GitHub;

namespace Ghpmv.Core.Tests;

public sealed class GraphQLDiagnosticSanitizerTests
{
    [Fact]
    public void Only_field_response_names_survive_executable_document_grammar()
    {
        const string query = """
            query privateOperation(
              $privateVariable: [PrivateInputType!]! = [
                {privateInputField: PRIVATE_ENUM, privateNested: {privateFlag: true},
                 privateList: [null, false, -1.25e+2]}
              ] @privateVariableDirective(privateVariableArgument: PRIVATE_DEFAULT)
            ) @privateOperationDirective(privateOperationArgument: PRIVATE_OPERATION_ENUM) {
              selected: underlying(privateArgument: $privateVariable)
                @privateFieldDirective(privateDirectiveArgument: {privateDirectiveInput: PRIVATE_DIRECTIVE_ENUM}) {
                nodes {
                  id
                  ...privateFragment @privateSpreadDirective(privateSpreadArgument: true)
                  ... on PrivateInlineType @privateInlineDirective { inlineAlias: inlineUnderlying }
                  ... @privateUntypedDirective { untyped }
                }
              }
            }
            fragment privateFragment on PrivateFragmentType @privateFragmentDirective {
              fragmentAlias: fragmentUnderlying
              underlying
            }
            mutation privateMutation { changed: change(input: {}) { result } }
            subscription privateSubscription { event }
            """;
        string[] retained = ["selected", "nodes", "id", "inlineAlias", "untyped", "fragmentAlias", "underlying", "changed", "result", "event"];
        string[] redacted =
        [
            "privateOperation", "privateVariable", "PrivateInputType", "privateInputField", "PRIVATE_ENUM",
            "privateNested", "privateFlag", "privateList", "null", "false", "true",
            "privateVariableDirective", "privateVariableArgument", "PRIVATE_DEFAULT",
            "privateOperationDirective", "privateOperationArgument", "PRIVATE_OPERATION_ENUM",
            "privateArgument", "privateFieldDirective", "privateDirectiveArgument", "privateDirectiveInput",
            "PRIVATE_DIRECTIVE_ENUM", "privateFragment", "privateSpreadDirective", "privateSpreadArgument",
            "PrivateInlineType", "privateInlineDirective", "inlineUnderlying", "privateUntypedDirective",
            "PrivateFragmentType", "privateFragmentDirective", "fragmentUnderlying",
            "privateMutation", "change", "input", "privateSubscription",
        ];
        AssertPaths(query, retained, retained);
        foreach (var chunk in redacted.Chunk(GraphQLDiagnosticSanitizer.MaximumPathSegments))
        {
            AssertPaths(query, chunk, chunk.Select(_ => "[redacted]").ToArray());
        }
    }

    [Theory]
    [InlineData("query { selected } privateTrailing")]
    [InlineData("query { selected(privateArgument: PRIVATE_ENUM)")]
    [InlineData("query { selected() }")]
    [InlineData("query { selected { } }")]
    [InlineData("query { selected(privateArgument: 01) }")]
    [InlineData("query { selected(privateArgument: 1e) }")]
    [InlineData("query { selected(privateArgument: 1.) }")]
    [InlineData("query { selected(privateArgument: \"bad\\q\") }")]
    [InlineData("query { selected(privateArgument: \"bad\\u12\") }")]
    [InlineData("query { selected(privateArgument: \"bad\\uD800\") }")]
    [InlineData("query { selected(privateArgument: \"bad\\uDC00\") }")]
    [InlineData("query { selected(privateArgument: \"unsupported\\u{41}\") }")]
    [InlineData("query { selected(privateArgument: \"bad\nnewline\") }")]
    [InlineData("query($value: String = $other) { selected }")]
    [InlineData("query { selected } fragment on on Type { other }")]
    [InlineData("query { selected ... on }")]
    [InlineData("query { selected ... fragment { other } }")]
    [InlineData("query { selected } type UnsupportedSchemaSyntax { field: String }")]
    public void Malformed_or_unsupported_syntax_discards_all_previously_collected_names(string query) =>
        AssertPaths(query, ["selected", "PRIVATE_ENUM", "privateTrailing"], ["[redacted]", "[redacted]", "[redacted]"]);

    [Fact]
    public void Ordinary_escapes_comments_bom_and_ignored_commas_preserve_nested_fields()
    {
        const string query = "\uFEFF{ selected(arg: \"quote\\\" slash\\\\ \\/ \\b \\f \\n \\r \\t \\u0041 \\uD83D\\uDE00\")"
            + " # syntheticComment \r\n { , nested, } }";
        AssertPaths(query, ["selected", "nested", "arg", "syntheticComment"], ["selected", "nested", "[redacted]", "[redacted]"]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void Query_length_limit_is_fail_closed(int delta)
    {
        var query = "{ selected } #" + new string('x', GraphQLResponseNames.MaximumQueryLength + delta - 14);
        Assert.Equal(GraphQLResponseNames.MaximumQueryLength + delta, query.Length);
        AssertPaths(query, ["selected"], [delta <= 0 ? "selected" : "[redacted]"]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void Identifier_length_limit_is_fail_closed(int delta)
    {
        var name = new string('x', GraphQLResponseNames.MaximumNameLength + delta);
        AssertPaths($"{{ selected {name} }}", ["selected", name],
            delta <= 0 ? ["selected", name] : ["[redacted]", "[redacted]"]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void Selection_depth_limit_is_fail_closed(int delta)
    {
        var depth = GraphQLResponseNames.MaximumDepth + delta;
        var query = string.Concat(Enumerable.Repeat("{ selected ", depth)) + new string('}', depth);
        AssertPaths(query, ["selected"], [delta <= 0 ? "selected" : "[redacted]"]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void Token_limit_is_fail_closed(int delta)
    {
        var query = "{ " + string.Concat(Enumerable.Repeat("x ", GraphQLResponseNames.MaximumTokens + delta - 2)) + "}";
        AssertPaths(query, ["x"], [delta <= 0 ? "x" : "[redacted]"]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void Unique_response_name_limit_is_fail_closed(int delta)
    {
        var query = "{ " + string.Join(' ', Enumerable.Range(0, GraphQLResponseNames.MaximumResponseNames + delta).Select(i => $"field{i}")) + " }";
        AssertPaths(query, ["field0"], [delta <= 0 ? "field0" : "[redacted]"]);
    }

    [Fact]
    public void Nested_input_and_type_syntax_share_bounded_recursion()
    {
        var brackets = new string('[', GraphQLResponseNames.MaximumDepth + 1);
        var closers = new string(']', GraphQLResponseNames.MaximumDepth + 1);
        AssertPaths($"{{ selected(arg: {brackets}ENUM{closers}) }}", ["selected"], ["[redacted]"]);
        AssertPaths($"query($arg: {brackets}String{closers}) {{ selected }}", ["selected"], ["[redacted]"]);
    }

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
        Assert.Equal(["[redacted]", "[redacted]"], error.Path);
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
        Assert.Equal(["repository", "[redacted]", "[redacted]", "[redacted]", "selected", "[redacted]"], error.Path);
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

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(1_000)]
    public void Error_count_is_bounded_with_order_and_explicit_truncation(int count)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(
            Enumerable.Range(0, count).Select(index => new { type = "FORBIDDEN", path = new[] { index } })));
        var errors = GraphQLDiagnosticSanitizer.Errors(document.RootElement, "{ selected }");
        Assert.Equal(Math.Min(count, 17), errors.Count);
        for (var index = 0; index < Math.Min(count, 16); index++)
        {
            Assert.Equal("FORBIDDEN", errors[index].Type);
            Assert.Equal([index.ToString(System.Globalization.CultureInfo.InvariantCulture)], errors[index].Path);
        }

        if (count > 16)
        {
            Assert.Equal("Additional GraphQL errors truncated.", errors[16].Message);
            Assert.Null(errors[16].Type);
            Assert.Empty(errors[16].Path);
        }
        else
        {
            Assert.DoesNotContain(errors, error => error.Message == GraphQLDiagnosticSanitizer.ErrorsTruncated);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(1_000)]
    public void Path_count_is_bounded_with_order_and_explicit_truncation(int count)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new[]
        {
            new { path = Enumerable.Range(0, count).Select(index => (object)(index % 2 == 0 ? "selected" : index)) },
        }));
        var path = Assert.Single(GraphQLDiagnosticSanitizer.Errors(document.RootElement, "{ selected }")).Path;
        Assert.Equal(Math.Min(count, 33), path.Count);
        for (var index = 0; index < Math.Min(count, 32); index++)
        {
            Assert.Equal(index % 2 == 0 ? "selected" : index.ToString(System.Globalization.CultureInfo.InvariantCulture), path[index]);
        }

        if (count > 32)
        {
            Assert.Equal("[path truncated]", path[32]);
        }
        else
        {
            Assert.DoesNotContain(GraphQLDiagnosticSanitizer.PathTruncated, path);
        }
    }

    [Fact]
    public void Malformed_entries_count_towards_caps_without_hiding_the_retained_suffix()
    {
        var malformedErrors = string.Join(',', Enumerable.Repeat("null", 15));
        using var errorsDocument = JsonDocument.Parse("[" + malformedErrors
            + """,{"type":"FORBIDDEN","path":["selected"]},{"type":"UNPROCESSABLE"}]""");
        var errors = GraphQLDiagnosticSanitizer.Errors(errorsDocument.RootElement, "{ selected }");
        Assert.Equal(17, errors.Count);
        Assert.All(errors.Take(15), error => Assert.Empty(error.Path));
        Assert.Equal("FORBIDDEN", errors[15].Type);
        Assert.Equal(["selected"], errors[15].Path);
        Assert.Equal(GraphQLDiagnosticSanitizer.ErrorsTruncated, errors[16].Message);

        var malformedPaths = string.Join(',', Enumerable.Repeat("{}", 31));
        using var pathsDocument = JsonDocument.Parse("""[{"path":[""" + malformedPaths + ""","selected","privateTail"]}]""");
        var path = Assert.Single(GraphQLDiagnosticSanitizer.Errors(pathsDocument.RootElement, "{ selected }")).Path;
        Assert.Equal(33, path.Count);
        Assert.All(path.Take(31), segment => Assert.Equal("[redacted]", segment));
        Assert.Equal("selected", path[31]);
        Assert.Equal(GraphQLDiagnosticSanitizer.PathTruncated, path[32]);
    }

    [Fact]
    public void Error_and_path_iteration_stop_after_a_single_unread_lookahead()
    {
        using var errorDocument = JsonDocument.Parse("""{"path":["selected"]}""");
        var errors = GraphQLDiagnosticSanitizer.Errors(
            PrefixWithUnreadableTail(errorDocument.RootElement, 16), "{ selected }");
        Assert.Equal(17, errors.Count);
        Assert.Equal(GraphQLDiagnosticSanitizer.ErrorsTruncated, errors[16].Message);

        using var pathDocument = JsonDocument.Parse("\"selected\"");
        var path = GraphQLDiagnosticSanitizer.SanitizePath(
            PrefixWithUnreadableTail(pathDocument.RootElement, 32), new(StringComparer.Ordinal) { "selected" });
        Assert.Equal(33, path.Count);
        Assert.Equal(GraphQLDiagnosticSanitizer.PathTruncated, path[32]);
    }

    [Fact]
    public void Path_identifier_length_is_bounded_even_if_the_allowlist_is_supplied_directly()
    {
        var name = new string('x', 129);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new[] { name }));
        Assert.Equal(["[redacted]"], GraphQLDiagnosticSanitizer.SanitizePath(
            document.RootElement.EnumerateArray(), new(StringComparer.Ordinal) { name }));
    }

    private static IEnumerable<JsonElement> PrefixWithUnreadableTail(JsonElement element, int count)
    {
        using var document = JsonDocument.Parse("{}");
        var unreadable = document.RootElement;
        document.Dispose();
        for (var index = 0; index < count; index++)
        {
            yield return element;
        }

        // A disposed element proves the lookahead is not inspected or sanitized.
        yield return unreadable;
        throw new InvalidOperationException("Enumeration continued beyond the bounded prefix and lookahead.");
    }

    private static void AssertPaths(string query, string[] input, string[] expected)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new[] { new { path = input } }));
        var error = Assert.Single(GraphQLDiagnosticSanitizer.Errors(document.RootElement, query));
        Assert.Equal(expected, error.Path);
    }
}
