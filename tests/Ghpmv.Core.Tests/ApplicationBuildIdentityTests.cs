using System.Diagnostics;

namespace Ghpmv.Core.Tests;

public sealed class ApplicationBuildIdentityTests
{
    [Fact]
    public async Task Build_metadata_tracks_clean_staged_unstaged_untracked_and_new_commits_incrementally()
    {
        using var fixture = await BuildFixture.CreateAsync();
        await fixture.GitAsync("init", "--quiet");
        await fixture.GitAsync("add", ".");
        await fixture.CommitAsync();
        var sha = (await fixture.GitAsync("rev-parse", "HEAD")).Trim();
        Assert.Equal(40, sha.Length);
        await fixture.AssertMetadataAsync(sha, false);

        await File.AppendAllTextAsync(fixture.InputPath, "// changed\n", TestContext.Current.CancellationToken);
        await fixture.AssertMetadataAsync(sha, true);
        await fixture.GitAsync("add", "input.cs");
        await fixture.AssertMetadataAsync(sha, true);
        await fixture.CommitAsync();
        var newSha = (await fixture.GitAsync("rev-parse", "HEAD")).Trim();
        Assert.NotEqual(sha, newSha);
        await fixture.AssertMetadataAsync(newSha, false);

        var untracked = Path.Combine(fixture.Root, "untracked.cs");
        await File.WriteAllTextAsync(untracked, "// new\n", TestContext.Current.CancellationToken);
        await fixture.AssertMetadataAsync(newSha, true);
        File.Delete(untracked);
        await fixture.AssertMetadataAsync(newSha, false);

        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "ignored.tmp"), "ignored", TestContext.Current.CancellationToken);
        await fixture.AssertMetadataAsync(newSha, false);
        await fixture.GitAsync("checkout", "--quiet", "--detach", newSha);
        await fixture.AssertMetadataAsync(newSha, false);
    }

    [Fact]
    public async Task Source_archive_does_not_borrow_parent_checkout_identity()
    {
        using var fixture = await BuildFixture.CreateAsync();
        await fixture.AssertMetadataAsync(null, null);
    }

    [Fact]
    public async Task Unborn_repository_does_not_claim_a_clean_build()
    {
        using var fixture = await BuildFixture.CreateAsync();
        await fixture.GitAsync("init", "--quiet");
        await fixture.AssertMetadataAsync(null, null);
    }

    [Fact]
    public async Task Worktree_git_file_is_supported()
    {
        using var fixture = await BuildFixture.CreateAsync();
        await fixture.GitAsync("init", "--quiet");
        await fixture.GitAsync("add", ".");
        await fixture.CommitAsync();
        var worktree = Path.Combine(fixture.Root, "linked-worktree");
        await fixture.GitAsync("worktree", "add", "--quiet", "--detach", worktree);
        var sha = (await fixture.GitAsync("rev-parse", "HEAD")).Trim();
        Assert.True(File.Exists(Path.Combine(worktree, ".git")));
        await fixture.AssertMetadataAsync(sha, false, worktree);
    }

    private sealed class BuildFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Environment.CurrentDirectory, "build-identity-" + Guid.NewGuid().ToString("N"));
        public string InputPath => Path.Combine(Root, "input.cs");

        public static async Task<BuildFixture> CreateAsync()
        {
            var fixture = new BuildFixture();
            var projectDirectory = Path.Combine(fixture.Root, "src", "Ghpmv.Cli");
            Directory.CreateDirectory(projectDirectory);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "BuildIdentity.targets"), Path.Combine(projectDirectory, "BuildIdentity.targets"));
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, "Identity.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <Version>1.2.3</Version>
                    <IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>
                    <ImportDirectoryBuildProps>false</ImportDirectoryBuildProps>
                    <ImportDirectoryBuildTargets>false</ImportDirectoryBuildTargets>
                  </PropertyGroup>
                  <Import Project="BuildIdentity.targets" />
                </Project>
                """, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(fixture.Root, ".gitignore"),
                "bin/\nobj/\n*.tmp\nlinked-worktree/\n", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(fixture.InputPath, "// fixture\n", TestContext.Current.CancellationToken);
            return fixture;
        }

        public Task<string> GitAsync(params string[] arguments) => RunAsync("git", Root, arguments);

        public Task<string> CommitAsync() =>
            GitAsync("-c", "user.name=Build identity test", "-c", "user.email=build-test@example.test",
                "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", "Synthetic build fixture");

        public async Task AssertMetadataAsync(string? sha, bool? dirty, string? root = null)
        {
            var projectDirectory = Path.Combine(root ?? Root, "src", "Ghpmv.Cli");
            await RunAsync("dotnet", projectDirectory,
                ["msbuild", "Identity.csproj", "-nologo", "-verbosity:quiet", "-t:GenerateAssemblyInfo"]);
            var source = await File.ReadAllTextAsync(
                Path.Combine(projectDirectory, "obj", "Debug", "net10.0", "Identity.AssemblyInfo.cs"),
                TestContext.Current.CancellationToken);
            Assert.Contains($"AssemblyMetadata(\"CommitSha\", \"{sha ?? ""}\")", source, StringComparison.Ordinal);
            Assert.Contains($"AssemblyMetadata(\"IsDirty\", \"{dirty?.ToString().ToLowerInvariant() ?? ""}\")", source, StringComparison.Ordinal);
            Assert.Contains("AssemblyInformationalVersionAttribute(\"1.2.3\")", source, StringComparison.Ordinal);
        }

        private static async Task<string> RunAsync(string executable, string directory, string[] arguments)
        {
            var startInfo = new ProcessStartInfo(executable)
            {
                WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {executable}.");
            var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            var stdout = await output;
            Assert.True(process.ExitCode == 0, $"{executable} exited with {process.ExitCode}:\n{stdout}\n{await error}");
            return stdout;
        }

        public void Dispose()
        {
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(Root, recursive: true);
        }
    }
}
