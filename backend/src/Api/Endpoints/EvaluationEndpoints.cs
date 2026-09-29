using System.Text;
using Microsoft.AspNetCore.Http.HttpResults;
using VehicleDiagnostics.Application.Analytics;
using VehicleDiagnostics.Domain.SharedKernel;

namespace VehicleDiagnostics.Api.Endpoints;

/// <summary>Evaluation metrics per processing mode, over backend receipt time.</summary>
public static class EvaluationEndpoints
{
    public static RouteGroupBuilder MapEvaluationEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapGet("/evaluation/summary", SummaryAsync);
        api.MapGet("/evaluation/series", SeriesAsync);
        api.MapGet("/evaluation/export.csv", ExportAsync);
        return api;
    }

    private static async Task<Results<Ok<EvaluationSummary>, ProblemHttpResult>> SummaryAsync(
        string? from, string? to, IEvaluationQueries queries, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        Result<TimeRange, ValidationError> range = QueryParameters.Range(from, to, timeProvider);
        if (range.IsFailure)
        {
            return QueryParameters.Problem(range.Error);
        }

        return TypedResults.Ok(await queries.SummaryAsync(range.Value, cancellationToken));
    }

    private static async Task<Results<Ok<EvaluationSeriesDto>, ProblemHttpResult>> SeriesAsync(
        string? from,
        string? to,
        string? bucketSeconds,
        IEvaluationQueries queries,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        Result<TimeRange, ValidationError> range = QueryParameters.Range(from, to, timeProvider);
        if (range.IsFailure)
        {
            return QueryParameters.Problem(range.Error);
        }

        Result<BucketWidth, ValidationError> bucket = QueryParameters.Bucket(range.Value, bucketSeconds);
        if (bucket.IsFailure)
        {
            return QueryParameters.Problem(bucket.Error);
        }

        return TypedResults.Ok(EvaluationSeriesDto.Of(await queries.SeriesAsync(range.Value, bucket.Value, cancellationToken)));
    }

    private static async Task<Results<FileContentHttpResult, ProblemHttpResult>> ExportAsync(
        string? from, string? to, IEvaluationQueries queries, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        Result<TimeRange, ValidationError> range = QueryParameters.Range(from, to, timeProvider);
        if (range.IsFailure)
        {
            return QueryParameters.Problem(range.Error);
        }

        EvaluationSummary summary = await queries.SummaryAsync(range.Value, cancellationToken);
        return TypedResults.File(
            Encoding.UTF8.GetBytes(EvaluationCsv.Write(summary)),
            "text/csv",
            EvaluationCsv.FileName(summary.From, summary.To));
    }
}
