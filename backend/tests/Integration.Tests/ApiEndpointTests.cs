using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using MQTTnet;
using VehicleDiagnostics.Contracts;
using Xunit;

namespace VehicleDiagnostics.Integration.Tests;

/// <summary>
/// The HTTP API on a real Kestrel host against the real stack: telemetry published over MQTT is
/// read back through every endpoint, invalid parameters answer ProblemDetails, and the live stream
/// delivers a persisted message.
/// </summary>
[Collection(StackCollectionDefinition.Name)]
public sealed class ApiEndpointTests(StackFixture stack) : IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private WebApplication? _app;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _app = await stack.StartApiAsync("e2e-api");
        _http = new HttpClient { BaseAddress = new Uri(_app.Urls.First()), Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private static string Iso(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static async Task PublishAsync(IMqttClient publisher, string deviceId, long sequence, DateTimeOffset sampledAt)
    {
        byte[] payload = EnvelopeSerializer.Serialize(new MessageEnvelope<SignalReadingsPayload>
        {
            SchemaVersion = EnvelopeSerializer.CurrentSchemaVersion,
            DeviceId = deviceId,
            VehicleId = "api-car",
            Sequence = sequence,
            TimestampDevice = sampledAt,
            TraceId = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
            ProcessingMode = "P2",
            Payload = new SignalReadingsPayload
            {
                Readings = [new SignalReadingDto { Name = "engine_rpm", Value = 900 + sequence, Unit = "rpm" }],
            },
        });
        await publisher.PublishAsync(
            MqttIngestEndToEndTests.Message(TelemetryTopics.Signals("api-car", deviceId), payload), CancellationToken.None);
    }

    private async Task<JsonElement> PollAsync(string url, Func<JsonElement, bool> done)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + Patience;
        JsonElement body = default;
        while (DateTimeOffset.UtcNow < deadline)
        {
            body = await _http.GetFromJsonAsync<JsonElement>(url);
            if (done(body))
            {
                return body;
            }

            await Task.Delay(250);
        }

        return body;
    }

    [Fact]
    public async Task Published_telemetry_is_readable_through_every_read_endpoint()
    {
        DateTimeOffset sampledAt = DateTimeOffset.UtcNow;
        string window = $"from={Iso(sampledAt.AddMinutes(-1))}&to={Iso(sampledAt.AddMinutes(1))}";
        using IMqttClient publisher = await stack.ConnectPublisherAsync("api-publisher");
        await PublishAsync(publisher, "api-device", 1, sampledAt);

        JsonElement latest = await PollAsync("/api/devices/api-device/signals", b => b.GetArrayLength() > 0);
        latest[0].GetProperty("name").GetString().Should().Be("engine_rpm");
        latest[0].GetProperty("value").GetDouble().Should().Be(901);
        latest[0].GetProperty("processingMode").GetString().Should().Be("P2");

        JsonElement devices = await _http.GetFromJsonAsync<JsonElement>($"/api/devices?{window}");
        devices.EnumerateArray().Should().Contain(d => d.GetProperty("deviceId").GetString() == "api-device");

        JsonElement series = await _http.GetFromJsonAsync<JsonElement>(
            $"/api/devices/api-device/signals/engine_rpm/series?{window}&bucketSeconds=60");
        series.GetProperty("bucketSeconds").GetDouble().Should().Be(60);
        series.GetProperty("points").EnumerateArray().Sum(p => p.GetProperty("samples").GetInt64()).Should().Be(1);

        // The settlement travels over the bus to Analytics, so the summary catches up shortly after.
        JsonElement summary = await PollAsync($"/api/evaluation/summary?{window}", b =>
            b.GetProperty("modes").EnumerateArray().Any(m => m.GetProperty("mode").GetString() == "P2"
                && m.GetProperty("persisted").GetInt64() >= 1));
        JsonElement p2 = summary.GetProperty("modes").EnumerateArray().Single(m => m.GetProperty("mode").GetString() == "P2");
        p2.GetProperty("endToEnd").GetProperty("p50").GetDouble().Should().BeGreaterThan(0);
        p2.GetProperty("meanPayloadBytes").GetDouble().Should().BeGreaterThan(0);

        JsonElement evaluationSeries = await _http.GetFromJsonAsync<JsonElement>($"/api/evaluation/series?{window}");
        evaluationSeries.GetProperty("points").GetArrayLength().Should().BeGreaterThan(0);

        using HttpResponseMessage csv = await _http.GetAsync($"/api/evaluation/export.csv?{window}");
        csv.StatusCode.Should().Be(HttpStatusCode.OK);
        csv.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        csv.Content.Headers.ContentDisposition!.FileName.Should().StartWith("evaluation_").And.EndWith(".csv");
        string text = await csv.Content.ReadAsStringAsync();
        text.Split('\n')[0].Should().StartWith("mode,received,persisted");
        text.Should().Contain("\nP2,");
    }

    [Theory]
    [InlineData("/api/evaluation/summary?from=2026-09-29T11:00:00Z&to=2026-09-29T10:00:00Z", "'from'")]
    [InlineData("/api/devices?from=yesterday", "'from'")]
    [InlineData("/api/evaluation/summary?from=2026-09-01T00:00:00Z&to=2026-09-29T00:00:00Z", "'range'")]
    [InlineData("/api/evaluation/series?from=2026-09-29T10:00:00Z&to=2026-09-29T11:00:00Z&bucketSeconds=1", "'bucket'")]
    [InlineData("/api/evaluation/series?bucketSeconds=soon", "'bucketSeconds'")]
    [InlineData("/api/devices/bad%20id/signals", "'deviceId'")]
    public async Task Invalid_parameters_are_answered_with_problem_details(string url, string names)
    {
        using HttpResponseMessage response = await _http.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("detail").GetString().Should().Contain(names);
    }

    [Fact]
    public async Task Offset_with_an_unencoded_plus_is_still_an_instant()
    {
        using HttpResponseMessage response = await _http.GetAsync("/api/devices?from=2026-09-29T12:00:00+02:00&to=2026-09-29T12:30:00+02:00");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Live_stream_delivers_a_persisted_message_for_its_device()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/stream/readings?deviceId=api-live");
        using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        using IMqttClient publisher = await stack.ConnectPublisherAsync("api-live-publisher");
        using var timeout = new CancellationTokenSource(Patience);

        // The subscription starts with the response body, so keep publishing until one arrives.
        long sequence = 0;
        Task<string?> nextLine = reader.ReadLineAsync(timeout.Token).AsTask();
        string? eventType = null;
        string? data = null;
        while (data is null)
        {
            await PublishAsync(publisher, "api-live", ++sequence, DateTimeOffset.UtcNow);
            if (await Task.WhenAny(nextLine, Task.Delay(500, timeout.Token)) != nextLine)
            {
                continue;
            }

            string? line = await nextLine;
            line.Should().NotBeNull("the stream must stay open");
            if (line!.StartsWith("event:", StringComparison.Ordinal))
            {
                eventType = line["event:".Length..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                data = line["data:".Length..].Trim();
                break;
            }

            nextLine = reader.ReadLineAsync(timeout.Token).AsTask();
        }

        eventType.Should().Be("reading");
        using JsonDocument reading = JsonDocument.Parse(data);
        reading.RootElement.GetProperty("deviceId").GetString().Should().Be("api-live");
        reading.RootElement.GetProperty("readings")[0].GetProperty("name").GetString().Should().Be("engine_rpm");
    }
}
