using Ghpmv.Core.Browser;

namespace Ghpmv.Browser.Tests;

public class ProjectFieldUiExporterTests
{
    [Fact]
    public void ParseCatalog_reads_hidden_fields_options_iterations_and_issue_field_links()
    {
        var catalog = ProjectFieldUiExporter.ParseCatalog(
            """
            [
              {
                "dataType":"title","id":"Title","name":"Title","position":1,
                "settings":null,"issueFieldId":null
              },
              {
                "dataType":"text","id":101,"name":"Hidden text","position":2,
                "settings":null,"issueFieldId":null
              },
              {
                "dataType":"singleSelect","id":102,"name":"Priority","position":3,
                "settings":{"options":[
                  {"id":"high","name":"High","color":"RED","description":"Urgent"}
                ]},"issueFieldId":null
              },
              {
                "dataType":"iteration","id":103,"name":"Sprint","position":4,
                "settings":{"configuration":{
                  "duration":14,"startDay":1,
                  "iterations":[{"id":"s2","title":"Sprint 2","startDate":"2026-07-27","duration":14}],
                  "completedIterations":[{"id":"s1","title":"Sprint 1","startDate":"2026-07-13","duration":14}]
                }},"issueFieldId":null
              },
              {
                "dataType":"text","id":104,"name":"Teams","position":5,
                "settings":null,"issueFieldId":null
              },
              {
                "dataType":"multiSelect","id":105,"name":"Teams","position":6,
                "settings":{"options":[
                  {"id":7,"name":"SDK","color":"GREEN","description":null}
                ]},"issueFieldId":44611488
              }
            ]
            """);

        Assert.Equal(["Title", "Hidden text", "Priority", "Sprint", "Teams", "Teams"], catalog.Fields.Select(field => field.Name));
        Assert.Equal("TEXT", catalog.Fields.Single(field => field.Name == "Hidden text").DataType);
        var priority = catalog.Fields.Single(field => field.Name == "Priority");
        Assert.Equal("SINGLE_SELECT", priority.DataType);
        Assert.Equal("Urgent", Assert.Single(priority.Options!).Description);
        var sprint = catalog.Fields.Single(field => field.Name == "Sprint").IterationConfiguration!;
        Assert.Equal(14, sprint.Duration);
        Assert.Equal("Sprint 2", Assert.Single(sprint.Iterations).Title);
        Assert.Equal("Sprint 1", Assert.Single(sprint.CompletedIterations).Title);
        var teams = catalog.Entries.Single(entry => entry.IsIssueField).Field;
        Assert.Equal("MULTI_SELECT", teams.DataType);
        Assert.Equal("7", Assert.Single(teams.Options!).Id);
        Assert.Single(catalog.Entries, entry => entry.Field.Name == "Teams" && !entry.IsIssueField);
    }

    [Fact]
    public void ParseCatalog_rejects_duplicate_field_identity()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProjectFieldUiExporter.ParseCatalog(
                """
                [
                  {"dataType":"text","id":1,"name":"Notes","position":1,"settings":null,"issueFieldId":null},
                  {"dataType":"number","id":2,"name":"Notes","position":2,"settings":null,"issueFieldId":null}
                ]
                """));

        Assert.Contains("duplicate field identity", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseCatalog_rejects_unknown_types_instead_of_omitting_fields()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProjectFieldUiExporter.ParseCatalog(
                """
                [{
                  "dataType":"futureType","id":1,"name":"Future","position":1,
                  "settings":null,"issueFieldId":null
                }]
                """));

        Assert.Contains("futureType", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseCatalog_rejects_unsupported_ordinary_multi_select_fields()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProjectFieldUiExporter.ParseCatalog(
                """
                [{
                  "dataType":"multiSelect","id":1,"name":"Teams","position":1,
                  "settings":{"options":[
                    {"id":"sdk","name":"SDK","color":"GREEN","description":null}
                  ]},"issueFieldId":null
                }]
                """));

        Assert.Contains("unsupported ordinary multi-select field 'Teams'", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void ParseCatalog_rejects_non_scalar_issue_field_ids(string issueFieldId)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProjectFieldUiExporter.ParseCatalog(
                $$"""
                [{
                  "dataType":"text","id":1,"name":"Notes","position":1,
                  "settings":null,"issueFieldId":{{issueFieldId}}
                }]
                """));

        Assert.Contains("issueFieldId", exception.Message, StringComparison.Ordinal);
        Assert.Contains("scalar ID", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseCatalog_rejects_missing_issue_field_id_members()
    {
        Assert.Throws<KeyNotFoundException>(() =>
            ProjectFieldUiExporter.ParseCatalog(
                """
                [{
                  "dataType":"text","id":1,"name":"Notes","position":1,
                  "settings":null
                }]
                """));
    }

    [Theory]
    [InlineData("false")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void ParseCatalog_rejects_non_string_option_descriptions(string description)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProjectFieldUiExporter.ParseCatalog(
                $$"""
                [{
                  "dataType":"singleSelect","id":1,"name":"Priority","position":1,
                  "settings":{"options":[{
                    "id":"high","name":"High","color":"RED","description":{{description}}
                  }]},"issueFieldId":null
                }]
                """));

        Assert.Contains("description", exception.Message, StringComparison.Ordinal);
        Assert.Contains("not a string", exception.Message, StringComparison.Ordinal);
    }
}
