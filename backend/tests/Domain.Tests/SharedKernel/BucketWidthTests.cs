using FluentAssertions;
using VehicleDiagnostics.Domain.SharedKernel;
using Xunit;

namespace VehicleDiagnostics.Domain.Tests.SharedKernel;

public sealed class BucketWidthTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private static TimeRange Last(TimeSpan span) => TimeRange.Create(Now - span, Now, Now).Value;

    [Theory]
    [InlineData(60, 1)]          // 1 min  → 60 buckets of 1 s
    [InlineData(900, 5)]         // 15 min → 180 buckets of 5 s
    [InlineData(3_600, 30)]      // 1 h    → 120 buckets of 30 s
    [InlineData(86_400, 900)]    // 1 d    → 96 buckets of 15 min
    [InlineData(604_800, 3_600)] // 7 d    → 168 buckets of 1 h
    public void Missing_width_is_the_finest_ladder_step_within_the_target(int rangeSeconds, int expectedSeconds)
    {
        BucketWidth width = BucketWidth.Resolve(Last(TimeSpan.FromSeconds(rangeSeconds)), null).Value;

        width.Value.Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Fact]
    public void Requested_width_within_the_bound_is_kept()
    {
        BucketWidth width = BucketWidth.Resolve(Last(TimeSpan.FromHours(1)), TimeSpan.FromSeconds(7)).Value;

        width.Value.Should().Be(TimeSpan.FromSeconds(7));
    }

    [Fact]
    public void Requested_width_producing_too_many_buckets_is_refused()
    {
        var result = BucketWidth.Resolve(Last(TimeSpan.FromHours(1)), TimeSpan.FromSeconds(1));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("out_of_range");
        result.Error.Message.Should().Contain("3600").And.Contain(BucketWidth.MaxBuckets.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Exactly_the_maximum_number_of_buckets_is_accepted()
    {
        var result = BucketWidth.Resolve(Last(TimeSpan.FromSeconds(BucketWidth.MaxBuckets)), TimeSpan.FromSeconds(1));

        result.IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Non_positive_width_is_refused(int seconds)
    {
        var result = BucketWidth.Resolve(Last(TimeSpan.FromMinutes(5)), TimeSpan.FromSeconds(seconds));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("invalid");
    }

    [Fact]
    public void Sub_second_width_is_refused()
    {
        var result = BucketWidth.Resolve(Last(TimeSpan.FromMinutes(5)), TimeSpan.FromMilliseconds(500));

        result.IsFailure.Should().BeTrue();
    }
}
