using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Ghpmv.TestSupport;

public sealed partial record E2eTestSettings
{
    public const string SettingsPathEnvironmentVariable = "GHPMV_E2E_SETTINGS";

    [JsonPropertyName("$schema")]
    public string? Schema { get; init; }

    public int SchemaVersion { get; init; } = 1;

    public E2eEndpointSettings Source { get; init; } = new()
    {
        Organization = "gpm-source",
        BrowserProfile = "source",
        BrowserStateEnvironmentVariable = "GHPMV_SOURCE_BROWSER_STATE",
        TokenEnvironmentVariable = "GHPMV_SOURCE_TOKEN",
    };

    public E2eEndpointSettings Target { get; init; } = new()
    {
        Organization = "gpm-target",
        BrowserProfile = "target",
        BrowserStateEnvironmentVariable = "GHPMV_TARGET_BROWSER_STATE",
        TokenEnvironmentVariable = "GHPMV_TARGET_TOKEN",
    };

    public E2eFixtureSettings Fixtures { get; init; } = new();

    public E2eUserSettings Users { get; init; } = new();

    public E2eGeiSettings Gei { get; init; } = new();

    public E2eExecutionSettings Execution { get; init; } = new();

    public static E2eTestSettings Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"E2E settings file was not found: {fullPath}", fullPath);
        }

        var json = File.ReadAllText(fullPath);
        ValidateRequiredJsonProperties(json, fullPath);
        var settings = JsonSerializer.Deserialize(json, E2eTestSettingsJsonContext.Default.E2eTestSettings)
            ?? throw new JsonException($"E2E settings file '{fullPath}' contained JSON null.");
        settings.Validate(fullPath);
        return settings;
    }

    public static E2eTestSettings LoadDefault()
    {
        var explicitPath = Environment.GetEnvironmentVariable(SettingsPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return Load(explicitPath);
        }

        foreach (var directory in CandidateDirectories())
        {
            var localPath = Path.Combine(directory, "tests", "e2e.settings.local.jsonc");
            if (File.Exists(localPath))
            {
                return Load(localPath);
            }

            var sharedPath = Path.Combine(directory, "tests", "e2e.settings.jsonc");
            if (File.Exists(sharedPath))
            {
                return Load(sharedPath);
            }
        }

        throw new FileNotFoundException(
            $"Could not find tests{Path.DirectorySeparatorChar}e2e.settings.jsonc. "
            + $"Set {SettingsPathEnvironmentVariable} to an explicit JSONC file path.");
    }

    public void Validate(string sourceName = "E2E settings")
    {
        if (SchemaVersion != 1)
        {
            throw new InvalidDataException(
                $"{sourceName}: schemaVersion must be 1, but was {SchemaVersion.ToString(CultureInfo.InvariantCulture)}.");
        }

        ValidateEndpoint(Source, "source", sourceName);
        ValidateEndpoint(Target, "target", sourceName);
        ValidateFixture(Fixtures.Integration, "fixtures.integration", sourceName);
        ValidateFixture(Fixtures.Browser, "fixtures.browser", sourceName);
        ValidateEnvironmentVariable(Gei.SourceTokenEnvironmentVariable, "gei.sourceTokenEnvironmentVariable", sourceName);
        ValidateEnvironmentVariable(Gei.TargetTokenEnvironmentVariable, "gei.targetTokenEnvironmentVariable", sourceName);

        if (Gei.TargetRepositoryVisibility is not ("private" or "internal" or "public"))
        {
            throw new InvalidDataException(
                $"{sourceName}: gei.targetRepositoryVisibility must be private, internal, or public.");
        }

        if (Gei.SourceRole is not ("owner" or "migrator-active" or "migrator-pending"))
        {
            throw new InvalidDataException(
                $"{sourceName}: gei.sourceRole must be owner, migrator-active, or migrator-pending.");
        }

        if (Gei.TargetRole is not ("owner" or "migrator-active" or "migrator-pending"))
        {
            throw new InvalidDataException(
                $"{sourceName}: gei.targetRole must be owner, migrator-active, or migrator-pending.");
        }

        if (Execution.FixturePreparation is not ("existing" or "create"))
        {
            throw new InvalidDataException(
                $"{sourceName}: execution.fixturePreparation must be existing or create.");
        }

        if (Execution.RepositoryPreparationMode is not ("gei" or "fixture-seed"))
        {
            throw new InvalidDataException(
                $"{sourceName}: execution.repositoryPreparationMode must be gei or fixture-seed.");
        }

        var sourceLogins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in Users.Mappings)
        {
            if (string.IsNullOrWhiteSpace(mapping.SourceLogin) || string.IsNullOrWhiteSpace(mapping.TargetLogin))
            {
                throw new InvalidDataException(
                    $"{sourceName}: every users.mappings entry requires non-empty sourceLogin and targetLogin.");
            }

            if (!sourceLogins.Add(mapping.SourceLogin))
            {
                throw new InvalidDataException(
                    $"{sourceName}: users.mappings contains duplicate source login '{mapping.SourceLogin}'.");
            }
        }
    }

    private static void ValidateEndpoint(E2eEndpointSettings endpoint, string propertyName, string sourceName)
    {
        if (string.IsNullOrWhiteSpace(endpoint.Organization))
        {
            throw new InvalidDataException($"{sourceName}: {propertyName}.organization is required.");
        }

        ValidateAbsoluteHttpsUrl(endpoint.ApiBaseUrl, $"{propertyName}.apiBaseUrl", sourceName);
        ValidateAbsoluteHttpsUrl(endpoint.WebBaseUrl, $"{propertyName}.webBaseUrl", sourceName);
        if (endpoint.UploadsBaseUrl is not null)
        {
            ValidateAbsoluteHttpsUrl(endpoint.UploadsBaseUrl, $"{propertyName}.uploadsBaseUrl", sourceName);
        }

        ValidateEnvironmentVariable(
            endpoint.BrowserStateEnvironmentVariable,
            $"{propertyName}.browserStateEnvironmentVariable",
            sourceName);
        ValidateEnvironmentVariable(
            endpoint.TokenEnvironmentVariable,
            $"{propertyName}.tokenEnvironmentVariable",
            sourceName);
    }

    private static void ValidateFixture(E2eFixtureDefinition fixture, string propertyName, string sourceName)
    {
        if (fixture.ProjectNumber <= 0)
        {
            throw new InvalidDataException($"{sourceName}: {propertyName}.projectNumber must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(fixture.SourceRepository)
            || string.IsNullOrWhiteSpace(fixture.TargetRepository))
        {
            throw new InvalidDataException(
                $"{sourceName}: {propertyName}.sourceRepository and targetRepository are required.");
        }
    }

    private static void ValidateAbsoluteHttpsUrl(string value, string propertyName, string sourceName)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException($"{sourceName}: {propertyName} must be an absolute HTTPS URL.");
        }
    }

    private static void ValidateEnvironmentVariable(string value, string propertyName, string sourceName)
    {
        if (!EnvironmentVariableName().IsMatch(value) || GitHubTokenPrefix().IsMatch(value))
        {
            throw new InvalidDataException(
                $"{sourceName}: {propertyName} must contain an environment variable name, not a token value.");
        }
    }

    private static void ValidateRequiredJsonProperties(string json, string sourceName)
    {
        using var document = JsonDocument.Parse(
            json,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        var root = RequireObject(document.RootElement, sourceName);
        RequireProperties(
            root,
            sourceName,
            "schemaVersion",
            "source",
            "target",
            "fixtures",
            "users",
            "gei",
            "execution");
        RequireProperties(
            RequireObject(root.GetProperty("source"), $"{sourceName}: source"),
            $"{sourceName}: source",
            "organization",
            "apiBaseUrl",
            "webBaseUrl",
            "uploadsBaseUrl",
            "browserProfile",
            "browserStateEnvironmentVariable",
            "tokenEnvironmentVariable");
        RequireProperties(
            RequireObject(root.GetProperty("target"), $"{sourceName}: target"),
            $"{sourceName}: target",
            "organization",
            "apiBaseUrl",
            "webBaseUrl",
            "uploadsBaseUrl",
            "browserProfile",
            "browserStateEnvironmentVariable",
            "tokenEnvironmentVariable");

        var fixtures = RequireObject(root.GetProperty("fixtures"), $"{sourceName}: fixtures");
        RequireProperties(fixtures, $"{sourceName}: fixtures", "integration", "browser");
        foreach (var fixtureName in new[] { "integration", "browser" })
        {
            RequireProperties(
                RequireObject(fixtures.GetProperty(fixtureName), $"{sourceName}: fixtures.{fixtureName}"),
                $"{sourceName}: fixtures.{fixtureName}",
                "projectNumber",
                "sourceRepository",
                "targetRepository");
        }

        var users = RequireObject(root.GetProperty("users"), $"{sourceName}: users");
        RequireProperties(
            users,
            $"{sourceName}: users",
            "sourceBrowserLogin",
            "targetBrowserLogin",
            "collaboratorLogin",
            "mappings");
        if (users.GetProperty("mappings").ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"{sourceName}: users.mappings must be an array.");
        }

        foreach (var (mapping, index) in users.GetProperty("mappings").EnumerateArray().Select((value, index) => (value, index)))
        {
            RequireProperties(
                RequireObject(mapping, $"{sourceName}: users.mappings[{index}]"),
                $"{sourceName}: users.mappings[{index}]",
                "sourceLogin",
                "targetLogin");
        }

        RequireProperties(
            RequireObject(root.GetProperty("gei"), $"{sourceName}: gei"),
            $"{sourceName}: gei",
            "sourceRepository",
            "targetRepository",
            "targetRepositoryVisibility",
            "sourceTokenEnvironmentVariable",
            "targetTokenEnvironmentVariable",
            "sourceTokenOwnerLogin",
            "targetTokenOwnerLogin",
            "sourceRole",
            "targetRole");
        RequireProperties(
            RequireObject(root.GetProperty("execution"), $"{sourceName}: execution"),
            $"{sourceName}: execution",
            "fixturePreparation",
            "repositoryPreparationMode",
            "reuseSourceFixture",
            "createTemporaryTargetProject");
    }

    private static JsonElement RequireObject(JsonElement value, string propertyName)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{propertyName} must be a JSON object.");
        }

        return value;
    }

    private static void RequireProperties(JsonElement value, string propertyName, params string[] requiredProperties)
    {
        foreach (var requiredProperty in requiredProperties)
        {
            if (!value.TryGetProperty(requiredProperty, out _))
            {
                throw new InvalidDataException($"{propertyName} is missing required property '{requiredProperty}'.");
            }
        }
    }

    private static IEnumerable<string> CandidateDirectories()
    {
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(Path.GetFullPath(start));
            while (directory is not null)
            {
                if (visited.Add(directory.FullName))
                {
                    yield return directory.FullName;
                }

                directory = directory.Parent;
            }
        }
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentVariableName();

    [GeneratedRegex("^(?:ghp|gho|ghu|ghs|ghr|github_pat)_", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubTokenPrefix();
}

public sealed record E2eEndpointSettings
{
    public string Organization { get; init; } = "";

    public string ApiBaseUrl { get; init; } = "https://api.github.com/graphql";

    public string WebBaseUrl { get; init; } = "https://github.com";

    public string? UploadsBaseUrl { get; init; }

    public string BrowserProfile { get; init; } = "";

    public string BrowserStateEnvironmentVariable { get; init; } = "GHPMV_BROWSER_STATE";

    public string TokenEnvironmentVariable { get; init; } = "GHPMV_TEST_TOKEN";
}

public sealed record E2eFixtureSettings
{
    public E2eFixtureDefinition Integration { get; init; } = new()
    {
        ProjectNumber = 89,
        SourceRepository = "fixture-repo2",
        TargetRepository = "fixture-repo",
    };

    public E2eFixtureDefinition Browser { get; init; } = new()
    {
        ProjectNumber = 3,
        SourceRepository = "fixture-repo",
        TargetRepository = "fixture-repo",
    };
}

public sealed record E2eFixtureDefinition
{
    public int ProjectNumber { get; init; }

    public string SourceRepository { get; init; } = "";

    public string TargetRepository { get; init; } = "";
}

public sealed record E2eUserSettings
{
    public string SourceBrowserLogin { get; init; } = "";

    public string TargetBrowserLogin { get; init; } = "";

    public string CollaboratorLogin { get; init; } = "ravel-maurice-uo_sde";

    public IReadOnlyList<E2eUserMapping> Mappings { get; init; } = [];

    public IReadOnlyDictionary<string, string> ToMappingDictionary()
        => Mappings.ToDictionary(
            mapping => mapping.SourceLogin,
            mapping => mapping.TargetLogin,
            StringComparer.OrdinalIgnoreCase);
}

public sealed record E2eUserMapping
{
    public string SourceLogin { get; init; } = "";

    public string TargetLogin { get; init; } = "";
}

public sealed record E2eGeiSettings
{
    public string SourceRepository { get; init; } = "fixture-repo";

    public string TargetRepository { get; init; } = "fixture-repo-gei-target";

    public string TargetRepositoryVisibility { get; init; } = "private";

    public string SourceTokenEnvironmentVariable { get; init; } = "GHPMV_GEI_SOURCE_TOKEN";

    public string TargetTokenEnvironmentVariable { get; init; } = "GHPMV_GEI_TARGET_TOKEN";

    public string SourceTokenOwnerLogin { get; init; } = "";

    public string TargetTokenOwnerLogin { get; init; } = "";

    public string SourceRole { get; init; } = "owner";

    public string TargetRole { get; init; } = "owner";
}

public sealed record E2eExecutionSettings
{
    public string FixturePreparation { get; init; } = "existing";

    public string RepositoryPreparationMode { get; init; } = "gei";

    public bool ReuseSourceFixture { get; init; } = true;

    public bool CreateTemporaryTargetProject { get; init; } = true;
}

public static class E2eTestEnvironment
{
    private static readonly Lazy<E2eTestSettings> Settings = new(E2eTestSettings.LoadDefault);

    public static E2eTestSettings Current => Settings.Value;

    public static string SourceOrganization =>
        FirstEnvironmentValue("GHPMV_TEST_ORG", "GHPMV_SOURCE_ORG") ?? Current.Source.Organization;

    public static string TargetOrganization =>
        FirstEnvironmentValue("GHPMV_TEST_TARGET_ORG", "GHPMV_TARGET_ORG") ?? Current.Target.Organization;

    public static int IntegrationProjectNumber =>
        ReadPositiveInteger("GHPMV_TEST_PROJECT_NUMBER", Current.Fixtures.Integration.ProjectNumber);

    public static int BrowserProjectNumber =>
        ReadPositiveInteger("GHPMV_TEST_PROJECT_NUMBER", Current.Fixtures.Browser.ProjectNumber);

    public static string IntegrationSourceRepository =>
        FirstEnvironmentValue("GHPMV_TEST_FIXTURE_REPO", "GHPMV_FIXTURE_REPO")
        ?? Current.Fixtures.Integration.SourceRepository;

    public static string IntegrationTargetRepository =>
        FirstEnvironmentValue("GHPMV_TEST_TARGET_FIXTURE_REPO")
        ?? Current.Fixtures.Integration.TargetRepository;

    public static string BrowserSourceRepository =>
        FirstEnvironmentValue("GHPMV_TEST_FIXTURE_REPO", "GHPMV_FIXTURE_REPO")
        ?? Current.Fixtures.Browser.SourceRepository;

    public static string BrowserTargetRepository =>
        FirstEnvironmentValue("GHPMV_TEST_TARGET_FIXTURE_REPO")
        ?? Current.Fixtures.Browser.TargetRepository;

    public static string CollaboratorLogin =>
        FirstEnvironmentValue("GHPMV_TEST_COLLABORATOR_LOGIN") ?? Current.Users.CollaboratorLogin;

    public static string? SourceBrowserStatePath =>
        FirstEnvironmentValue(Current.Source.BrowserStateEnvironmentVariable, "GHPMV_BROWSER_STATE");

    public static string? TargetBrowserStatePath =>
        FirstEnvironmentValue(Current.Target.BrowserStateEnvironmentVariable, "GHPMV_BROWSER_STATE");

    public static string? SourceToken =>
        FirstEnvironmentValue(Current.Source.TokenEnvironmentVariable, "GHPMV_TEST_TOKEN");

    public static string? TargetToken =>
        FirstEnvironmentValue(Current.Target.TokenEnvironmentVariable, "GHPMV_TEST_TOKEN");

    public static Uri IntegrationApiBaseUrl
    {
        get
        {
            var source = new Uri(Current.Source.ApiBaseUrl);
            var target = new Uri(Current.Target.ApiBaseUrl);
            if (!string.Equals(source.Scheme, target.Scheme, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(source.Host, target.Host, StringComparison.OrdinalIgnoreCase)
                || source.Port != target.Port)
            {
                throw new InvalidOperationException(
                    "The integration test suite uses one GHPMV_TEST_TOKEN and requires source and target "
                    + "to be on the same GitHub deployment. Use the browser/manual E2E flow for cross-deployment validation.");
            }

            return source;
        }
    }

    private static string? FirstEnvironmentValue(params string[] names)
    {
        foreach (var name in names.Where(name => !string.IsNullOrWhiteSpace(name)))
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static int ReadPositiveInteger(string variableName, int fallback)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) && result > 0)
        {
            return result;
        }

        throw new FormatException($"{variableName} must be a positive integer, but was '{value}'.");
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(E2eTestSettings))]
internal sealed partial class E2eTestSettingsJsonContext : JsonSerializerContext;
