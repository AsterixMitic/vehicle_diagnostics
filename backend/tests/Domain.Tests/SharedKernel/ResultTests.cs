using FluentAssertions;
using VehicleDiagnostics.Domain.SharedKernel;
using Xunit;

namespace VehicleDiagnostics.Domain.Tests.SharedKernel;

public sealed class ResultTests
{
    [Fact]
    public void Ok_exposes_value_and_reports_success()
    {
        var result = Result<int, ValidationError>.Ok(42);

        result.IsSuccess.Should().BeTrue();
        result.IsFailure.Should().BeFalse();
        result.Value.Should().Be(42);
    }

    [Fact]
    public void Fail_exposes_error_and_reports_failure()
    {
        ValidationError error = ValidationError.Required("deviceId");
        var result = Result<int, ValidationError>.Fail(error);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(error);
    }

    [Fact]
    public void Accessing_value_of_failure_throws()
    {
        var result = Result<int, ValidationError>.Fail(ValidationError.Required("x"));

        FluentActions.Invoking(() => result.Value).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Accessing_error_of_success_throws()
    {
        var result = Result<int, ValidationError>.Ok(1);

        FluentActions.Invoking(() => result.Error).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Map_transforms_success_only()
    {
        Result<int, ValidationError>.Ok(2).Map(v => v * 10).Value.Should().Be(20);

        ValidationError error = ValidationError.Invalid("f", "bad");
        Result<int, ValidationError>.Fail(error).Map(v => v * 10).Error.Should().Be(error);
    }

    [Fact]
    public void Bind_chains_success_and_short_circuits_failure()
    {
        static Result<int, ValidationError> NonNegative(int v) =>
            v >= 0 ? v : ValidationError.OutOfRange("v", "negative");

        Result<int, ValidationError>.Ok(3).Bind(NonNegative).Value.Should().Be(3);
        Result<int, ValidationError>.Ok(-3).Bind(NonNegative).IsFailure.Should().BeTrue();

        ValidationError error = ValidationError.Required("v");
        Result<int, ValidationError>.Fail(error).Bind(NonNegative).Error.Should().Be(error);
    }

    [Fact]
    public void Match_branches_on_outcome()
    {
        Result<int, ValidationError>.Ok(5).Match(v => $"ok:{v}", e => $"err:{e.Code}")
            .Should().Be("ok:5");

        Result<int, ValidationError>.Fail(ValidationError.Required("id"))
            .Match(v => $"ok:{v}", e => $"err:{e.Code}")
            .Should().Be("err:required");
    }

    [Fact]
    public void Implicit_conversions_build_the_expected_outcome()
    {
        Result<int, ValidationError> ok = 7;
        Result<int, ValidationError> fail = ValidationError.Required("id");

        ok.IsSuccess.Should().BeTrue();
        fail.IsFailure.Should().BeTrue();
    }
}
