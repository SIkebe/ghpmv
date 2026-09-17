using System.Globalization;
using System.Text.Json;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Import;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Core.Tests;

public sealed class IterationConfigurationInputTests
{
    [Fact]
    public void Uninitialized_configuration_is_sent_as_null_without_initializing_a_schedule()
    {
        using var client = new GitHubGraphQLClient("test-token");
        var progress = new List<string>();
        var importer = new ProjectImporter(client) { OperationLogDirectory = "unused", OnProgress = progress.Add };
        var configuration = Configuration(duration: 0, startDay: 0);

        Assert.Null(importer.BuildIterationConfigurationInput("Probe Sprint", configuration));
        Assert.Single(progress);
        Assert.Contains("preserving uninitialized", progress[0], StringComparison.Ordinal);
        Assert.Equal(0, configuration.Duration);
        Assert.Equal(0, configuration.StartDay);
        Assert.Empty(configuration.Iterations);
        Assert.Empty(configuration.CompletedIterations);
    }

    [Theory]
    [InlineData(1, "2026-09-14")]
    [InlineData(2, "2026-09-15")]
    [InlineData(3, "2026-09-16")]
    [InlineData(4, "2026-09-17")]
    [InlineData(5, "2026-09-11")]
    [InlineData(6, "2026-09-12")]
    [InlineData(7, "2026-09-13")]
    public void Empty_initialized_configuration_preserves_each_weekday(int startDay, string expectedDate)
    {
        using var client = new GitHubGraphQLClient("test-token");
        var importer = new ProjectImporter(client) { OperationLogDirectory = "unused" };

        var input = JsonSerializer.SerializeToElement(importer.BuildIterationConfigurationInput(
            "Probe Sprint", Configuration(14, startDay), new DateOnly(2026, 9, 17)));

        Assert.Equal(14, input.GetProperty("duration").GetInt32());
        Assert.Equal(expectedDate, input.GetProperty("startDate").GetString());
        Assert.Empty(input.GetProperty("iterations").EnumerateArray());
    }

    [Fact]
    public void Empty_schedule_weekday_alignment_handles_a_year_boundary()
    {
        using var client = new GitHubGraphQLClient("test-token");
        var importer = new ProjectImporter(client) { OperationLogDirectory = "unused" };
        var input = JsonSerializer.SerializeToElement(importer.BuildIterationConfigurationInput(
            "Probe Sprint", Configuration(7, 1), new DateOnly(2026, 1, 1)));
        Assert.Equal("2025-12-29", input.GetProperty("startDate").GetString());
    }

    [Fact]
    public void Active_and_completed_iterations_keep_dates_durations_and_breaks_in_chronological_order()
    {
        using var client = new GitHubGraphQLClient("test-token");
        var importer = new ProjectImporter(client) { OperationLogDirectory = "unused" };
        var configuration = Configuration(7, 1) with
        {
            Iterations = [Iteration("long", "2026-09-28", 21), Iteration("week", "2026-09-14", 7)],
            CompletedIterations = [Iteration("day", "2026-08-31", 1), Iteration("older", "2026-08-17", 3)],
        };
        var input = JsonSerializer.SerializeToElement(importer.BuildIterationConfigurationInput(
            "Probe Sprint", configuration, new DateOnly(2026, 9, 17)));
        Assert.Equal("2026-08-17", input.GetProperty("startDate").GetString());
        Assert.Equal(7, input.GetProperty("duration").GetInt32());
        var iterations = input.GetProperty("iterations").EnumerateArray().ToArray();
        Assert.Equal(["older", "day", "week", "long"], iterations.Select(iteration => iteration.GetProperty("title").GetString()));
        Assert.Equal([3, 1, 7, 21], iterations.Select(iteration => iteration.GetProperty("duration").GetInt32()));
        Assert.All(iterations, iteration => Assert.False(iteration.TryGetProperty("id", out _)));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(14, 0)]
    [InlineData(14, 8)]
    public void Invalid_empty_configuration_is_not_silently_treated_as_uninitialized(int duration, int startDay)
    {
        using var client = new GitHubGraphQLClient("test-token");
        var importer = new ProjectImporter(client) { OperationLogDirectory = "unused" };
        Assert.Throws<InvalidDataException>(() =>
            importer.BuildIterationConfigurationInput("Probe Sprint", Configuration(duration, startDay)));
    }

    [Fact]
    public void Completed_only_configuration_uses_the_original_past_start_date()
    {
        using var client = new GitHubGraphQLClient("test-token");
        var importer = new ProjectImporter(client) { OperationLogDirectory = "unused" };
        var configuration = Configuration(14, 1) with
        {
            CompletedIterations = [Iteration("past", "2026-08-17", 14)],
        };
        var input = JsonSerializer.SerializeToElement(importer.BuildIterationConfigurationInput(
            "Probe Sprint", configuration, new DateOnly(2026, 9, 17)));
        Assert.Equal("2026-08-17", input.GetProperty("startDate").GetString());
        Assert.Single(input.GetProperty("iterations").EnumerateArray());
    }

    private static IterationConfigurationSnapshot Configuration(int duration, int startDay) => new()
    {
        Duration = duration,
        StartDay = startDay,
        Iterations = [],
        CompletedIterations = [],
    };

    private static IterationSnapshot Iteration(string title, string date, int duration) => new()
    {
        Id = "synthetic-" + title,
        Title = title,
        StartDate = DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        Duration = duration,
    };
}
