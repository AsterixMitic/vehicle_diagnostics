using System.Reflection;

namespace VehicleDiagnostics.Architecture.Tests;

/// <summary>
/// Loads the backend assemblies under test by name. Loading by name (rather than via
/// <c>typeof</c> markers) keeps these tests independent of any particular type existing
/// in a layer, including while a layer is still empty.
/// </summary>
internal static class BackendAssemblies
{
    public static Assembly Domain { get; } = Load("VehicleDiagnostics.Domain");

    public static Assembly Application { get; } = Load("VehicleDiagnostics.Application");

    public static Assembly Contracts { get; } = Load("VehicleDiagnostics.Contracts");

    public static Assembly Infrastructure { get; } = Load("VehicleDiagnostics.Infrastructure");

    public static Assembly Api { get; } = Load("VehicleDiagnostics.Api");

    private static Assembly Load(string name) => Assembly.Load(new AssemblyName(name));
}
