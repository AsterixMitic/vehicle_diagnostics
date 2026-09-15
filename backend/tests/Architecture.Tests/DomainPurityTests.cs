using FluentAssertions;
using NetArchTest.Rules;
using Xunit;

namespace VehicleDiagnostics.Architecture.Tests;

/// <summary>
/// The domain layer performs no I/O: no database, no network, no message broker,
/// no file system, no web framework, and no direct clock access.
/// Time enters the domain only through <see cref="TimeProvider"/>.
/// </summary>
public sealed class DomainPurityTests
{
    /// <remarks>
    /// <c>System.DateTime</c> is banned as a type: domain timestamps use
    /// <c>DateTimeOffset</c>, so any dependency on <c>DateTime</c> almost certainly
    /// means a <c>DateTime.Now</c>/<c>UtcNow</c> call. Type-level analysis cannot see
    /// <c>DateTimeOffset.UtcNow</c> member access — that gap is covered by convention
    /// (TimeProvider) and code review, not by this test.
    /// </remarks>
    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("System.Net.Http")]
    [InlineData("MQTTnet")]
    [InlineData("System.IO")]
    [InlineData("Microsoft.AspNetCore")]
    [InlineData("Npgsql")]
    [InlineData("System.Data")]
    [InlineData("System.DateTime")]
    public void Domain_has_no_dependency_on(string forbidden)
    {
        TestResult result = Types.InAssembly(BackendAssemblies.Domain)
            .ShouldNot()
            .HaveDependencyOn(forbidden)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(LayerDependencyTests.Offenders(result));
    }

    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("System.Net.Http")]
    [InlineData("MQTTnet")]
    [InlineData("Microsoft.AspNetCore")]
    [InlineData("Npgsql")]
    public void Application_has_no_dependency_on(string forbidden)
    {
        TestResult result = Types.InAssembly(BackendAssemblies.Application)
            .ShouldNot()
            .HaveDependencyOn(forbidden)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(LayerDependencyTests.Offenders(result));
    }
}
