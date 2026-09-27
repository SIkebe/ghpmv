using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Ghpmv.Core.Import;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Core.Tests;

public class CliImportTests
{
    [Fact]
    public async Task Project_title_override_does_not_relabel_an_old_snapshots_source()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "title-identity-" + Guid.NewGuid().ToString("N"));
        await SnapshotFile.SaveAsync(MinimalSnapshot(), directory, TestContext.Current.CancellationToken);
        using var server = new GraphQlStubServer(EmptyProjectsResponse,
            """{"errors":[{"type":"FORBIDDEN","message":"SYNTHETIC-BODY-SECRET"}]}""");
        try
        {
            var result = await RunCliAsync(directory, server, "--project-title", "Renamed target");
            Assert.Equal(1, result.ExitCode);
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "import-error.json"), TestContext.Current.CancellationToken));
            var context = report.RootElement.GetProperty("context");
            Assert.Equal("Roadmap", context.GetProperty("source").GetProperty("title").GetString());
            Assert.Equal(JsonValueKind.Null, context.GetProperty("source").GetProperty("owner").ValueKind);
            Assert.Equal("Renamed target", context.GetProperty("target").GetProperty("title").GetString());
            Assert.Equal(JsonValueKind.Null, context.GetProperty("target").GetProperty("id").ValueKind);
            Assert.Equal(JsonValueKind.Null, context.GetProperty("target").GetProperty("number").ValueKind);
            Assert.DoesNotContain("SYNTHETIC-BODY-SECRET", result.Error + report.RootElement.GetRawText(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Existing_project_metadata_failure_uses_the_actual_destination_title()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "existing-identity-" + Guid.NewGuid().ToString("N"));
        await SnapshotFile.SaveAsync(MinimalSnapshot() with
        {
            Source = new() { Owner = "source-org", Number = 12, Title = "Demo project" },
        }, directory, TestContext.Current.CancellationToken);
        using var server = new GraphQlStubServer(
            """{"data":{"organization":{"projectV2":{"id":"PVT_existing","number":34,"title":"Destination title","url":"https://github.com/orgs/target/projects/34","public":false,"viewerCanUpdate":true}}}}""",
            """{"errors":[{"type":"FORBIDDEN","message":"SYNTHETIC-BODY-SECRET"}]}""");
        try
        {
            var result = await RunCliAsync(directory, server, "--project-number", "34");
            Assert.Equal(1, result.ExitCode);
            Assert.Contains("source: source-org / Project 12 \"Demo project\"", result.Error, StringComparison.Ordinal);
            Assert.Contains("target: target / Project 34 \"Destination title\"", result.Error, StringComparison.Ordinal);
            Assert.Contains("element: Project", result.Error, StringComparison.Ordinal);
            Assert.Contains("operation: updateProjectV2", result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("SYNTHETIC-BODY-SECRET", result.Error, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Invalid_target_url_argument_does_not_echo_url_credentials()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "early-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var result = await RunIsolatedCliAsync(directory,
            [
                "import", "--org", "target-org", "--in", directory, "--token", "SYNTHETIC-TOKEN-SECRET",
                "--target-base-url", "http://user:SYNTHETIC-URL-SECRET@target.example.test/graphql?token=SYNTHETIC-URL-SECRET",
                "--no-update-check",
            ]);
            Assert.Equal(1, result.ExitCode);
            Assert.Contains("--target-base-url:", result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("SYNTHETIC-", result.Error + result.Output, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(directory, "import-error.json")));
            var earlyFailure = await RunIsolatedCliAsync(directory,
            [
                "import", "--org", "target-org", "--in", directory, "--token", " ", "--no-update-check",
            ]);
            Assert.Equal(1, earlyFailure.ExitCode);
            Assert.Contains("target: target-org / Project (number unknown)", earlyFailure.Error, StringComparison.Ordinal);
            var json = await File.ReadAllTextAsync(Path.Combine(directory, "import-error.json"), TestContext.Current.CancellationToken);
            using var report = JsonDocument.Parse(json);
            var context = report.RootElement.GetProperty("context");
            Assert.Equal("initializing", context.GetProperty("stage").GetString());
            Assert.Equal(JsonValueKind.Null, context.GetProperty("source").ValueKind);
            Assert.Equal(JsonValueKind.Null, context.GetProperty("element").ValueKind);
            Assert.Equal(JsonValueKind.Null, context.GetProperty("target").GetProperty("id").ValueKind);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Recoverable_repository_link_failure_emits_identity_without_response_body()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "repository-identity-" + Guid.NewGuid().ToString("N"));
        var snapshot = MinimalSnapshot() with
        {
            Source = new() { Owner = "source-org", Number = 12, Title = "Demo project" },
            LinkedRepositories = ["target/repository"],
        };
        await SnapshotFile.SaveAsync(snapshot, directory, TestContext.Current.CancellationToken);
        using var server = new GraphQlStubServer(
            ExistingProjectResponse, UpdateProjectResponse, EmptyFieldsResponse,
            """{"data":{"repository":{"id":"R_target"}}}""",
            """{"errors":[{"type":"FORBIDDEN","message":"SYNTHETIC-REPOSITORY-SECRET"}]}""",
            NonTemplateProjectResponse);
        try
        {
            using var client = new Ghpmv.Core.GitHub.GitHubGraphQLClient("synthetic-token", new Uri(server.GraphQlUrl));
            var messages = new List<string>();
            var importer = new ProjectImporter(client)
            {
                OperationLogDirectory = directory,
                OnConflict = ConflictAction.Update,
                OnProgress = messages.Add,
            };
            var result = await importer.ImportAsync(snapshot, "target", TestContext.Current.CancellationToken);
            Assert.Equal(42, result.ProjectNumber);
            var warning = Assert.Single(importer.Warnings);
            Assert.Contains("element: Repository target/repository", warning, StringComparison.Ordinal);
            Assert.Contains("operation: linkProjectV2ToRepository", warning, StringComparison.Ordinal);
            Assert.Contains("source: source-org / Project 12", warning, StringComparison.Ordinal);
            Assert.Contains("target: target / Project 42", warning, StringComparison.Ordinal);
            Assert.Contains("warning: " + warning, messages);
            Assert.DoesNotContain("SYNTHETIC-REPOSITORY-SECRET", string.Join("\n", messages), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Iteration_failure_prints_and_persists_source_target_and_field_identity(bool existing)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Environment.CurrentDirectory, "identity-cli-" + Guid.NewGuid().ToString("N"));
        var snapshot = MinimalSnapshot() with
        {
            Source = new() { Owner = "source-org", OwnerType = "organization", Number = 12, Title = "Demo project", Host = "source.example.test" },
            Fields =
            [
                new()
                {
                    Name = "Demo sprint\nforged-line", DataType = "ITERATION",
                    IterationConfiguration = new()
                    {
                        Duration = 14, StartDay = 1, CompletedIterations = [],
                        Iterations = [new() { Id = "source-iteration", Title = "SYNTHETIC-INPUT-SECRET", StartDate = "2026-01-05", Duration = 14 }],
                    },
                },
            ],
        };
        await SnapshotFile.SaveAsync(snapshot, directory, cancellationToken);
        var responses = existing
            ? new[] { ExistingProjectResponse, UpdateProjectResponse, EmptyFieldsResponse }
            : new[] { EmptyProjectsResponse, OwnerResponse, CreateProjectResponse, UpdateCreatedProjectResponse, EmptyFieldsResponse };
        using var server = new GraphQlStubServer([.. responses,
            """{"errors":[{"type":"UNPROCESSABLE","message":"SYNTHETIC-RESPONSE-SECRET","extensions":{"private":"SYNTHETIC-RESPONSE-SECRET"}}]}"""]);
        try
        {
            var result = existing
                ? await RunCliAsync(directory, server, "--on-conflict", "update")
                : await RunCliAsync(directory, server);
            Assert.Equal(1, result.ExitCode);
            Assert.Contains("source: source-org / Project 12 \"Demo project\"", result.Error, StringComparison.Ordinal);
            Assert.Contains("target: target / Project 42 \"Roadmap\"", result.Error, StringComparison.Ordinal);
            Assert.Contains("element: Field \"Demo sprint\\u000aforged-line\" (ITERATION)", result.Error, StringComparison.Ordinal);
            Assert.Contains("operation: createProjectV2Field", result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("\nforged-line", result.Error, StringComparison.Ordinal);
            var json = await File.ReadAllTextAsync(Path.Combine(directory, "import-error.json"), cancellationToken);
            foreach (var secret in new[] { "SYNTHETIC-INPUT-SECRET", "SYNTHETIC-RESPONSE-SECRET", "dummy-token" })
            {
                Assert.DoesNotContain(secret, json + result.Error + result.Output, StringComparison.Ordinal);
            }
            using var report = JsonDocument.Parse(json);
            var context = report.RootElement.GetProperty("context");
            Assert.Equal("source-org", context.GetProperty("source").GetProperty("owner").GetString());
            Assert.Equal(12, context.GetProperty("source").GetProperty("number").GetInt32());
            Assert.Equal("target", context.GetProperty("target").GetProperty("owner").GetString());
            Assert.Equal(42, context.GetProperty("target").GetProperty("number").GetInt32());
            Assert.Equal(existing ? "PVT_existing" : "PVT_created", context.GetProperty("target").GetProperty("id").GetString());
            var element = context.GetProperty("element");
            Assert.Equal("Field", element.GetProperty("kind").GetString());
            Assert.Equal("Demo sprint\\u000aforged-line", element.GetProperty("name").GetString());
            Assert.Equal("ITERATION", element.GetProperty("dataType").GetString());
            Assert.Equal(JsonValueKind.Null, element.GetProperty("targetId").ValueKind);
            Assert.Equal("createProjectV2Field", context.GetProperty("operation").GetString());
            Assert.Contains("UNPROCESSABLE", result.Error, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(directory, "ghpmv-sensitive-api-*.jsonl"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData("export")]
    [InlineData("import")]
    [InlineData("verify")]
    public async Task Sensitive_diagnostics_require_each_invocation_to_opt_in_and_never_reach_stderr(string command)
    {
        const string sentinel = "SYNTHETIC-CLI-RESPONSE-SECRET";
        var directory = Path.Combine(Environment.CurrentDirectory, "cli-diagnostics-test-" + Guid.NewGuid().ToString("N"));
        var cancellationToken = TestContext.Current.CancellationToken;
        await SnapshotFile.SaveAsync(MinimalSnapshot(), directory, cancellationToken);
        try
        {
            var expectedFileCount = 0;
            var runIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var enabled in new[] { false, true, false })
            {
                using var server = new GraphQlStubServer(
                    """{"errors":[{"type":"FORBIDDEN","message":"SYNTHETIC-CLI-RESPONSE-SECRET","extensions":{"private":"SYNTHETIC-CLI-RESPONSE-SECRET"}}]}""");
                var arguments = new List<string>
                {
                    command, "--org", "synthetic", "--token", "synthetic-token", "--no-update-check",
                    command == "export" ? "--base-url" : "--target-base-url", server.GraphQlUrl,
                };
                if (command == "export")
                {
                    arguments.AddRange(["--project", "1", "--out", directory]);
                }
                else
                {
                    arguments.AddRange(["--in", directory]);
                    if (command == "verify")
                    {
                        arguments.AddRange(["--project", "1"]);
                    }
                }
                if (enabled)
                {
                    arguments.Add("--allow-sensitive-diagnostics");
                    expectedFileCount++;
                }

                var result = await RunIsolatedCliAsync(directory, arguments);
                Assert.Equal(1, result.ExitCode);
                Assert.DoesNotContain(sentinel, result.Error + result.Output, StringComparison.Ordinal);
                Assert.Contains("FORBIDDEN", result.Error, StringComparison.Ordinal);
                Assert.Equal(enabled, result.Error.Contains("SENSITIVE API diagnostics", StringComparison.Ordinal));
                var files = Directory.GetFiles(directory, "ghpmv-sensitive-api-*.jsonl");
                Assert.Equal(expectedFileCount, files.Length);
                if (enabled)
                {
                    Assert.Contains(Path.GetFullPath(files[0]), result.Error, StringComparison.Ordinal);
                    Assert.Contains(sentinel, await File.ReadAllTextAsync(files[0], cancellationToken), StringComparison.Ordinal);
                }
                if (command == "import")
                {
                    var json = await File.ReadAllTextAsync(Path.Combine(directory, "import-error.json"), cancellationToken);
                    Assert.DoesNotContain(sentinel, json, StringComparison.Ordinal);
                    using var report = JsonDocument.Parse(json);
                    var root = report.RootElement;
                    var runId = root.GetProperty("runId").GetString()!;
                    Assert.True(runIds.Add(runId));
                    Assert.Contains($"runId: {runId}", result.Error, StringComparison.Ordinal);
                    var detail = Assert.Single(root.GetProperty("exceptions").EnumerateArray());
                    Assert.Equal(runId, detail.GetProperty("runId").GetString());
                    Assert.Contains($"attemptId: {detail.GetProperty("attemptId").GetString()}", result.Error, StringComparison.Ordinal);
                    Assert.Equal(enabled ? "captured" : "disabled", detail.GetProperty("sensitiveResponseCapture").GetString());
                    if (enabled)
                    {
                        Assert.Equal(Path.GetFullPath(files[0]), root.GetProperty("sensitiveDiagnosticsFile").GetString());
                        using var raw = JsonDocument.Parse(await File.ReadAllTextAsync(files[0], cancellationToken));
                        Assert.Equal(runId, raw.RootElement.GetProperty("runId").GetString());
                        Assert.Equal(detail.GetProperty("attemptId").GetString(), raw.RootElement.GetProperty("attemptId").GetString());
                    }
                    else
                    {
                        Assert.Equal(JsonValueKind.Null, root.GetProperty("sensitiveDiagnosticsFile").ValueKind);
                    }
                }
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData("export")]
    [InlineData("import")]
    [InlineData("verify")]
    [InlineData("setup")]
    public async Task Api_commands_expose_sensitive_diagnostics_help_without_enabling_it(string command)
    {
        var result = await RunIsolatedCliAsync(Environment.CurrentDirectory, [command, "--help"]);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("--allow-sensitive-diagnostics", result.Output, StringComparison.Ordinal);
        Assert.Empty(result.Error);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunIsolatedCliAsync(string directory, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // Neither plausible environment spelling is an opt-in mechanism.
        startInfo.Environment["GHPMV_ALLOW_SENSITIVE_DIAGNOSTICS"] = "true";
        startInfo.Environment["ALLOW_SENSITIVE_DIAGNOSTICS"] = "true";
        startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ghpmv.dll"));
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start synthetic CLI test.");
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, await output, await error);
    }

    [Fact]
    public async Task Import_invocation_shares_correlation_across_rest_preflight_and_graphql_with_separate_report_directory()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "cli-correlation-" + Guid.NewGuid().ToString("N"));
        var reportDirectory = Path.Combine(directory, "snapshot");
        await SnapshotFile.SaveAsync(MinimalSnapshot() with
        {
            Fields = [new FieldSnapshot
            {
                Name = "Synthetic issue field", DataType = "TEXT",
                IssueField = new IssueFieldConfigurationSnapshot { Visibility = "ALL" },
            }],
        }, reportDirectory, TestContext.Current.CancellationToken);
        using var server = new GraphQlStubServer(
            ExistingProjectResponse,
            """{"message":"Validation Failed SYNTHETIC-PROBE-SECRET"}""",
            """{"errors":[{"type":"FORBIDDEN","message":"SYNTHETIC-GRAPHQL-SECRET"}]}""")
        {
            ResponseStatusCodes = [200, 422, 200],
        };
        try
        {
            var result = await RunIsolatedCliAsync(directory,
            [
                "import", "--org", "synthetic", "--in", reportDirectory, "--token", "synthetic-token",
                "--target-base-url", server.GraphQlUrl, "--on-conflict", "update", "--allow-sensitive-diagnostics", "--no-update-check",
            ]);
            Assert.Equal(1, result.ExitCode);
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(reportDirectory, "import-error.json"), TestContext.Current.CancellationToken));
            var rawPath = Assert.Single(Directory.GetFiles(directory, "ghpmv-sensitive-api-*.jsonl"));
            Assert.Equal(rawPath, report.RootElement.GetProperty("sensitiveDiagnosticsFile").GetString());
            var lines = await File.ReadAllLinesAsync(rawPath, TestContext.Current.CancellationToken);
            Assert.Equal(2, lines.Length);
            using var probe = JsonDocument.Parse(lines[0]);
            using var graph = JsonDocument.Parse(lines[1]);
            var runId = report.RootElement.GetProperty("runId").GetString();
            Assert.Equal(runId, probe.RootElement.GetProperty("runId").GetString());
            Assert.Equal(runId, graph.RootElement.GetProperty("runId").GetString());
            Assert.Equal("RestValidationProbe", probe.RootElement.GetProperty("operation").GetString());
            Assert.Equal("GraphQlMutation", graph.RootElement.GetProperty("operation").GetString());
            Assert.NotEqual(probe.RootElement.GetProperty("attemptId").GetString(), graph.RootElement.GetProperty("attemptId").GetString());
            var exception = Assert.Single(report.RootElement.GetProperty("exceptions").EnumerateArray());
            Assert.Equal(graph.RootElement.GetProperty("attemptId").GetString(), exception.GetProperty("attemptId").GetString());
            Assert.DoesNotContain("SYNTHETIC-", result.Error + result.Output + report.RootElement.GetRawText(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Local_only_import_failure_with_opt_in_does_not_claim_a_sensitive_file()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "cli-local-correlation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var result = await RunIsolatedCliAsync(directory,
            [
                "import", "--org", "synthetic", "--in", directory, "--token", " ", "--allow-sensitive-diagnostics", "--no-update-check",
            ]);
            Assert.Equal(1, result.ExitCode);
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "import-error.json"), TestContext.Current.CancellationToken));
            Assert.True(Guid.TryParseExact(report.RootElement.GetProperty("runId").GetString(), "N", out _));
            Assert.Equal(JsonValueKind.Null, report.RootElement.GetProperty("sensitiveDiagnosticsFile").ValueKind);
            Assert.Equal("not-created", report.RootElement.GetProperty("sensitiveDiagnosticsState").GetString());
            Assert.Equal(JsonValueKind.Null, report.RootElement.GetProperty("exceptions")[0].GetProperty("attemptId").ValueKind);
            Assert.Empty(Directory.GetFiles(directory, "ghpmv-sensitive-api-*"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Import_invalid_snapshot_writes_a_diagnostic_report()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-invalid-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "snapshot.json"),
            "{}",
            cancellationToken);

        using var server = new GraphQlStubServer();
        try
        {
            var result = await RunCliAsync(directory, server);

            Assert.Equal(1, result.ExitCode);
            Assert.DoesNotContain("Unhandled exception", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Detailed error log:", result.Error, StringComparison.Ordinal);
            using var diagnostic = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(directory, "import-error.json"),
                cancellationToken));
            Assert.Equal("loading-snapshot", diagnostic.RootElement.GetProperty("stage").GetString());
            var context = diagnostic.RootElement.GetProperty("context");
            Assert.Equal(JsonValueKind.Null, context.GetProperty("source").ValueKind);
            Assert.Equal(JsonValueKind.Null, context.GetProperty("element").ValueKind);
            Assert.Equal("target", context.GetProperty("target").GetProperty("owner").GetString());
            Assert.Contains(
                diagnostic.RootElement.GetProperty("exceptions").EnumerateArray(),
                exception => exception.GetProperty("type").GetString() ==
                    "System.IO.InvalidDataException");
            Assert.Empty(server.RequestBodies);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_snapshot_access_denied_writes_a_diagnostic_report()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-denied-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "snapshot.json"));

        using var server = new GraphQlStubServer();
        try
        {
            var result = await RunCliAsync(directory, server);

            Assert.Equal(1, result.ExitCode);
            Assert.DoesNotContain("Unhandled exception", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Detailed error log:", result.Error, StringComparison.Ordinal);
            using var diagnostic = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(directory, "import-error.json"),
                cancellationToken));
            Assert.Equal("loading-snapshot", diagnostic.RootElement.GetProperty("stage").GetString());
            Assert.Contains(
                diagnostic.RootElement.GetProperty("exceptions").EnumerateArray(),
                exception => exception.GetProperty("type").GetString() ==
                    "System.UnauthorizedAccessException");
            Assert.Empty(server.RequestBodies);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_invalid_mapping_writes_a_preflight_diagnostic_report()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-invalid-mapping-" + Guid.NewGuid().ToString("N"));
        await SnapshotFile.SaveAsync(MinimalSnapshot(), directory, cancellationToken);
        var mappingPath = Path.Combine(directory, "repository-mapping.csv");
        await File.WriteAllTextAsync(mappingPath, "invalid-header\n", cancellationToken);

        using var server = new GraphQlStubServer();
        try
        {
            var result = await RunCliAsync(directory, server, "--repo-mapping", mappingPath);

            Assert.Equal(1, result.ExitCode);
            using var diagnostic = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(directory, "import-error.json"),
                cancellationToken));
            Assert.Equal("preflight", diagnostic.RootElement.GetProperty("stage").GetString());
            Assert.Equal(JsonValueKind.Null, diagnostic.RootElement.GetProperty("context").GetProperty("element").ValueKind);
            Assert.Contains(
                diagnostic.RootElement.GetProperty("exceptions").EnumerateArray(),
                exception => exception.GetProperty("type").GetString() ==
                    "System.FormatException");
            Assert.Empty(server.RequestBodies);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Failed_project_number_lookup_does_not_record_the_request_as_the_resolved_target()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-missing-project-" + Guid.NewGuid().ToString("N"));
        await SnapshotFile.SaveAsync(MinimalSnapshot(), directory, cancellationToken);

        using var server = new GraphQlStubServer(
            """{"data":{"organization":{"projectV2":null}}}""");
        try
        {
            var result = await RunCliAsync(directory, server, "--project-number", "99");

            Assert.Equal(1, result.ExitCode);
            using var diagnostic = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(directory, "import-error.json"),
                cancellationToken));
            Assert.Equal(99, diagnostic.RootElement.GetProperty("requestedTargetProjectNumber").GetInt32());
            Assert.Equal(JsonValueKind.Null, diagnostic.RootElement.GetProperty("targetProjectNumber").ValueKind);
            Assert.Equal(JsonValueKind.Null, diagnostic.RootElement.GetProperty("targetProjectUrl").ValueKind);
            Assert.Equal(99, diagnostic.RootElement.GetProperty("context").GetProperty("target").GetProperty("number").GetInt32());
            Assert.Equal(JsonValueKind.Null, diagnostic.RootElement.GetProperty("context").GetProperty("target").GetProperty("id").ValueKind);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Verify_reports_category_statuses_and_writes_consistent_json()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-verify-" + Guid.NewGuid().ToString("N"));
        var reportPath = Path.Combine(directory, "report.json");
        await SnapshotFile.SaveAsync(VerifySnapshot(), directory, cancellationToken);

        using var server = new GraphQlStubServer(
            VerifyProjectResponse,
            VerifyItemsResponse,
            VerifyStatusUpdatesResponse,
            VerifyFieldsResponse,
            VerifyTeamsResponse);
        try
        {
            var result = await RunVerifyCliAsync(directory, server, "--report-json", reportPath);

            Assert.Equal(5, server.RequestBodies.Count);
            Assert.Equal(1, result.ExitCode);
            Assert.Contains("Project: Match", result.Output, StringComparison.Ordinal);
            Assert.Contains("LinkedRepository: PartialMatch", result.Output, StringComparison.Ordinal);
            Assert.Contains("Collaborator: NotVerified", result.Output, StringComparison.Ordinal);
            Assert.Contains("1 warning(s)", result.Output, StringComparison.Ordinal);
            Assert.EndsWith("NotVerified." + Environment.NewLine, result.Output, StringComparison.Ordinal);

            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath, cancellationToken));
            Assert.Equal("NotVerified", report.RootElement.GetProperty("status").GetString());
            Assert.Equal(1, report.RootElement.GetProperty("warningCount").GetInt32());
            Assert.Equal(1, report.RootElement.GetProperty("notVerifiedCount").GetInt32());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Verify_reports_template_drift_in_the_project_category_and_json()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-verify-template-" + Guid.NewGuid().ToString("N"));
        var reportPath = Path.Combine(directory, "report.json");
        var snapshot = VerifySnapshot();
        await SnapshotFile.SaveAsync(
            snapshot with { Project = snapshot.Project with { Template = true } },
            directory,
            cancellationToken);

        using var server = new GraphQlStubServer(
            VerifyProjectResponse,
            VerifyItemsResponse,
            VerifyStatusUpdatesResponse,
            VerifyFieldsResponse,
            VerifyTeamsResponse);
        try
        {
            var result = await RunVerifyCliAsync(directory, server, "--report-json", reportPath);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("Project: Mismatch", result.Output, StringComparison.Ordinal);
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath, cancellationToken));
            Assert.Contains(
                report.RootElement.GetProperty("differences").EnumerateArray(),
                difference => difference.GetProperty("category").GetString() == "Project"
                    && difference.GetProperty("message").GetString()!
                        .Contains("template state mismatch", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Verify_categories_limits_comparison_and_api_sections()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-verify-categories-" + Guid.NewGuid().ToString("N"));
        await SnapshotFile.SaveAsync(VerifySnapshot(), directory, cancellationToken);

        using var server = new GraphQlStubServer(VerifyProjectResponse, VerifyFieldsResponse);
        try
        {
            var result = await RunVerifyCliAsync(directory, server, "--categories", "view");

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(2, server.RequestBodies.Count);
            Assert.Contains(server.RequestBodies, body =>
                body.Contains("fields(first:", StringComparison.Ordinal));
            Assert.Contains("selected verification categories match", result.Output, StringComparison.Ordinal);
            Assert.Contains("View: Match", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("Project:", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("Workflow:", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Conflict_skip_with_browser_automation_does_not_run_downstream_importers()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-skip-" + Guid.NewGuid().ToString("N"));
        await SnapshotFile.SaveAsync(SnapshotWithDownstreamContent(), directory, cancellationToken);
        var diagnosticPath = Path.Combine(directory, "import-error.json");
        await File.WriteAllTextAsync(diagnosticPath, """{"previousFailure":true}""", cancellationToken);

        using var server = new GraphQlStubServer(ExistingProjectResponse);
        try
        {
            var result = await RunCliAsync(
                directory,
                server,
                "--on-conflict", "skip",
                "--enable-browser-automation");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("result=skipped project=42", result.Output, StringComparison.Ordinal);
            Assert.Contains("skipped without making changes", result.Error, StringComparison.Ordinal);
            Assert.Single(server.RequestBodies);
            Assert.DoesNotContain("mutation", server.RequestBodies[0], StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(diagnosticPath));
            Assert.Contains(
                "previousFailure",
                await File.ReadAllTextAsync(diagnosticPath, cancellationToken),
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Conflict_fail_returns_error_without_a_result_line()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-fail-" + Guid.NewGuid().ToString("N"));
        await SnapshotFile.SaveAsync(MinimalSnapshot(), directory, cancellationToken);

        using var server = new GraphQlStubServer(ExistingProjectResponse);
        try
        {
            var result = await RunCliAsync(directory, server, "--on-conflict", "fail");

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("already exists", result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("result=", result.Output, StringComparison.Ordinal);
            using var diagnostic = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(directory, "import-error.json"),
                cancellationToken));
            Assert.Equal("importing-project", diagnostic.RootElement.GetProperty("stage").GetString());
            var request = Assert.Single(server.RequestBodies);
            Assert.DoesNotContain("mutation", request, StringComparison.OrdinalIgnoreCase);
        }

        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Created_project_failure_records_target_before_metadata_writes_complete()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-created-target-" + Guid.NewGuid().ToString("N"));
        await SnapshotFile.SaveAsync(MinimalSnapshot(), directory, cancellationToken);

        using var server = new GraphQlStubServer(
            EmptyProjectsResponse,
            OwnerResponse,
            CreateProjectResponse,
            TemplateMutationErrorResponse);
        try
        {
            var result = await RunCliAsync(directory, server);

            Assert.Equal(1, result.ExitCode);
            using var diagnostic = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(directory, "import-error.json"),
                cancellationToken));
            Assert.Equal(42, diagnostic.RootElement.GetProperty("targetProjectNumber").GetInt32());
            Assert.Equal(
                "https://github.com/orgs/target/projects/42",
                diagnostic.RootElement.GetProperty("targetProjectUrl").GetString());
            Assert.Equal("importing-project", diagnostic.RootElement.GetProperty("stage").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Warning_completed_import_preserves_previous_failure_report()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-warning-log-" + Guid.NewGuid().ToString("N"));
        var snapshot = MinimalSnapshot() with
        {
            Fields =
            [
                new FieldSnapshot
                {
                    Name = "Custom",
                    DataType = "TEXT",
                    DefaultValue = new FieldDefaultValueSnapshot { Text = "default" },
                },
            ],
        };
        await SnapshotFile.SaveAsync(snapshot, directory, cancellationToken);
        var diagnosticPath = Path.Combine(directory, "import-error.json");
        await File.WriteAllTextAsync(diagnosticPath, """{"previousFailure":true}""", cancellationToken);
        const string existingFieldResponse =
            """{"data":{"node":{"fields":{"nodes":[{"__typename":"ProjectV2Field","id":"PVTF_custom","name":"Custom","dataType":"TEXT"}]}}}}""";

        using var server = new GraphQlStubServer(
            ExistingProjectResponse,
            UpdateProjectResponse,
            existingFieldResponse,
            NonTemplateProjectResponse);
        try
        {
            var result = await RunCliAsync(directory, server, "--on-conflict", "update");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains(
                "captured field defaults require browser automation",
                result.Error,
                StringComparison.Ordinal);
            Assert.True(File.Exists(diagnosticPath));
            Assert.Contains(
                "previousFailure",
                await File.ReadAllTextAsync(diagnosticPath, cancellationToken),
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Browser_filter_mapping_preflight_fails_before_any_mutation_by_default()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-filter-preflight-" + Guid.NewGuid().ToString("N"));
        var snapshot = MinimalSnapshot() with
        {
            Views =
            [
                new ViewSnapshot
                {
                    Number = 1,
                    Name = "EMU",
                    Layout = "TABLE_LAYOUT",
                    Filter = "assignee:old-user",
                    GroupByFields = [],
                    SortByFields = [],
                    VerticalGroupByFields = [],
                    VisibleFields = [],
                },
            ],
            StatusUpdates = [],
        };
        await SnapshotFile.SaveAsync(snapshot, directory, cancellationToken);
        await new ImportLog
        {
            ProjectId = "PVT_existing",
            SourceSnapshotFingerprint = ImportLog.ComputeSnapshotFingerprint(snapshot),
            TemplateRestorationRequired = true,
        }.SaveAsync(directory, cancellationToken);

        using var server = new GraphQlStubServer(
            TemplateProjectResponse,
            ExistingProjectResponse);
        try
        {
            var result = await RunCliAsync(directory, server, "--enable-browser-automation");

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("unmapped assignee value 'old-user'", result.Error, StringComparison.Ordinal);
            Assert.Contains("Filter mapping preflight failed", result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain(server.RequestBodies, request =>
                request.Contains("mutation", StringComparison.OrdinalIgnoreCase));
            var importLog = await ImportLog.LoadAsync(directory, cancellationToken);
            Assert.NotNull(importLog);
            Assert.False(importLog.TemplateRestorationRequired);
            using var diagnostic = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(directory, "import-error.json"),
                cancellationToken));
            Assert.Equal("preflight", diagnostic.RootElement.GetProperty("stage").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Conflict_update_emits_stable_result_and_applies_project_mutation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-update-" + Guid.NewGuid().ToString("N"));
        await SnapshotFile.SaveAsync(MinimalSnapshot(), directory, cancellationToken);

        using var server = new GraphQlStubServer(
            ExistingProjectResponse,
            UpdateProjectResponse,
            EmptyFieldsResponse,
            NonTemplateProjectResponse);
        try
        {
            var result = await RunCliAsync(directory, server, "--on-conflict", "update");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("result=updated project=42", result.Output, StringComparison.Ordinal);
            Assert.Contains(
                "items: created=0 resumed=0 already-complete=0 skipped=0 warnings=0",
                result.Output,
                StringComparison.Ordinal);
            Assert.Contains(
                "status-updates: created=0 resumed=0 already-complete=0",
                result.Output,
                StringComparison.Ordinal);
            Assert.Contains("views: imported=0 warnings=0", result.Output, StringComparison.Ordinal);
            Assert.Equal(4, server.RequestBodies.Count);
            Assert.Single(server.RequestBodies, request =>
                request.Contains("mutation", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(server.RequestBodies, request =>
                request.Contains("ProjectV2AsTemplate", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task User_owned_template_snapshot_fails_before_any_api_request()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-user-template-" + Guid.NewGuid().ToString("N"));
        await SnapshotFile.SaveAsync(SnapshotWithTemplate(true), directory, cancellationToken);

        using var server = new GraphQlStubServer();
        try
        {
            var result = await RunCliAsync(directory, server, "--owner-type", "user");

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("user-owned Project cannot be marked as a template", result.Error, StringComparison.Ordinal);
            Assert.Empty(server.RequestBodies);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Successful_created_project_import_restores_requested_conflict_policy_on_retry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-created-complete-" + Guid.NewGuid().ToString("N"));
        await SnapshotFile.SaveAsync(MinimalSnapshot(), directory, cancellationToken);

        try
        {
            using (var createServer = new GraphQlStubServer(
                       EmptyProjectsResponse,
                       OwnerResponse,
                       CreateProjectResponse,
                       UpdateCreatedProjectResponse,
                       EmptyFieldsResponse,
                       NonTemplateProjectResponse))
            {
                var created = await RunCliAsync(directory, createServer);

                Assert.Equal(0, created.ExitCode);
                Assert.Contains("result=created project=42", created.Output, StringComparison.Ordinal);
            }

            var completedLog = await ProjectImportLog.LoadAsync(directory, cancellationToken);
            Assert.Equal("PVT_created", completedLog.CreatedProjectId);
            Assert.True(completedLog.ImportCompleted);
            Assert.False(completedLog.HasUnresolvedWarnings);

            using var retryServer = new GraphQlStubServer(CreatedProjectLookupResponse);
            var retry = await RunCliAsync(directory, retryServer, "--on-conflict", "fail");

            Assert.Equal(1, retry.ExitCode);
            Assert.Contains("already exists", retry.Error, StringComparison.Ordinal);
            Assert.Single(retryServer.RequestBodies);
            Assert.DoesNotContain(
                retryServer.RequestBodies,
                request => request.Contains("mutation", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public void Import_completion_remains_incomplete_when_core_stages_warn(
        int projectWarningCount,
        int itemWarningCount)
    {
        var log = new ProjectImportLog
        {
            CreatedProjectId = "PVT_created",
            ImportCompleted = false,
            HasUnresolvedWarnings = false,
        };

        var changed = log.TryMarkImportCompleted(
            browserAutomationEnabled: false,
            projectWarningCount,
            itemWarningCount,
            viewWarningCount: 0,
            workflowWarningCount: 0);

        Assert.False(changed);
        Assert.False(log.ImportCompleted);
        Assert.True(log.HasUnresolvedWarnings);

        changed = log.TryMarkImportCompleted(
            browserAutomationEnabled: false,
            projectWarningCount: 0,
            itemWarningCount: 0,
            viewWarningCount: 0,
            workflowWarningCount: 0);

        Assert.False(changed);
        Assert.False(log.ImportCompleted);
        Assert.True(log.HasUnresolvedWarnings);
    }

    [Fact]
    public void Legacy_incomplete_import_without_warning_state_fails_closed()
    {
        var log = new ProjectImportLog
        {
            CreatedProjectId = "PVT_created",
            ImportCompleted = false,
        };

        var changed = log.TryMarkImportCompleted(
            browserAutomationEnabled: false,
            projectWarningCount: 0,
            itemWarningCount: 0,
            viewWarningCount: 0,
            workflowWarningCount: 0);

        Assert.False(changed);
        Assert.False(log.ImportCompleted);
        Assert.Null(log.HasUnresolvedWarnings);
    }

    [Fact]
    public async Task Incomplete_item_log_forces_update_on_default_retry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-resume-" + Guid.NewGuid().ToString("N"));
        var snapshot = MinimalSnapshot() with
        {
            Items =
            [
                new ItemSnapshot
                {
                    Type = "DRAFT_ISSUE",
                    Position = 0,
                    IsArchived = false,
                    Draft = new DraftIssueSnapshot { Title = "Interrupted", Assignees = [] },
                    FieldValues = [],
                },
            ],
        };
        await SnapshotFile.SaveAsync(snapshot, directory, cancellationToken);
        var log = new Ghpmv.Core.Import.ImportLog
        {
            ProjectId = "PVT_existing",
            SourceSnapshotFingerprint = Ghpmv.Core.Import.ImportLog.ComputeSnapshotFingerprint(snapshot),
        };
        log.Items["0"] = "PVTI_interrupted";
        log.ItemStates["DRAFT_ISSUE:Interrupted::::position:0"] = new Ghpmv.Core.Import.ImportItemState
        {
            TargetItemId = "PVTI_interrupted",
            TargetContentIdentity = "DRAFT_ISSUE:assignees:",
        };
        await log.SaveAsync(directory, cancellationToken);

        using var server = new GraphQlStubServer(
            ExistingProjectResponse,
            UpdateProjectResponse,
            EmptyFieldsResponse,
            PositionResponse,
            NonTemplateProjectResponse);
        try
        {
            var result = await RunCliAsync(directory, server);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("result=updated project=42", result.Output, StringComparison.Ordinal);
            Assert.Contains("resumed=1", result.Output, StringComparison.Ordinal);
            var completed = await Ghpmv.Core.Import.ImportLog.LoadAsync(directory, cancellationToken);
            var state = Assert.Single(completed!.ItemStates).Value;
            Assert.True(state.FieldValuesApplied);
            Assert.True(state.PositionApplied);
            Assert.True(state.ArchiveApplied);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_prints_the_status_update_summary_line_after_items()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-status-line-" + Guid.NewGuid().ToString("N"));
        await SnapshotFile.SaveAsync(SnapshotWithStatusUpdates(), directory, cancellationToken);

        using var server = new GraphQlStubServer(
            ExistingProjectResponse,
            UpdateProjectResponse,
            EmptyFieldsResponse,
            NonTemplateProjectResponse,
            CreateStatusUpdateResponse);
        try
        {
            var result = await RunCliAsync(directory, server, "--on-conflict", "update");

            Assert.Equal(0, result.ExitCode);
            string[] expected =
            [
                "https://github.com/orgs/target/projects/42",
                "result=updated project=42",
                "items: created=0 resumed=0 already-complete=0 skipped=0 warnings=0",
                "status-updates: created=1 resumed=0 already-complete=0",
                "views: imported=0 warnings=0",
            ];
            Assert.Equal(expected, result.Output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));

            // The stdout contract is additive: the new line reports real work, and the
            // created update carries the attribution note plus the snapshot's dates.
            Assert.Equal(5, server.RequestBodies.Count);
            var createRequest = Assert.Single(
                server.RequestBodies,
                request => request.Contains("createProjectV2StatusUpdate", StringComparison.Ordinal));
            Assert.Contains("Originally created by @octocat on 2024-01-05T09:00:00Z", createRequest, StringComparison.Ordinal);
            Assert.Contains("Kickoff complete.", createRequest, StringComparison.Ordinal);
            Assert.Contains("\"status\":\"ON_TRACK\"", createRequest, StringComparison.Ordinal);
            Assert.Contains("\"startDate\":\"2024-01-01\"", createRequest, StringComparison.Ordinal);
            Assert.Contains("\"targetDate\":\"2024-03-31\"", createRequest, StringComparison.Ordinal);

            // A non-template target must never be touched by the template seam.
            Assert.DoesNotContain(server.RequestBodies, request =>
                request.Contains("ProjectV2AsTemplate", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_skip_path_does_not_print_the_status_update_line()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-status-skip-" + Guid.NewGuid().ToString("N"));
        await SnapshotFile.SaveAsync(SnapshotWithStatusUpdates(), directory, cancellationToken);

        using var server = new GraphQlStubServer(ExistingProjectResponse);
        try
        {
            var result = await RunCliAsync(directory, server, "--on-conflict", "skip");

            Assert.Equal(0, result.ExitCode);
            string[] expected =
            [
                "https://github.com/orgs/target/projects/42",
                "result=skipped project=42",
            ];
            Assert.Equal(expected, result.Output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
            Assert.DoesNotContain("status-updates:", result.Output, StringComparison.Ordinal);

            // Skipping means no downstream stage ran at all: no template probe, no writes.
            var request = Assert.Single(server.RequestBodies);
            Assert.DoesNotContain("mutation", request, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("statusUpdates", request, StringComparison.Ordinal);
            Assert.Contains("skipped without making changes", result.Error, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_marks_and_restores_the_template_only_when_the_snapshot_has_status_updates()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var withoutDirectory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-template-none-" + Guid.NewGuid().ToString("N"));
        var withDirectory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-template-some-" + Guid.NewGuid().ToString("N"));
        await SnapshotFile.SaveAsync(MinimalSnapshot() with { StatusUpdates = [] }, withoutDirectory, cancellationToken);
        var templateSnapshot = SnapshotWithStatusUpdates();
        await SnapshotFile.SaveAsync(
            templateSnapshot with { Project = templateSnapshot.Project with { Template = true } },
            withDirectory,
            cancellationToken);

        try
        {
            using (var withoutServer = new GraphQlStubServer(
                ExistingProjectResponse,
                UpdateProjectResponse,
                EmptyFieldsResponse,
                NonTemplateProjectResponse))
            {
                var withoutResult = await RunCliAsync(withoutDirectory, withoutServer, "--on-conflict", "update");

                Assert.Equal(0, withoutResult.ExitCode);
                Assert.Contains(
                    "status-updates: created=0 resumed=0 already-complete=0",
                    withoutResult.Output,
                    StringComparison.Ordinal);

                Assert.Equal(4, withoutServer.RequestBodies.Count);
                Assert.DoesNotContain(withoutServer.RequestBodies, request =>
                    request.Contains("ProjectV2AsTemplate", StringComparison.Ordinal));
                Assert.DoesNotContain(withoutServer.RequestBodies, request =>
                    request.Contains("createProjectV2StatusUpdate", StringComparison.Ordinal));
            }

            using var withServer = new GraphQlStubServer(
                ExistingProjectResponse,
                UpdateProjectResponse,
                EmptyFieldsResponse,
                TemplateProjectResponse,
                UnmarkTemplateResponse,
                CreateStatusUpdateResponse,
                MarkTemplateResponse);
            var withResult = await RunCliAsync(withDirectory, withServer, "--on-conflict", "update");

            Assert.Equal(0, withResult.ExitCode);
            Assert.Contains(
                "status-updates: created=1 resumed=0 already-complete=0",
                withResult.Output,
                StringComparison.Ordinal);
            Assert.Equal(7, withServer.RequestBodies.Count);
            Assert.Single(withServer.RequestBodies, IsUnmarkTemplateMutation);
            Assert.Single(withServer.RequestBodies, IsMarkTemplateMutation);
            Assert.Single(withServer.RequestBodies, request =>
                request.Contains("createProjectV2StatusUpdate", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(withoutDirectory, recursive: true);
            Directory.Delete(withDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_applies_the_requested_template_state_after_downstream_importers()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-template-order-" + Guid.NewGuid().ToString("N"));
        var snapshot = SnapshotWithStatusUpdates();
        await SnapshotFile.SaveAsync(
            snapshot with { Project = snapshot.Project with { Template = true } },
            directory,
            cancellationToken);

        using var server = new GraphQlStubServer(
            ExistingProjectResponse,
            UpdateProjectResponse,
            EmptyFieldsResponse,
            TemplateProjectResponse,
            UnmarkTemplateResponse,
            CreateStatusUpdateResponse,
            MarkTemplateResponse);
        try
        {
            var result = await RunCliAsync(directory, server, "--on-conflict", "update");

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(7, server.RequestBodies.Count);

            var unmarkIndex = server.RequestBodies.FindIndex(IsUnmarkTemplateMutation);
            var createIndex = server.RequestBodies.FindIndex(request =>
                request.Contains("createProjectV2StatusUpdate", StringComparison.Ordinal));
            var markIndex = server.RequestBodies.FindIndex(IsMarkTemplateMutation);
            var projectUpdateIndex = server.RequestBodies.FindIndex(request =>
                request.Contains("updateProjectV2", StringComparison.Ordinal));

            Assert.True(projectUpdateIndex >= 0 && projectUpdateIndex < unmarkIndex);
            Assert.True(unmarkIndex >= 0 && unmarkIndex < createIndex);
            Assert.True(createIndex < markIndex);

            // The requested final template state is the last orchestration stage.
            Assert.Equal(server.RequestBodies.Count - 1, markIndex);
            Assert.Contains(
                "Temporarily unmarking the target project as a template before status update writes...",
                result.Error,
                StringComparison.Ordinal);
            Assert.Contains(
                "Marking the target project as a template as the final import stage...",
                result.Error,
                StringComparison.Ordinal);
            Assert.Contains(
                "status-updates: created=1 resumed=0 already-complete=0",
                result.Output,
                StringComparison.Ordinal);
            var importLog = await ImportLog.LoadAsync(directory, cancellationToken);
            Assert.NotNull(importLog);
            Assert.Single(importLog.StatusUpdates);
            Assert.Empty(importLog.PendingStatusUpdates);
            Assert.False(importLog.TemplateRestorationRequired);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_marks_a_new_project_as_a_template_only_after_all_writers()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-template-create-" + Guid.NewGuid().ToString("N"));
        var snapshot = SnapshotWithStatusUpdates();
        await SnapshotFile.SaveAsync(
            snapshot with { Project = snapshot.Project with { Template = true } },
            directory,
            cancellationToken);

        using var server = new GraphQlStubServer(
            EmptyProjectsResponse,
            OwnerResponse,
            CreateProjectResponse,
            UpdateCreatedProjectResponse,
            EmptyFieldsResponse,
            """{"data":{"node":{"id":"PVT_created","template":false}}}""",
            CreateStatusUpdateResponse,
            """{"data":{"markProjectV2AsTemplate":{"projectV2":{"id":"PVT_created","template":true}}}}""");
        try
        {
            var result = await RunCliAsync(directory, server);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(8, server.RequestBodies.Count);
            var statusUpdateIndex = server.RequestBodies.FindIndex(request =>
                request.Contains("createProjectV2StatusUpdate", StringComparison.Ordinal));
            Assert.True(statusUpdateIndex >= 0 && statusUpdateIndex < server.RequestBodies.Count - 1);
            Assert.True(IsMarkTemplateMutation(server.RequestBodies[^1]));
            Assert.Contains("\"projectId\":\"PVT_created\"", server.RequestBodies[^1], StringComparison.Ordinal);
            Assert.Contains(
                "Marking the target project as a template as the final import stage...",
                result.Error,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_unmarks_an_existing_project_as_the_final_stage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-template-unmark-" + Guid.NewGuid().ToString("N"));
        await SnapshotFile.SaveAsync(
            SnapshotWithTemplate(false) with { StatusUpdates = [] },
            directory,
            cancellationToken);
        var diagnosticPath = Path.Combine(directory, "import-error.json");
        await File.WriteAllTextAsync(diagnosticPath, """{"previousFailure":true}""", cancellationToken);

        using var server = new GraphQlStubServer(
            ExistingProjectResponse,
            UpdateProjectResponse,
            EmptyFieldsResponse,
            TemplateProjectResponse,
            UnmarkTemplateResponse);
        try
        {
            var result = await RunCliAsync(directory, server, "--on-conflict", "update");

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(5, server.RequestBodies.Count);
            Assert.True(IsUnmarkTemplateMutation(server.RequestBodies[^1]));
            Assert.DoesNotContain(server.RequestBodies, IsMarkTemplateMutation);
            Assert.False(File.Exists(diagnosticPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_reports_a_template_restore_failure_on_stderr_and_fails_the_run()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-template-restore-fail-" + Guid.NewGuid().ToString("N"));
        var snapshot = SnapshotWithStatusUpdates();
        await SnapshotFile.SaveAsync(
            snapshot with { Project = snapshot.Project with { Template = true } },
            directory,
            cancellationToken);
        var diagnosticPath = Path.Combine(directory, "import-error.json");
        await File.WriteAllTextAsync(diagnosticPath, """{"previousFailure":true}""", cancellationToken);

        using var server = new GraphQlStubServer(
            ExistingProjectResponse,
            UpdateProjectResponse,
            EmptyFieldsResponse,
            TemplateProjectResponse,
            UnmarkTemplateResponse,
            CreateStatusUpdateResponse,
            TemplateMutationErrorResponse);
        try
        {
            var result = await RunCliAsync(directory, server, "--on-conflict", "update");

            // The status update itself was written before the restore failed.
            Assert.Single(server.RequestBodies, request =>
                request.Contains("createProjectV2StatusUpdate", StringComparison.Ordinal));
            Assert.Equal(1, result.ExitCode);
            Assert.DoesNotContain("Template restore is not permitted", result.Error, StringComparison.Ordinal);
            Assert.Contains("FORBIDDEN", result.Error, StringComparison.Ordinal);
            Assert.Contains("Detailed error log:", result.Error, StringComparison.Ordinal);

            Assert.True(File.Exists(diagnosticPath));
            var diagnosticJson = await File.ReadAllTextAsync(
                diagnosticPath,
                cancellationToken);
            Assert.DoesNotContain("previousFailure", diagnosticJson, StringComparison.Ordinal);
            Assert.DoesNotContain("Template restore is not permitted", diagnosticJson, StringComparison.Ordinal);
            using var diagnostic = JsonDocument.Parse(diagnosticJson);
            var root = diagnostic.RootElement;
            Assert.Equal("import", root.GetProperty("command").GetString());
            Assert.Equal("target", root.GetProperty("targetOwner").GetString());
            Assert.Equal(42, root.GetProperty("targetProjectNumber").GetInt32());
            Assert.Equal(
                "https://github.com/orgs/target/projects/42",
                root.GetProperty("targetProjectUrl").GetString());
            Assert.Equal("finalizing-template-state", root.GetProperty("stage").GetString());
            Assert.False(root.GetProperty("browserAutomationEnabled").GetBoolean());
            Assert.Contains(
                root.GetProperty("progress").EnumerateArray(),
                entry => entry.GetProperty("message").GetString() ==
                    "Marking the target project as a template as the final import stage...");
            Assert.Contains(
                root.GetProperty("progress").EnumerateArray(),
                entry => entry.GetProperty("message").GetString()!.StartsWith(
                    "error: failed to restore the target project's template state:",
                    StringComparison.Ordinal));
            var cleanupFailure = Assert.Single(root.GetProperty("cleanupFailures").EnumerateArray());
            Assert.Equal("restoring-template-state", cleanupFailure.GetProperty("stage").GetString());
            Assert.Equal(
                "GitHub GraphQL request failed (HTTP 200 OK, code FORBIDDEN, request ID unavailable, retries 0).",
                cleanupFailure.GetProperty("message").GetString());
            var exceptionDetails = root.GetProperty("exceptions").EnumerateArray().ToArray();
            Assert.Equal(3, exceptionDetails.Length);
            Assert.Equal("System.AggregateException", exceptionDetails[0].GetProperty("type").GetString());
            Assert.All(
                exceptionDetails[1..],
                exceptionDetail => Assert.Equal(
                    "Ghpmv.Core.GitHub.GitHubGraphQLException",
                    exceptionDetail.GetProperty("type").GetString()));
            Assert.Contains(
                "Multiple related failures occurred",
                exceptionDetails[0].GetProperty("message").GetString(),
                StringComparison.Ordinal);
            Assert.All(
                exceptionDetails[1..],
                exceptionDetail =>
                {
                    Assert.Equal(
                        "GitHub GraphQL request failed (HTTP 200 OK, code FORBIDDEN, request ID unavailable, retries 0).",
                        exceptionDetail.GetProperty("message").GetString());
                    Assert.False(string.IsNullOrWhiteSpace(
                        exceptionDetail.GetProperty("stackTrace").GetString()));
                    Assert.Single(exceptionDetail.GetProperty("graphQlErrors").EnumerateArray());
                });

            // The finally-path retry reports the dedicated restore diagnostic.
            Assert.Contains(
                "error: failed to restore the target project's template state:",
                result.Error,
                StringComparison.Ordinal);

            // Both restore attempts are mark mutations; nothing else follows them.
            Assert.Equal(2, server.RequestBodies.Count(IsMarkTemplateMutation));
            Assert.True(IsMarkTemplateMutation(server.RequestBodies[^1]));
            var importLog = await ImportLog.LoadAsync(directory, cancellationToken);
            Assert.NotNull(importLog);
            Assert.Single(importLog.StatusUpdates);
            Assert.True(importLog.TemplateRestorationRequired);

            // The stdout contract is never partially emitted: the failure happens before
            // the summary block, so no result/items/status-updates/views line is printed.
            Assert.Equal(string.Empty, result.Output);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_restores_a_pending_template_even_when_project_resume_fails_first()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-template-early-fail-" + Guid.NewGuid().ToString("N"));
        var snapshot = SnapshotWithStatusUpdates();
        await SnapshotFile.SaveAsync(snapshot, directory, cancellationToken);
        await new ImportLog
        {
            ProjectId = "PVT_existing",
            SourceSnapshotFingerprint = ImportLog.ComputeSnapshotFingerprint(snapshot),
            TemplateRestorationRequired = true,
        }.SaveAsync(directory, cancellationToken);

        using var server = new GraphQlStubServer(
            NonTemplateProjectResponse,
            TemplateMutationErrorResponse,
            MarkTemplateResponse);
        try
        {
            var result = await RunCliAsync(directory, server);

            Assert.Equal(1, result.ExitCode);
            Assert.Single(server.RequestBodies, IsMarkTemplateMutation);
            Assert.DoesNotContain(server.RequestBodies, IsUnmarkTemplateMutation);
            Assert.True(IsMarkTemplateMutation(server.RequestBodies[^1]));

            var importLog = await ImportLog.LoadAsync(directory, cancellationToken);
            Assert.NotNull(importLog);
            Assert.False(importLog.TemplateRestorationRequired);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static bool IsMarkTemplateMutation(string requestBody)
        => requestBody.Contains("markProjectV2AsTemplate", StringComparison.Ordinal)
            && !requestBody.Contains("unmarkProjectV2AsTemplate", StringComparison.Ordinal);

    private static bool IsUnmarkTemplateMutation(string requestBody)
        => requestBody.Contains("unmarkProjectV2AsTemplate", StringComparison.Ordinal);

    private static async Task<(int ExitCode, string Output, string Error)> RunCliAsync(
        string directory,
        GraphQlStubServer server,
        params string[] additionalArguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ghpmv.dll"));
        foreach (var argument in new[]
        {
            "import",
            "--org", "target",
            "--in", directory,
            "--token", "dummy-token",
            "--target-base-url", server.GraphQlUrl,
            "--no-update-check",
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var argument in additionalArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the ghpmv process.");
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        return (process.ExitCode, await output, await error);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunVerifyCliAsync(
        string directory,
        GraphQlStubServer server,
        params string[] additionalArguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[]
        {
            Path.Combine(AppContext.BaseDirectory, "ghpmv.dll"),
            "verify",
            "--org", "target",
            "--project", "42",
            "--in", directory,
            "--token", "dummy-token",
            "--target-base-url", server.GraphQlUrl,
            "--no-update-check",
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var argument in additionalArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the ghpmv process.");
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        return (process.ExitCode, await output, await error);
    }

    private static ProjectSnapshot MinimalSnapshot() => new()
    {
        SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
        StatusUpdates = [],
        LinkedRepositories = [],
        LinkedTeams = [],
        Project = new ProjectInfoSnapshot
        {
            Title = "Roadmap",
            Public = false,
            Closed = false,
            Template = false,
        },
        Fields = [],
        Views = [],
        Workflows = [],
        Items = [],
    };

    private static ProjectSnapshot SnapshotWithDownstreamContent() => new()
    {
        SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
        StatusUpdates = [],
        LinkedRepositories = [],
        LinkedTeams = [],
        Project = new ProjectInfoSnapshot
        {
            Title = "Roadmap",
            Public = false,
            Closed = false,
            Template = false,
        },
        Fields = [],
        Views =
        [
            new ViewSnapshot
            {
                Number = 1,
                Name = "Table",
                Layout = "TABLE_LAYOUT",
                Filter = "assignee:old-user",
                GroupByFields = [],
                SortByFields = [],
                VerticalGroupByFields = [],
                VisibleFields = [],
            },
        ],
        Workflows =
        [
            new WorkflowSnapshot
            {
                Number = 1,
                Name = "Item added to project",
                Enabled = true,
            },
        ],
        Items =
        [
            new ItemSnapshot
            {
                Type = "DRAFT_ISSUE",
                Position = 0,
                IsArchived = false,
                Draft = new DraftIssueSnapshot
                {
                    Title = "Must not be imported",
                    Assignees = [],
                },
                FieldValues = [],
            },
        ],
    };

    private static ProjectSnapshot VerifySnapshot() => MinimalSnapshot() with
    {
        Collaborators = null,
        LinkedRepositories = [],
        LinkedTeams = [],
    };

    private static ProjectSnapshot SnapshotWithStatusUpdates() => MinimalSnapshot() with
    {
        StatusUpdates =
        [
            new StatusUpdateSnapshot
            {
                Body = "Kickoff complete.",
                Status = "ON_TRACK",
                StartDate = "2024-01-01",
                TargetDate = "2024-03-31",
                Creator = "octocat",
                CreatedAt = "2024-01-05T09:00:00Z",
                UpdatedAt = "2024-01-06T09:00:00Z",
            },
        ],
    };

    private static ProjectSnapshot SnapshotWithTemplate(bool template)
    {
        var snapshot = MinimalSnapshot();
        return snapshot with { Project = snapshot.Project with { Template = template } };
    }

    private const string ExistingProjectResponse =
        """
        {"data":{"organization":{"projectsV2":{
          "nodes":[{"id":"PVT_existing","number":42,"title":"Roadmap","url":"https://github.com/orgs/target/projects/42"}],
          "pageInfo":{"hasNextPage":false,"endCursor":null}
        }}}}
        """;

    private const string EmptyProjectsResponse =
        """
        {"data":{"organization":{"projectsV2":{
          "nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}
        }}}}
        """;

    private const string OwnerResponse =
        """{"data":{"organization":{"id":"O_target"}}}""";

    private const string CreateProjectResponse =
        """{"data":{"createProjectV2":{"projectV2":{"id":"PVT_created","number":42,"title":"Roadmap","url":"https://github.com/orgs/target/projects/42","public":false}}}}""";

    private const string CreatedProjectLookupResponse =
        """
        {"data":{"organization":{"projectsV2":{
          "nodes":[{"id":"PVT_created","number":42,"title":"Roadmap","url":"https://github.com/orgs/target/projects/42"}],
          "pageInfo":{"hasNextPage":false,"endCursor":null}
        }}}}
        """;

    private const string UpdateCreatedProjectResponse =
        """{"data":{"updateProjectV2":{"projectV2":{"id":"PVT_created"}}}}""";

    private const string UpdateProjectResponse =
        """{"data":{"updateProjectV2":{"projectV2":{"id":"PVT_existing"}}}}""";

    private const string EmptyFieldsResponse =
        """{"data":{"node":{"fields":{"nodes":[]}}}}""";

    private const string PositionResponse =
        """{"data":{"updateProjectV2ItemPosition":{"clientMutationId":"position"}}}""";

    private const string TemplateProjectResponse =
        """{"data":{"node":{"id":"PVT_existing","template":true}}}""";

    private const string NonTemplateProjectResponse =
        """{"data":{"node":{"id":"PVT_existing","template":false}}}""";

    private const string UnmarkTemplateResponse =
        """{"data":{"unmarkProjectV2AsTemplate":{"projectV2":{"id":"PVT_existing","template":false}}}}""";

    private const string MarkTemplateResponse =
        """{"data":{"markProjectV2AsTemplate":{"projectV2":{"id":"PVT_existing","template":true}}}}""";

    private const string TemplateMutationErrorResponse =
        """{"errors":[{"type":"FORBIDDEN","message":"Template restore is not permitted."}]}""";

    private const string CreateStatusUpdateResponse =
        """{"data":{"createProjectV2StatusUpdate":{"statusUpdate":{"id":"PVTSU_imported"}}}}""";

    private const string VerifyProjectResponse =
        """
        {"data":{"organization":{"projectV2":{
          "title":"Roadmap","shortDescription":null,"readme":null,"public":false,"closed":false,"template":false,
          "views":{"nodes":[]},"workflows":{"nodes":[]},
          "repositories":{"nodes":[{"nameWithOwner":"target/extra"}]}
        }}}}
        """;

    private const string VerifyItemsResponse =
        """
        {"data":{"organization":{"projectV2":{
          "items":{"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}
        }}}}
        """;

    private const string VerifyFieldsResponse =
        """
        {"data":{"organization":{"projectV2":{
          "fields":{"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}
        }}}}
        """;

    private const string VerifyStatusUpdatesResponse =
        """
        {"data":{"organization":{"projectV2":{
          "statusUpdates":{"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}
        }}}}
        """;

    private const string VerifyTeamsResponse =
        """{"data":{"organization":{"projectV2":{"teams":{"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}}""";

    private sealed class GraphQlStubServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cancellation = new();
        private readonly string[] _responses;
        private readonly Task _serverTask;

        public GraphQlStubServer(params string[] responses)
        {
            _responses = responses;
            using var portReservation = new TcpListener(IPAddress.Loopback, 0);
            portReservation.Start();
            var port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
            portReservation.Stop();

            var prefix = $"http://127.0.0.1:{port}/";
            GraphQlUrl = prefix + "graphql";
            _listener.Prefixes.Add(prefix);
            _listener.Start();
            _serverTask = ServeAsync(_cancellation.Token);
        }

        public string GraphQlUrl { get; }
        public int[]? ResponseStatusCodes { get; init; }

        public List<string> RequestBodies { get; } = [];

        public void Dispose()
        {
            _cancellation.Cancel();
            _listener.Close();
            try
            {
                _serverTask.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }

            _cancellation.Dispose();
        }

        private async Task ServeAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().WaitAsync(cancellationToken);
                }
                catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                RequestBodies.Add(await reader.ReadToEndAsync(cancellationToken));

                var responseIndex = Math.Min(RequestBodies.Count - 1, _responses.Length - 1);
                var response = Encoding.UTF8.GetBytes(_responses[responseIndex]);
                context.Response.ContentType = "application/json";
                if (ResponseStatusCodes is { } statuses) context.Response.StatusCode = statuses[Math.Min(responseIndex, statuses.Length - 1)];
                context.Response.ContentLength64 = response.Length;
                await context.Response.OutputStream.WriteAsync(response, cancellationToken);
                context.Response.Close();
            }
        }
    }
}
