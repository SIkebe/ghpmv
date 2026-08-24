using System.Reflection;

namespace Ghpmv.Browser.Tests;

public class BrowserE2eArchitectureTests
{
    [Fact]
    public void Browser_e2e_has_one_shared_round_trip_entry_point()
    {
        var e2eTests = typeof(BrowserE2eArchitectureTests).Assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(IsTestMethod)
                .Where(method => HasE2eTrait(type) || HasE2eTrait(method)))
            .ToList();

        var test = Assert.Single(e2eTests);
        Assert.Equal(typeof(BrowserRoundTripTests), test.DeclaringType);
        Assert.Equal(
            nameof(BrowserRoundTripTests.Browser_features_round_trip_in_one_shared_scenario),
            test.Name);
    }

    private static bool IsTestMethod(MethodInfo method)
        => method.GetCustomAttributesData().Any(attribute =>
            attribute.AttributeType.Name is "FactAttribute" or "TheoryAttribute");

    private static bool HasE2eTrait(MemberInfo member)
        => member.GetCustomAttributesData().Any(attribute =>
            attribute.AttributeType.Name == "TraitAttribute"
            && attribute.ConstructorArguments.Count == 2
            && string.Equals(attribute.ConstructorArguments[0].Value as string, "Category", StringComparison.Ordinal)
            && string.Equals(attribute.ConstructorArguments[1].Value as string, "E2E", StringComparison.Ordinal));
}
