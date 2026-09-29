using FluentAssertions;
using VehicleDiagnostics.Domain.SharedKernel;
using Xunit;

namespace VehicleDiagnostics.Domain.Tests.SharedKernel;

public sealed class TimeRangeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Absent_bounds_default_to_the_last_fifteen_minutes()
    {
        TimeRange range = TimeRange.Create(null, null, Now).Value;

        range.From.Should().Be(Now.AddMinutes(-15));
        range.To.Should().Be(Now);
    }

    [Fact]
    public void Absent_from_is_fifteen_minutes_before_to()
    {
        DateTimeOffset to = Now.AddHours(-2);

        TimeRange range = TimeRange.Create(null, to, Now).Value;

        range.From.Should().Be(to.AddMinutes(-15));
    }

    [Fact]
    public void Absent_to_is_now()
    {
        TimeRange range = TimeRange.Create(Now.AddHours(-1), null, Now).Value;

        range.To.Should().Be(Now);
    }

    [Fact]
    public void Bounds_are_normalized_to_utc()
    {
        var from = new DateTimeOffset(2026, 9, 29, 13, 0, 0, TimeSpan.FromHours(2));
        var to = new DateTimeOffset(2026, 9, 29, 14, 0, 0, TimeSpan.FromHours(2));

        TimeRange range = TimeRange.Create(from, to, Now).Value;

        range.From.Offset.Should().Be(TimeSpan.Zero);
        range.From.Should().Be(new DateTimeOffset(2026, 9, 29, 11, 0, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(-10, -10)] // empty
    [InlineData(-5, -10)]  // reversed
    public void From_not_before_to_is_rejected(int fromMinutes, int toMinutes)
    {
        var result = TimeRange.Create(Now.AddMinutes(fromMinutes), Now.AddMinutes(toMinutes), Now);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("invalid");
        result.Error.Message.Should().Contain("'from'");
    }

    [Fact]
    public void Range_longer_than_the_maximum_is_rejected()
    {
        var result = TimeRange.Create(Now.AddDays(-8), Now, Now);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("out_of_range");
    }

    [Fact]
    public void Range_of_exactly_the_maximum_is_accepted()
    {
        TimeRange.Create(Now - TimeRange.MaxSpan, Now, Now).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Span_is_the_difference_of_the_bounds()
    {
        TimeRange.Create(Now.AddMinutes(-90), Now, Now).Value.Span.Should().Be(TimeSpan.FromMinutes(90));
    }
}
