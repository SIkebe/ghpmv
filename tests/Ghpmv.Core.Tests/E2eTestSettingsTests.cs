using Ghpmv.TestSupport;
using System.Text.Json;

namespace Ghpmv.Core.Tests;

public class E2eTestSettingsTests
{
    [Fact]
    public void Load_accepts_comments_trailing_commas_gei_and_user_mappings()
    {
        var path = WriteSettings(
            """
            {
              // JSONC comments keep the checked-in settings understandable.
              "schemaVersion": 1,
              "source": {
                "organization": "source-org",
                "apiBaseUrl": "https://api.github.com/graphql",
                "webBaseUrl": "https://github.com",
                "uploadsBaseUrl": null,
                "browserProfile": "source",
                "browserStateEnvironmentVariable": "SOURCE_STATE",
                "tokenEnvironmentVariable": "SOURCE_TOKEN",
              },
              "target": {
                "organization": "target-org",
                "apiBaseUrl": "https://api.example.ghe.com/graphql",
                "webBaseUrl": "https://example.ghe.com",
                "uploadsBaseUrl": "https://uploads.example.ghe.com",
                "browserProfile": "target",
                "browserStateEnvironmentVariable": "TARGET_STATE",
                "tokenEnvironmentVariable": "TARGET_TOKEN"
              },
              "fixtures": {
                "integration": {
                  "projectNumber": 89,
                  "sourceRepository": "api-source",
                  "targetRepository": "api-target"
                },
                "browser": {
                  "projectNumber": 3,
                  "sourceRepository": "browser-source",
                  "targetRepository": "browser-target"
                }
              },
              "users": {
                "sourceBrowserLogin": "octocat",
                "targetBrowserLogin": "octocat_contoso",
                "collaboratorLogin": "hubot",
                "mappings": [
                  { "sourceLogin": "octocat", "targetLogin": "octocat_contoso" }
                ]
              },
              "gei": {
                "sourceRepository": "gei-source",
                "targetRepository": "gei-target",
                "targetRepositoryVisibility": "private",
                "sourceTokenEnvironmentVariable": "GEI_SOURCE_TOKEN",
                "targetTokenEnvironmentVariable": "GEI_TARGET_TOKEN",
                "sourceTokenOwnerLogin": "source-owner",
                "targetTokenOwnerLogin": "target-owner",
                "sourceRole": "migrator-active",
                "targetRole": "owner"
              },
              "execution": {
                "fixturePreparation": "existing",
                "repositoryPreparationMode": "gei",
                "reuseSourceFixture": true,
                "createTemporaryTargetProject": true
              }
            }
            """);

        try
        {
            var result = E2eTestSettings.Load(path);

            Assert.Equal("example.ghe.com", new Uri(result.Target.WebBaseUrl).Host);
            Assert.Equal("gei-target", result.Gei.TargetRepository);
            Assert.Equal("migrator-active", result.Gei.SourceRole);
            Assert.Equal("octocat_contoso", result.Users.ToMappingDictionary()["octocat"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_rejects_token_values_in_environment_variable_fields()
    {
        var settings = new E2eTestSettings
        {
            Source = new E2eEndpointSettings
            {
                Organization = "source-org",
                TokenEnvironmentVariable = "github_pat_11AA22BB33CC44DD55EE66FF77GG88HH99II00JJ",
            },
        };

        var exception = Assert.Throws<InvalidDataException>(() => settings.Validate());

        Assert.Contains("environment variable name, not a token value", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_rejects_missing_required_sections_instead_of_using_live_defaults()
    {
        var path = WriteSettings("""{ "schemaVersion": 1 }""");

        try
        {
            var exception = Assert.Throws<InvalidDataException>(() => E2eTestSettings.Load(path));

            Assert.Contains("missing required property 'source'", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Load_rejects_json_null_for_non_nullable_settings()
        {
            var json = JsonSerializer.Serialize(
                new E2eTestSettings(),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            json = json.Replace(
                "\"sourceBrowserLogin\":\"\"",
                "\"sourceBrowserLogin\":null",
                StringComparison.Ordinal);
            var path = WriteSettings(json);

            try
            {
                Assert.Throws<JsonException>(() => E2eTestSettings.Load(path));
            }
            finally
            {
                File.Delete(path);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Validate_rejects_duplicate_source_user_mappings()
    {
        var settings = new E2eTestSettings
        {
            Users = new E2eUserSettings
            {
                Mappings =
                [
                    new E2eUserMapping { SourceLogin = "octocat", TargetLogin = "octocat_one" },
                    new E2eUserMapping { SourceLogin = "OCTOCAT", TargetLogin = "octocat_two" },
                ],
            },
        };

        var exception = Assert.Throws<InvalidDataException>(() => settings.Validate());

        Assert.Contains("duplicate source login", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_rejects_empty_live_resource_identifiers()
    {
        var emptyProfile = new E2eTestSettings
        {
            Source = new E2eEndpointSettings
            {
                Organization = "source-org",
                BrowserProfile = "",
            },
        };
        var emptyGeiRepository = new E2eTestSettings
        {
            Gei = new E2eGeiSettings { SourceRepository = "" },
        };
        var emptyCollaborator = new E2eTestSettings
        {
            Users = new E2eUserSettings { CollaboratorLogin = "" },
        };

        Assert.Contains(
            "source.browserProfile is required",
            Assert.Throws<InvalidDataException>(() => emptyProfile.Validate()).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "gei.sourceRepository and gei.targetRepository are required",
            Assert.Throws<InvalidDataException>(() => emptyGeiRepository.Validate()).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "users.collaboratorLogin is required",
            Assert.Throws<InvalidDataException>(() => emptyCollaborator.Validate()).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_rejects_mismatched_deployment_urls_and_shared_cross_deployment_state()
    {
        var mismatchedWebHost = new E2eTestSettings
        {
            Target = new E2eEndpointSettings
            {
                Organization = "target-org",
                ApiBaseUrl = "https://api.example.ghe.com/graphql",
                WebBaseUrl = "https://github.com",
                BrowserProfile = "target",
                BrowserStateEnvironmentVariable = "TARGET_STATE",
                TokenEnvironmentVariable = "TARGET_TOKEN",
            },
        };
        var sharedCrossDeploymentCredentials = new E2eTestSettings
        {
            Target = new E2eEndpointSettings
            {
                Organization = "target-org",
                ApiBaseUrl = "https://api.example.ghe.com/graphql",
                WebBaseUrl = "https://example.ghe.com",
                UploadsBaseUrl = "https://uploads.example.ghe.com",
                BrowserProfile = "target",
                BrowserStateEnvironmentVariable = "GHPMV_SOURCE_BROWSER_STATE",
                TokenEnvironmentVariable = "GHPMV_SOURCE_TOKEN",
            },
        };
        var nonGitHubApiHost = new E2eTestSettings
        {
            Source = new E2eEndpointSettings
            {
                Organization = "source-org",
                ApiBaseUrl = "https://example.com/graphql",
                WebBaseUrl = "https://github.com",
                BrowserProfile = "source",
                BrowserStateEnvironmentVariable = "SOURCE_STATE",
                TokenEnvironmentVariable = "SOURCE_TOKEN",
            },
        };
        var nonDefaultApiPort = new E2eTestSettings
        {
            Source = new E2eEndpointSettings
            {
                Organization = "source-org",
                ApiBaseUrl = "https://api.github.com:8443/graphql",
                WebBaseUrl = "https://github.com:8443",
                BrowserProfile = "source",
                BrowserStateEnvironmentVariable = "SOURCE_STATE",
                TokenEnvironmentVariable = "SOURCE_TOKEN",
            },
        };

        Assert.Contains(
            "target.webBaseUrl must be the origin https://example.ghe.com",
            Assert.Throws<InvalidDataException>(() => mismatchedWebHost.Validate()).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "require different token environment variables",
            Assert.Throws<InvalidDataException>(() => sharedCrossDeploymentCredentials.Validate()).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "apiBaseUrl must use api.github.com or api.<tenant>.ghe.com",
            Assert.Throws<InvalidDataException>(() => nonGitHubApiHost.Validate()).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "apiBaseUrl must be a GitHub API origin",
            Assert.Throws<InvalidDataException>(() => nonDefaultApiPort.Validate()).Message,
            StringComparison.Ordinal);
    }

    private static string WriteSettings(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ghpmv-e2e-settings-{Guid.NewGuid():N}.jsonc");
        File.WriteAllText(path, content);
        return path;
    }
}
