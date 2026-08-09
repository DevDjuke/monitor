using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Monitor.Domain;

namespace Monitor.Client;

public sealed class MonitorClient
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    public MonitorClient(HttpClient httpClient, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        if (httpClient.BaseAddress is null)
        {
            throw new ArgumentException("HttpClient.BaseAddress must be configured.", nameof(httpClient));
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("The Monitor ingestion API key is required.", nameof(apiKey));
        }

        _httpClient = httpClient;
        _apiKey = apiKey;
    }

    public async Task<RegisteredComponent> RegisterComponentAsync(
        ComponentRegistration registration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);

        var payload = new RegisterComponentRequest(
            registration.Name,
            registration.Slug,
            registration.Type,
            registration.Environment,
            registration.Version);

        using var response = await SendAsync(HttpMethod.Post, "/api/components/register", payload, cancellationToken);
        return await ReadRequiredAsync<RegisteredComponent>(response, cancellationToken);
    }

    public async Task HeartbeatAsync(Guid componentId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            $"/api/components/{componentId:D}/heartbeat",
            body: null,
            cancellationToken);
    }

    public async Task<MonitorRun> StartRunAsync(
        StartRunOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var payload = new StartRunRequest(
            options.ComponentId,
            options.Name,
            options.ExternalId,
            options.Trigger,
            options.Model,
            SerializePayload(options.Input));

        using var response = await SendAsync(HttpMethod.Post, "/api/runs", payload, cancellationToken);
        var started = await ReadRequiredAsync<StartRunResponse>(response, cancellationToken);

        return new MonitorRun(this, started.Id, started.StartedAt);
    }

    public async Task<Guid> RecordSpanAsync(
        Guid runId,
        SpanRecord span,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(span);

        var payload = new CreateSpanRequest(
            span.ParentSpanId,
            span.Name,
            span.Kind,
            span.Status,
            span.StartedAt,
            span.CompletedAt,
            SerializePayload(span.Attributes),
            span.Error);

        using var response = await SendAsync(
            HttpMethod.Post,
            $"/api/runs/{runId:D}/spans",
            payload,
            cancellationToken);

        var created = await ReadRequiredAsync<CreateSpanResponse>(response, cancellationToken);
        return created.Id;
    }

    internal async Task CompleteRunAsync(
        Guid runId,
        RunStatus status,
        RunCompletion completion,
        CancellationToken cancellationToken)
    {
        var payload = new CompleteRunRequest(
            status,
            completion.InputTokens,
            completion.OutputTokens,
            completion.CostUsd,
            SerializePayload(completion.Output),
            completion.Error);

        using var response = await SendAsync(
            HttpMethod.Post,
            $"/api/runs/{runId:D}/complete",
            payload,
            cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("X-Monitor-Key", _apiKey);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var statusCode = response.StatusCode;
        var reasonPhrase = response.ReasonPhrase;
        response.Dispose();

        throw new MonitorApiException(
            $"Monitor API returned {(int)statusCode} {reasonPhrase} for {method} {path}.",
            statusCode,
            responseBody);
    }

    private static async Task<T> ReadRequiredAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
        return value ?? throw new MonitorApiException(
            "Monitor API returned an empty response where JSON was expected.",
            response.StatusCode,
            string.Empty);
    }

    private static string? SerializePayload(object? value)
    {
        return value switch
        {
            null => null,
            string text => text,
            _ => JsonSerializer.Serialize(value, JsonOptions)
        };
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed record RegisterComponentRequest(
        string Name,
        string Slug,
        ComponentType Type,
        string Environment,
        string? Version);

    private sealed record StartRunRequest(
        Guid ComponentId,
        string Name,
        string? ExternalId,
        string? Trigger,
        string? Model,
        string? InputJson);

    private sealed record StartRunResponse(Guid Id, DateTimeOffset StartedAt);

    private sealed record CompleteRunRequest(
        RunStatus Status,
        long InputTokens,
        long OutputTokens,
        double CostUsd,
        string? OutputJson,
        string? Error);

    private sealed record CreateSpanRequest(
        Guid? ParentSpanId,
        string Name,
        SpanKind Kind,
        SpanStatus Status,
        DateTimeOffset? StartedAt,
        DateTimeOffset? CompletedAt,
        string? AttributesJson,
        string? Error);

    private sealed record CreateSpanResponse(Guid Id);
}

public sealed class MonitorRun
{
    private readonly MonitorClient _client;
    private int _terminalState;

    internal MonitorRun(MonitorClient client, Guid id, DateTimeOffset startedAt)
    {
        _client = client;
        Id = id;
        StartedAt = startedAt;
    }

    public Guid Id { get; }
    public DateTimeOffset StartedAt { get; }
    public bool IsCompleted => Volatile.Read(ref _terminalState) == 2;

