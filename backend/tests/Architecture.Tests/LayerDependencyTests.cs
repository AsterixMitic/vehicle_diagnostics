using FluentAssertions;
using NetArchTest.Rules;
using Xunit;

namespace VehicleDiagnostics.Architecture.Tests;

/// <summary>
/// Dependency direction points inward only (ADR-0002):
/// Domain → nothing, Application → Domain only, Infrastructure/Api → inward.
/// Contracts is a leaf shared with external producers and references no backend layer.
/// </summary>
public sealed class LayerDependencyTests
{
    private const string DomainNs = "VehicleDiagnostics.Domain";
    private const string ApplicationNs = "VehicleDiagnostics.Application";
    private const string ContractsNs = "VehicleDiagnostics.Contracts";
    private const string InfrastructureNs = "VehicleDiagnostics.Infrastructure";
    private const string ApiNs = "VehicleDiagnostics.Api";
    private const string ServiceDefaultsNs = "VehicleDiagnostics.ServiceDefaults";

    [Fact]
    public void Domain_depends_on_no_other_backend_layer()
    {
        TestResult result = Types.InAssembly(BackendAssemblies.Domain)
            .ShouldNot()
            .HaveDependencyOnAny(ApplicationNs, ContractsNs, InfrastructureNs, ApiNs, ServiceDefaultsNs)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(Offenders(result));
    }

    [Fact]
    public void Application_depends_only_on_Domain()
    {
        TestResult result = Types.InAssembly(BackendAssemblies.Application)
            .ShouldNot()
            .HaveDependencyOnAny(ContractsNs, InfrastructureNs, ApiNs, ServiceDefaultsNs)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(Offenders(result));
    }

    [Fact]
    public void Contracts_depends_on_no_backend_layer()
    {
        TestResult result = Types.InAssembly(BackendAssemblies.Contracts)
            .ShouldNot()
            .HaveDependencyOnAny(DomainNs, ApplicationNs, InfrastructureNs, ApiNs, ServiceDefaultsNs)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(Offenders(result));
    }

    [Fact]
    public void Infrastructure_does_not_depend_on_Api()
    {
        TestResult result = Types.InAssembly(BackendAssemblies.Infrastructure)
            .ShouldNot()
            .HaveDependencyOn(ApiNs)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(Offenders(result));
    }

    internal static string Offenders(TestResult result) =>
        result.IsSuccessful
            ? string.Empty
            : "offending types: " + string.Join(", ", result.FailingTypes.Select(t => t.FullName));
}
