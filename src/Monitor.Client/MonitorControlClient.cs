using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Monitor.Domain;

namespace Monitor.Client;

public sealed class MonitorControlClient
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    public MonitorControlClient(HttpClient httpClient, string apiKey)
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

    public async Task<ComponentControlCommand?> ClaimNextAsync(
        Guid componentId,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(
            HttpMethod.Post,
            $"/api/components/{componentId:D}/commands/claim",
            body: null);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        await EnsureSuccessAsync(response, request.Method, request.RequestUri!.ToString(), cancellationToken);
        return await response.Content.ReadFromJsonAsync<ComponentControlCommand>(JsonOptions, cancellationToken)
            ?? throw new MonitorApiException(
                "Monitor command API returned an empty response where JSON was expected.",
                response.StatusCode,
                string.Empty);
    }

    public Task<ComponentCommandCompletion> SucceedAsync(
        ComponentControlCommand command,
        object? result = null,
        CancellationToken cancellationToken = default) =>
        CompleteAsync(command, ComponentCommandOutcome.Succeeded, result, error: null, cancellationToken);

    public Task<ComponentCommandCompletion> FailAsync(
        ComponentControlCommand command,
        string error,
        object? result = null,
        CancellationToken cancellationToken = default) =>
        CompleteAsync(command, ComponentCommandOutcome.Failed, result, error, cancellationToken);

    public Task<ComponentCommandCompletion> RejectAsync(
        ComponentControlCommand command,
        string reason,
        object? result = null,
        CancellationToken cancellationToken = default) =>
        CompleteAsync(command, ComponentCommandOutcome.Rejected, result, reason, cancellationToken);

    public async Task<ComponentCommandCompletion> CompleteAsync(
        ComponentControlCommand command,
        ComponentCommandOutcome outcome,
        object? result = null,
        string? error = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var body = new CompleteCommandRequest(
            command.LeaseToken,
            outcome,
            SerializePayload(result),
            error);

        var path = $"/api/components/{command.ComponentId:D}/commands/{command.Id:D}/complete";
        using var request = CreateRequest(HttpMethod.Post, path, body);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, request.Method, path, cancellationToken);

        return await response.Content.ReadFromJsonAsync<ComponentCommandCompletion>(JsonOptions, cancellationToken)
            ?? throw new MonitorApiException(
                "Monitor command API returned an empty completion response.",
                response.StatusCode,
                string.Empty);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, object? body)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("X-Monitor-Key", _apiKey);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        return request;
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        HttpMethod method,
        string path,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new MonitorApiException(
            $"Monitor API returned {(int)response.StatusCode} {response.ReasonPhrase} for {method} {path}.",
            response.StatusCode,
            responseBody);
    }

    private static string? SerializePayload(object? value) => value switch
    {
        null => null,
        string text => text,
        _ => JsonSerializer.Serialize(value, JsonOptions)
    };

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

    private sealed record CompleteCommandRequest(
        Guid LeaseToken,
        ComponentCommandOutcome Outcome,
        string? ResultJson,
        string? Error);
}

public sealed record ComponentControlCommand(
    Guid Id,
    Guid ComponentId,
    ComponentCommandType Type,
    Guid? TargetRunId,
    string? PayloadJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    Guid LeaseToken,
    DateTimeOffset LeaseExpiresAt,
    int DeliveryAttempt);

public sealed record ComponentCommandCompletion(
    ComponentCommandStatus Status,
    bool AlreadyTerminal);