    public Task<Guid> RecordSpanAsync(SpanRecord span, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return _client.RecordSpanAsync(Id, span, cancellationToken);
    }

    public async Task MeasureSpanAsync(
        string name,
        SpanKind kind,
        Func<CancellationToken, Task> action,
        object? attributes = null,
        Guid? parentSpanId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        EnsureActive();

        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            await action(cancellationToken);
            await RecordSpanAsync(
                new SpanRecord(
                    name,
                    kind,
                    SpanStatus.Success,
                    startedAt,
                    DateTimeOffset.UtcNow,
                    parentSpanId,
                    attributes),
                cancellationToken);
        }
        catch (Exception exception)
        {
            await TryRecordFailedSpanAsync(
                name,
                kind,
                startedAt,
                parentSpanId,
                attributes,
                exception,
                cancellationToken);
            throw;
        }
    }

    public async Task<T> MeasureSpanAsync<T>(
        string name,
        SpanKind kind,
        Func<CancellationToken, Task<T>> action,
        object? attributes = null,
        Guid? parentSpanId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        EnsureActive();

        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            var result = await action(cancellationToken);
            await RecordSpanAsync(
                new SpanRecord(
                    name,
                    kind,
                    SpanStatus.Success,
                    startedAt,
                    DateTimeOffset.UtcNow,
                    parentSpanId,
                    attributes),
                cancellationToken);
            return result;
        }
        catch (Exception exception)
        {
            await TryRecordFailedSpanAsync(
                name,
                kind,
                startedAt,
                parentSpanId,
                attributes,
                exception,
                cancellationToken);
            throw;
        }
    }

    public Task CompleteAsync(RunCompletion completion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completion);
        return CompleteCoreAsync(RunStatus.Success, completion, cancellationToken);
    }

    public Task FailAsync(
        Exception exception,
        long inputTokens = 0,
        long outputTokens = 0,
        double costUsd = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return CompleteCoreAsync(
            RunStatus.Failed,
            new RunCompletion(inputTokens, outputTokens, costUsd, Error: exception.ToString()),
            cancellationToken);
    }

    public Task CancelAsync(string? reason = null, CancellationToken cancellationToken = default)
    {
        return CompleteCoreAsync(
            RunStatus.Cancelled,
            new RunCompletion(Error: reason),
            cancellationToken);
    }

    private async Task CompleteCoreAsync(
        RunStatus status,
        RunCompletion completion,
        CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _terminalState, 1, 0) != 0)
        {
            throw new InvalidOperationException("The Monitor run has already been completed or is completing.");
        }

        try
        {
            await _client.CompleteRunAsync(Id, status, completion, cancellationToken);
            Volatile.Write(ref _terminalState, 2);
        }
        catch
        {
            Volatile.Write(ref _terminalState, 0);
            throw;
        }
    }

    private async Task TryRecordFailedSpanAsync(
        string name,
        SpanKind kind,
        DateTimeOffset startedAt,
        Guid? parentSpanId,
        object? attributes,
        Exception exception,
        CancellationToken cancellationToken)
    {
        try
        {
            await RecordSpanAsync(
                new SpanRecord(
                    name,
                    kind,
                    SpanStatus.Failed,
                    startedAt,
                    DateTimeOffset.UtcNow,
                    parentSpanId,
                    attributes,
                    exception.ToString()),
                cancellationToken);
        }
        catch
        {
            // Preserve the original operation exception. Losing telemetry must not mask the failure being monitored.
        }
    }

    private void EnsureActive()
    {
        if (Volatile.Read(ref _terminalState) != 0)
        {
            throw new InvalidOperationException("Cannot add telemetry to a completed Monitor run.");
        }
    }
}

public sealed record ComponentRegistration(
    string Name,
    string Slug,
    ComponentType Type,
    string Environment,
    string? Version = null);

public sealed record RegisteredComponent(Guid Id, string Slug, string Environment);

public sealed record StartRunOptions(
    Guid ComponentId,
    string Name,
    string? ExternalId = null,
    string? Trigger = null,
    string? Model = null,
    object? Input = null);

public sealed record RunCompletion(
    long InputTokens = 0,
    long OutputTokens = 0,
    double CostUsd = 0,
    object? Output = null,
    string? Error = null);

public sealed record SpanRecord(
    string Name,
    SpanKind Kind,
    SpanStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt = null,
    Guid? ParentSpanId = null,
    object? Attributes = null,
    string? Error = null);

public sealed class MonitorApiException : HttpRequestException
{
    public MonitorApiException(string message, HttpStatusCode statusCode, string responseBody)
        : base(message, inner: null, statusCode)
    {
        ResponseBody = responseBody;
    }

    public string ResponseBody { get; }
}
