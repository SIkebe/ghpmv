using Ghpmv.Core.Browser;

namespace Ghpmv.Browser.Tests;

/// <summary>Storage-state path resolution for cross-account browser profiles.</summary>
public class BrowserProfileTests
{
    [Fact]
    public void Profile_maps_to_a_dedicated_state_file()
    {
        var path = BrowserSession.DefaultStatePath("source");

        Assert.EndsWith(Path.Combine("ghpmv", "browser-state.source.json"), path, StringComparison.Ordinal);
    }

    [Fact]
    public void Profiles_do_not_collide()
    {
        Assert.NotEqual(BrowserSession.DefaultStatePath("source"), BrowserSession.DefaultStatePath("target"));
    }

    [Fact]
    public void Explicit_state_path_wins_over_profile()
    {
        var session = new BrowserSession(new BrowserSessionOptions
        {
            StatePath = "C:/tmp/custom.json",
            Profile = "source",
        });

        Assert.Equal("C:/tmp/custom.json", session.StatePath);
    }

    [Fact]
    public async Task Login_does_not_load_existing_profile_state()
    {
        var path = Path.GetTempFileName();
        try
        {
            bool? loadStoredState = null;
            await using var session = new BrowserSession(
                new BrowserSessionOptions { StatePath = path },
                (loadState, _) =>
                {
                    loadStoredState = loadState;
                    return Task.FromResult(SignedInOperations("source-user"));
                },
                _ => Task.CompletedTask);

            var login = await session.LoginAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

            Assert.Equal("source-user", login);
            Assert.False(loadStoredState);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Expected_login_rejects_a_different_account_without_saving_state()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, "existing state", TestContext.Current.CancellationToken);
        var saveCount = 0;
        try
        {
            await using var session = new BrowserSession(
                new BrowserSessionOptions { StatePath = path },
                (_, _) => Task.FromResult(SignedInOperations("previous-user")),
                _ =>
                {
                    saveCount++;
                    return Task.CompletedTask;
                });

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.LoginAsAsync(
                    TimeSpan.FromSeconds(1),
                    "source-user",
                    TestContext.Current.CancellationToken));

            Assert.Contains("browser state was not saved", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, saveCount);
            Assert.Equal(
                "existing state",
                await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Expected_login_is_case_insensitive()
        => BrowserSession.EnsureExpectedLogin("Source-User", "source-user");

    private static BrowserSession.BrowserLoginOperations SignedInOperations(string login)
        => new(
            _ => Task.CompletedTask,
            () => Task.FromResult<string?>(login),
            () => Task.FromResult(false));
}
