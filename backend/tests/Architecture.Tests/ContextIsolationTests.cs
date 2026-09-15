using FluentAssertions;
using NetArchTest.Rules;
using Xunit;

namespace VehicleDiagnostics.Architecture.Tests;

/// <summary>
/// Bounded contexts inside the modular monolith are isolated by namespace (ADR-0002):
/// a context may use its own namespace and the shared kernel, never another context's
/// internals. Contexts communicate only via published contracts or messages.
/// </summary>
public sealed class ContextIsolationTests
{
    /// <summary>
    /// Every bounded context of architecture.md §11. Add here when a context gains code;
    /// the pairwise rule below then applies to it automatically.
    /// </summary>
    private static readonly string[] Contexts =
    [
        "Telemetry",
        "DeviceManagement",
        "SignalDecoding",
        "Diagnostics",
        "Analytics",
    ];

    public static TheoryData<string, string> ContextPairs()
    {
        var pairs = new TheoryData<string, string>();
        foreach (string a in Contexts)
        {
            foreach (string b in Contexts.Where(b => b != a))
            {
                pairs.Add(a, b);
            }
        }

        return pairs;
    }

    [Theory]
    [MemberData(nameof(ContextPairs))]
    public void Domain_context_does_not_reference_another_context(string context, string other)
    {
        TestResult result = Types.InAssembly(BackendAssemblies.Domain)
            .That().ResideInNamespaceStartingWith($"VehicleDiagnostics.Domain.{context}")
            .ShouldNot().HaveDependencyOn($"VehicleDiagnostics.Domain.{other}")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(LayerDependencyTests.Offenders(result));
    }

    [Theory]
    [MemberData(nameof(ContextPairs))]
    public void Application_context_does_not_reference_another_context(string context, string other)
    {
        TestResult result = Types.InAssembly(BackendAssemblies.Application)
            .That().ResideInNamespaceStartingWith($"VehicleDiagnostics.Application.{context}")
            .ShouldNot().HaveDependencyOnAny(
                $"VehicleDiagnostics.Application.{other}",
                $"VehicleDiagnostics.Domain.{other}")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(LayerDependencyTests.Offenders(result));
    }
}
