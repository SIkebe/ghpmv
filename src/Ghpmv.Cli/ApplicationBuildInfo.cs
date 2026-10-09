using System.Reflection;

namespace Ghpmv.Cli;

internal sealed record ApplicationBuildInfo
{
    public required string? Version { get; init; }
    public required string? CommitSha { get; init; }
    public required bool? IsDirty { get; init; }

    public static ApplicationBuildInfo Current { get; } = ReadCurrent();

    private static ApplicationBuildInfo ReadCurrent()
    {
        var assembly = typeof(ApplicationBuildInfo).Assembly;
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToArray();
        return FromMetadata(
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            metadata.SingleOrDefault(attribute => attribute.Key == "CommitSha")?.Value,
            metadata.SingleOrDefault(attribute => attribute.Key == "IsDirty")?.Value);
    }

    internal static ApplicationBuildInfo FromMetadata(string? version, string? commitSha, string? isDirty)
    {
        var validCommit = commitSha is { Length: 40 or 64 } && commitSha.All(char.IsAsciiHexDigit);
        return new()
        {
            Version = version,
            CommitSha = validCommit ? commitSha : null,
            IsDirty = validCommit && bool.TryParse(isDirty, out var dirty) ? dirty : null,
        };
    }
}
