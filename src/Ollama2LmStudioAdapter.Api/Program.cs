using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.Configure<LmStudioOptions>(builder.Configuration.GetSection(LmStudioOptions.SectionName));
builder.Services.Configure<AdapterOptions>(builder.Configuration.GetSection(AdapterOptions.SectionName));
builder.Services.AddSingleton(JsonOptionsFactory.Create());
builder.Services.AddSingleton<AdapterMapper>();
builder.Services.AddHttpClient<LmStudioClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<IOptions<LmStudioOptions>>().Value;
    if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri))
    {
        throw new InvalidOperationException($"Configuration '{LmStudioOptions.SectionName}:BaseUrl' must be an absolute URL.");
    }

    client.BaseAddress = baseUri;
    client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);

    if (!string.IsNullOrWhiteSpace(options.ApiKey))
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
    }
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/", (IOptions<AdapterOptions> adapter) =>
{
    return Results.Ok(new
    {
        name = "Ollama2LmStudioAdapter",
        version = adapter.Value.Version,
        status = "ok"
    });
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/api/version", (IOptions<AdapterOptions> adapter) =>
    Results.Ok(new { version = adapter.Value.Version }));

app.MapGet("/api/tags", async Task<Results<Ok<OllamaTagsResponse>, JsonHttpResult<AdapterErrorResponse>>> (
    LmStudioClient client,
    AdapterMapper mapper,
    CancellationToken cancellationToken) =>
{
    try
    {
        var models = await client.GetModelsAsync(cancellationToken);
        return TypedResults.Ok(mapper.MapTags(models));
    }
    catch (AdapterHttpException exception)
    {
        return TypedResults.Json(AdapterErrorResponse.FromMessage(exception.Message), statusCode: exception.StatusCode);
    }
});

app.MapPost("/api/show", async Task<IResult> (
    OllamaShowRequest request,
    LmStudioClient client,
    AdapterMapper mapper,
    JsonSerializerOptions jsonOptions,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Model))
    {
        return Results.Json(AdapterErrorResponse.FromMessage("The 'model' field is required."), jsonOptions, statusCode: StatusCodes.Status400BadRequest);
    }

    try
    {
        var models = await client.GetModelsAsync(cancellationToken);
        var match = models.Data.FirstOrDefault(model => string.Equals(model.Id, request.Model, StringComparison.Ordinal));
        if (match is null)
        {
            return Results.Json(AdapterErrorResponse.FromMessage($"Model '{request.Model}' was not found."), jsonOptions, statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Json(mapper.MapShow(match), jsonOptions);
    }
    catch (AdapterHttpException exception)
    {
        return Results.Json(AdapterErrorResponse.FromMessage(exception.Message), jsonOptions, statusCode: exception.StatusCode);
    }
});

app.MapGet("/v1/models", async Task<Results<Ok<OpenAiModelsResponse>, JsonHttpResult<AdapterErrorResponse>>> (
    LmStudioClient client,
    AdapterMapper mapper,
    CancellationToken cancellationToken) =>
{
    try
    {
        var models = await client.GetModelsAsync(cancellationToken);
        return TypedResults.Ok(mapper.MapOpenAiModels(models));
    }
    catch (AdapterHttpException exception)
    {
        return TypedResults.Json(AdapterErrorResponse.FromMessage(exception.Message), statusCode: exception.StatusCode);
    }
});

app.MapPost("/api/generate", async Task<IResult> (
    OllamaGenerateRequest request,
    LmStudioClient client,
    AdapterMapper mapper,
    JsonSerializerOptions jsonOptions,
    CancellationToken cancellationToken) =>
{
    var validationError = mapper.ValidateGenerateRequest(request);
    if (validationError is not null)
    {
        return Results.Json(validationError, jsonOptions, statusCode: StatusCodes.Status400BadRequest);
    }

    try
    {
        if (request.Stream)
        {
            var stream = await client.CreateCompletionStreamAsync(mapper.MapGenerateRequest(request), cancellationToken);
            return Results.Stream(async responseStream =>
            {
                await StreamAdapters.WriteGenerateStreamAsync(stream, responseStream, request.Model, jsonOptions, cancellationToken);
            }, "application/x-ndjson");
        }

        var completion = await client.CreateCompletionAsync(mapper.MapGenerateRequest(request), cancellationToken);
        return Results.Json(mapper.MapGenerateResponse(request.Model, completion), jsonOptions);
    }
    catch (AdapterHttpException exception)
    {
        return Results.Json(AdapterErrorResponse.FromMessage(exception.Message), jsonOptions, statusCode: exception.StatusCode);
    }
});

app.MapPost("/v1/completions", async Task<IResult> (
    JsonObject request,
    LmStudioClient client,
    JsonSerializerOptions jsonOptions,
    CancellationToken cancellationToken) =>
{
    try
    {
        if (RequestHelpers.GetStreamFlag(request))
        {
            var stream = await client.CreateCompletionPassthroughStreamAsync(request, cancellationToken);
            return Results.Stream(async responseStream =>
            {
                await StreamAdapters.CopyToAsync(stream, responseStream, cancellationToken);
            }, "text/event-stream");
        }

        var completion = await client.CreateCompletionPassthroughAsync(request, cancellationToken);
        return Results.Json(completion, jsonOptions);
    }
    catch (AdapterHttpException exception)
    {
        return Results.Json(AdapterErrorResponse.FromMessage(exception.Message), jsonOptions, statusCode: exception.StatusCode);
    }
});

app.MapPost("/api/chat", async Task<IResult> (
    OllamaChatRequest request,
    LmStudioClient client,
    AdapterMapper mapper,
    JsonSerializerOptions jsonOptions,
    CancellationToken cancellationToken) =>
{
    var validationError = mapper.ValidateChatRequest(request);
    if (validationError is not null)
    {
        return Results.Json(validationError, jsonOptions, statusCode: StatusCodes.Status400BadRequest);
    }

    try
    {
        if (request.Stream)
        {
            var stream = await client.CreateChatCompletionStreamAsync(mapper.MapChatRequest(request), cancellationToken);
            return Results.Stream(async responseStream =>
            {
                await StreamAdapters.WriteChatStreamAsync(stream, responseStream, request.Model, jsonOptions, cancellationToken);
            }, "application/x-ndjson");
        }

        var completion = await client.CreateChatCompletionAsync(mapper.MapChatRequest(request), cancellationToken);
        return Results.Json(mapper.MapChatResponse(request.Model, completion), jsonOptions);
    }
    catch (AdapterHttpException exception)
    {
        return Results.Json(AdapterErrorResponse.FromMessage(exception.Message), jsonOptions, statusCode: exception.StatusCode);
    }
});

app.MapPost("/v1/chat/completions", async Task<IResult> (
    JsonObject request,
    LmStudioClient client,
    JsonSerializerOptions jsonOptions,
    CancellationToken cancellationToken) =>
{
    try
    {
        if (RequestHelpers.GetStreamFlag(request))
        {
            var stream = await client.CreateChatCompletionPassthroughStreamAsync(request, cancellationToken);
            return Results.Stream(async responseStream =>
            {
                await StreamAdapters.CopyToAsync(stream, responseStream, cancellationToken);
            }, "text/event-stream");
        }

        var completion = await client.CreateChatCompletionPassthroughAsync(request, cancellationToken);
        return Results.Json(completion, jsonOptions);
    }
    catch (AdapterHttpException exception)
    {
        return Results.Json(AdapterErrorResponse.FromMessage(exception.Message), jsonOptions, statusCode: exception.StatusCode);
    }
});

app.Run();

internal sealed class LmStudioClient(HttpClient httpClient, JsonSerializerOptions jsonOptions)
{
    public async Task<LmStudioModelsResponse> GetModelsAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "models");
        using var response = await SendAsync(request, cancellationToken);
        return await DeserializeAsync<LmStudioModelsResponse>(response, cancellationToken);
    }

    public async Task<LmStudioCompletionResponse> CreateCompletionAsync(LmStudioCompletionRequest payload, CancellationToken cancellationToken)
    {
        using var request = CreateJsonRequest("completions", payload);
        using var response = await SendAsync(request, cancellationToken);
        return await DeserializeAsync<LmStudioCompletionResponse>(response, cancellationToken);
    }

    public async Task<JsonNode> CreateCompletionPassthroughAsync(JsonObject payload, CancellationToken cancellationToken)
    {
        using var request = CreateJsonRequest("completions", payload);
        using var response = await SendAsync(request, cancellationToken);
        return await DeserializeAsync<JsonNode>(response, cancellationToken);
    }

    public async Task<DownstreamStream> CreateCompletionStreamAsync(LmStudioCompletionRequest payload, CancellationToken cancellationToken)
    {
        using var request = CreateJsonRequest("completions", payload with { Stream = true });
        var response = await SendAsync(request, cancellationToken, HttpCompletionOption.ResponseHeadersRead);
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return new DownstreamStream(response, stream);
    }

    public async Task<DownstreamStream> CreateCompletionPassthroughStreamAsync(JsonObject payload, CancellationToken cancellationToken)
    {
        using var request = CreateJsonRequest("completions", payload);
        var response = await SendAsync(request, cancellationToken, HttpCompletionOption.ResponseHeadersRead);
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return new DownstreamStream(response, stream);
    }

    public async Task<LmStudioChatCompletionResponse> CreateChatCompletionAsync(LmStudioChatCompletionRequest payload, CancellationToken cancellationToken)
    {
        using var request = CreateJsonRequest("chat/completions", payload);
        using var response = await SendAsync(request, cancellationToken);
        return await DeserializeAsync<LmStudioChatCompletionResponse>(response, cancellationToken);
    }

    public async Task<JsonNode> CreateChatCompletionPassthroughAsync(JsonObject payload, CancellationToken cancellationToken)
    {
        using var request = CreateJsonRequest("chat/completions", payload);
        using var response = await SendAsync(request, cancellationToken);
        return await DeserializeAsync<JsonNode>(response, cancellationToken);
    }

    public async Task<DownstreamStream> CreateChatCompletionStreamAsync(LmStudioChatCompletionRequest payload, CancellationToken cancellationToken)
    {
        using var request = CreateJsonRequest("chat/completions", payload with { Stream = true });
        var response = await SendAsync(request, cancellationToken, HttpCompletionOption.ResponseHeadersRead);
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return new DownstreamStream(response, stream);
    }

    public async Task<DownstreamStream> CreateChatCompletionPassthroughStreamAsync(JsonObject payload, CancellationToken cancellationToken)
    {
        using var request = CreateJsonRequest("chat/completions", payload);
        var response = await SendAsync(request, cancellationToken, HttpCompletionOption.ResponseHeadersRead);
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return new DownstreamStream(response, stream);
    }

    private HttpRequestMessage CreateJsonRequest<T>(string relativePath, T payload)
    {
        return new HttpRequestMessage(HttpMethod.Post, relativePath)
        {
            Content = JsonContent.Create(payload, options: jsonOptions)
        };
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead)
    {
        try
        {
            var response = await httpClient.SendAsync(request, completionOption, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            var details = await SafeReadContentAsync(response, cancellationToken);
            response.Dispose();
            throw new AdapterHttpException(MapStatusCode(response.StatusCode), $"LM Studio request failed with {(int)response.StatusCode}: {details}");
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AdapterHttpException(StatusCodes.Status504GatewayTimeout, "LM Studio request timed out.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new AdapterHttpException(StatusCodes.Status502BadGateway, "Unable to reach LM Studio. Verify the local server is running.", exception);
        }
    }

    private async Task<T> DeserializeAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var payload = await response.Content.ReadFromJsonAsync<T>(jsonOptions, cancellationToken);
            return payload ?? throw new AdapterHttpException(StatusCodes.Status502BadGateway, "LM Studio returned an empty response.");
        }
        catch (JsonException exception)
        {
            throw new AdapterHttpException(StatusCodes.Status502BadGateway, "LM Studio returned invalid JSON.", exception);
        }
    }

    private static async Task<string> SafeReadContentAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            return string.IsNullOrWhiteSpace(text) ? "No response body." : text;
        }
        catch
        {
            return "Unable to read LM Studio error response.";
        }
    }

    private static int MapStatusCode(HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            HttpStatusCode.BadRequest => StatusCodes.Status400BadRequest,
            HttpStatusCode.Unauthorized => StatusCodes.Status502BadGateway,
            HttpStatusCode.Forbidden => StatusCodes.Status502BadGateway,
            HttpStatusCode.NotFound => StatusCodes.Status502BadGateway,
            HttpStatusCode.TooManyRequests => StatusCodes.Status429TooManyRequests,
            _ when (int)statusCode >= 500 => StatusCodes.Status502BadGateway,
            _ => StatusCodes.Status502BadGateway
        };
    }
}

internal sealed class AdapterMapper(IOptions<AdapterOptions> adapterOptions)
{
    private readonly AdapterOptions _options = adapterOptions.Value;

    public AdapterErrorResponse? ValidateGenerateRequest(OllamaGenerateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Model))
        {
            return AdapterErrorResponse.FromMessage("The 'model' field is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            return AdapterErrorResponse.FromMessage("The 'prompt' field is required.");
        }

        if (request.Raw)
        {
            return AdapterErrorResponse.FromMessage("The 'raw' option is not supported by this adapter.");
        }

        if (request.Images is { Length: > 0 })
        {
            return AdapterErrorResponse.FromMessage("The 'images' option is not supported by this adapter.");
        }

        if (request.Suffix is not null)
        {
            return AdapterErrorResponse.FromMessage("The 'suffix' option is not supported by this adapter.");
        }

        if (request.Format is not null && !string.Equals(request.Format, "json", StringComparison.OrdinalIgnoreCase))
        {
            return AdapterErrorResponse.FromMessage("Only 'json' is supported for the 'format' option.");
        }

        return ValidateOptions(request.Options);
    }

    public AdapterErrorResponse? ValidateChatRequest(OllamaChatRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Model))
        {
            return AdapterErrorResponse.FromMessage("The 'model' field is required.");
        }

        if (request.Messages is null || request.Messages.Count == 0)
        {
            return AdapterErrorResponse.FromMessage("At least one chat message is required.");
        }

        if (request.Format is not null && !string.Equals(request.Format, "json", StringComparison.OrdinalIgnoreCase))
        {
            return AdapterErrorResponse.FromMessage("Only 'json' is supported for the 'format' option.");
        }

        return ValidateOptions(request.Options);
    }

    private static AdapterErrorResponse? ValidateOptions(OllamaRequestOptions? options)
    {
        if (options is null)
        {
            return null;
        }

        if (options.Seed is not null)
        {
            return AdapterErrorResponse.FromMessage("The 'options.seed' setting is not supported by this adapter.");
        }

        if (options.NumGpu is not null || options.NumThread is not null || options.NumCtx is not null)
        {
            return AdapterErrorResponse.FromMessage("Hardware and context tuning options are not supported by this adapter.");
        }

        return null;
    }

    public LmStudioCompletionRequest MapGenerateRequest(OllamaGenerateRequest request)
    {
        return new LmStudioCompletionRequest(
            request.Model,
            request.Prompt,
            request.Stream,
            MapResponseFormat(request.Format),
            request.Options?.Temperature,
            request.Options?.TopP,
            request.Options?.Stop,
            request.Options?.NumPredict ?? _options.DefaultGenerateMaxTokens);
    }

    public LmStudioChatCompletionRequest MapChatRequest(OllamaChatRequest request)
    {
        var toolCallIdsByName = new Dictionary<string, Queue<string>>(StringComparer.Ordinal);
        var messages = request.Messages
            .Select((message, index) => MapChatMessage(message, index, toolCallIdsByName))
            .ToArray();

        return new LmStudioChatCompletionRequest(
            request.Model,
            messages,
            request.Stream,
            MapResponseFormat(request.Format),
            request.Options?.Temperature,
            request.Options?.TopP,
            request.Options?.Stop,
            request.Options?.NumPredict ?? _options.DefaultChatMaxTokens,
            request.Tools?.ToArray());
    }

    private static LmStudioChatMessage MapChatMessage(
        OllamaChatMessage message,
        int messageIndex,
        Dictionary<string, Queue<string>> toolCallIdsByName)
    {
        var toolCalls = message.ToolCalls?
            .Select((toolCall, toolIndex) => MapToolCall(toolCall, messageIndex, toolIndex, toolCallIdsByName))
            .ToArray();

        if (string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase))
        {
            return new LmStudioChatMessage(
                message.Role,
                message.Content,
                ToolCalls: null,
                ToolName: message.ToolName,
                ToolCallId: DequeueToolCallId(toolCallIdsByName, message.ToolName));
        }

        return new LmStudioChatMessage(message.Role, message.Content, toolCalls);
    }

    private static LmStudioToolCall MapToolCall(
        OllamaToolCall toolCall,
        int messageIndex,
        int toolIndex,
        Dictionary<string, Queue<string>> toolCallIdsByName)
    {
        var toolCallId = $"call_{messageIndex}_{toolCall.Function.Index ?? toolIndex}_{toolCall.Function.Name}";
        if (!toolCallIdsByName.TryGetValue(toolCall.Function.Name, out var queue))
        {
            queue = new Queue<string>();
            toolCallIdsByName[toolCall.Function.Name] = queue;
        }

        queue.Enqueue(toolCallId);

        return new LmStudioToolCall(
            toolCallId,
            string.IsNullOrWhiteSpace(toolCall.Type) ? "function" : toolCall.Type,
            new LmStudioToolFunctionCall(
                toolCall.Function.Name,
                SerializeToolArguments(toolCall.Function.Arguments)));
    }

    private static string? DequeueToolCallId(Dictionary<string, Queue<string>> toolCallIdsByName, string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return null;
        }

        return toolCallIdsByName.TryGetValue(toolName, out var queue) && queue.Count > 0
            ? queue.Dequeue()
            : null;
    }

    private static string SerializeToolArguments(JsonNode? arguments)
    {
        if (arguments is null)
        {
            return "{}";
        }

        return arguments.ToJsonString();
    }

    private static JsonObject? MapResponseFormat(string? format)
    {
        return string.Equals(format, "json", StringComparison.OrdinalIgnoreCase)
            ? new JsonObject { ["type"] = "json_object" }
            : null;
    }

    public OllamaTagsResponse MapTags(LmStudioModelsResponse response)
    {
        var models = response.Data.Select(model => new OllamaModelTag(
            model.Id,
            model.Id,
            DateTimeOffset.TryParse(model.CreatedText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var modifiedAt)
                ? modifiedAt
                : DateTimeOffset.UtcNow,
            $"sha256:{model.Id.ToLowerInvariant().GetHashCode():x8}",
            0,
            new OllamaModelDetails(
                Format: "gguf",
                Family: "lmstudio",
                Families: ["lmstudio"],
                ParameterSize: "unknown",
                QuantizationLevel: "unknown")))
            .ToArray();

        return new OllamaTagsResponse(models);
    }

    public OllamaShowResponse MapShow(LmStudioModel model)
    {
        const int contextLength = 65536;
        const string architecture = "lmstudio";

        return new OllamaShowResponse(
            License: "unknown",
            Modelfile: string.Empty,
            Parameters: string.Empty,
            Template: string.Empty,
            Details: new OllamaModelDetails(
                Format: "gguf",
                Family: architecture,
                Families: [architecture],
                ParameterSize: "unknown",
                QuantizationLevel: "unknown"),
            ModelInfo: new Dictionary<string, JsonNode?>
            {
                ["general.architecture"] = architecture,
                [$"{architecture}.context_length"] = contextLength,
                ["general.basename"] = model.Id
            },
            Capabilities: ["tools"]);
    }

    public OpenAiModelsResponse MapOpenAiModels(LmStudioModelsResponse response)
    {
        var models = response.Data.Select(model => new OpenAiModel(
            Id: model.Id,
            Created: model.Created,
            OwnedBy: model.OwnedBy))
            .ToArray();

        return new OpenAiModelsResponse(models);
    }

    public OllamaGenerateResponse MapGenerateResponse(string model, LmStudioCompletionResponse response)
    {
        var choice = response.Choices.FirstOrDefault();
        var content = choice?.Text ?? string.Empty;
        return new OllamaGenerateResponse(
            Model: model,
            CreatedAt: DateTimeOffset.UtcNow,
            Response: content,
            Done: true,
            DoneReason: NormalizeFinishReason(choice?.FinishReason),
            Context: null,
            TotalDuration: null,
            LoadDuration: null,
            PromptEvalCount: response.Usage?.PromptTokens,
            PromptEvalDuration: null,
            EvalCount: response.Usage?.CompletionTokens,
            EvalDuration: null);
    }

    public OllamaChatResponse MapChatResponse(string model, LmStudioChatCompletionResponse response)
    {
        var choice = response.Choices.FirstOrDefault();
        return new OllamaChatResponse(
            Model: model,
            CreatedAt: DateTimeOffset.UtcNow,
            Message: new OllamaChatMessage(
                "assistant",
                choice?.Message?.Content,
                MapToolCalls(choice?.Message?.ToolCalls)),
            Done: true,
            DoneReason: NormalizeFinishReason(choice?.FinishReason),
            TotalDuration: null,
            LoadDuration: null,
            PromptEvalCount: response.Usage?.PromptTokens,
            PromptEvalDuration: null,
            EvalCount: response.Usage?.CompletionTokens,
            EvalDuration: null);
    }

    private static string NormalizeFinishReason(string? finishReason)
    {
        return finishReason switch
        {
            null or "stop" => "stop",
            "length" => "length",
            _ => finishReason
        };
    }

    private static List<OllamaToolCall>? MapToolCalls(IReadOnlyList<LmStudioToolCall>? toolCalls)
    {
        if (toolCalls is not { Count: > 0 })
        {
            return null;
        }

        return toolCalls
            .Select((toolCall, index) => new OllamaToolCall(
                string.IsNullOrWhiteSpace(toolCall.Type) ? "function" : toolCall.Type,
                new OllamaToolFunctionCall(
                    index,
                    toolCall.Function.Name,
                    ParseToolArguments(toolCall.Function.Arguments))))
            .ToList();
    }

    internal static JsonNode? ParseToolArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(arguments);
        }
        catch (JsonException)
        {
            return JsonValue.Create(arguments);
        }
    }
}

internal static class StreamAdapters
{
    public static async Task CopyToAsync(
        DownstreamStream downstream,
        Stream responseStream,
        CancellationToken cancellationToken)
    {
        await using var managed = downstream;
        await downstream.Stream.CopyToAsync(responseStream, cancellationToken);
    }

    public static async Task WriteGenerateStreamAsync(
        DownstreamStream downstream,
        Stream responseStream,
        string model,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken)
    {
        await using var managed = downstream;
        await using var writer = new StreamWriter(responseStream, new UTF8Encoding(false), leaveOpen: true);
        int? promptTokens = null;
        int? completionTokens = null;
        string? finishReason = null;

        await foreach (var payload in EnumerateSsePayloadsAsync(downstream.Stream, cancellationToken))
        {
            if (string.Equals(payload, "[DONE]", StringComparison.Ordinal))
            {
                break;
            }

            var chunk = JsonSerializer.Deserialize<LmStudioCompletionChunk>(payload, jsonOptions);
            if (chunk is null)
            {
                continue;
            }

            var choice = chunk.Choices.FirstOrDefault();
            var text = choice?.Text ?? string.Empty;
            finishReason ??= choice?.FinishReason;
            promptTokens ??= chunk.Usage?.PromptTokens;
            completionTokens = chunk.Usage?.CompletionTokens ?? completionTokens;

            if (!string.IsNullOrEmpty(text))
            {
                var mappedChunk = new OllamaGenerateResponse(
                    model,
                    DateTimeOffset.UtcNow,
                    text,
                    false,
                    null,
                    null,
                    null,
                    null,
                    promptTokens,
                    null,
                    completionTokens,
                    null);

                await writer.WriteLineAsync(JsonSerializer.Serialize(mappedChunk, jsonOptions));
                await writer.FlushAsync(cancellationToken);
            }
        }

        var finalChunk = new OllamaGenerateResponse(
            model,
            DateTimeOffset.UtcNow,
            string.Empty,
            true,
            finishReason ?? "stop",
            null,
            null,
            null,
            promptTokens,
            null,
            completionTokens,
            null);

        await writer.WriteLineAsync(JsonSerializer.Serialize(finalChunk, jsonOptions));
        await writer.FlushAsync(cancellationToken);
    }

    public static async Task WriteChatStreamAsync(
        DownstreamStream downstream,
        Stream responseStream,
        string model,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken)
    {
        await using var managed = downstream;
        await using var writer = new StreamWriter(responseStream, new UTF8Encoding(false), leaveOpen: true);
        int? promptTokens = null;
        int? completionTokens = null;
        string? finishReason = null;
        var toolCalls = new Dictionary<int, StreamingToolCallAccumulator>();

        await foreach (var payload in EnumerateSsePayloadsAsync(downstream.Stream, cancellationToken))
        {
            if (string.Equals(payload, "[DONE]", StringComparison.Ordinal))
            {
                break;
            }

            var chunk = JsonSerializer.Deserialize<LmStudioChatCompletionChunk>(payload, jsonOptions);
            if (chunk is null)
            {
                continue;
            }

            var choice = chunk.Choices.FirstOrDefault();
            var text = choice?.Delta?.Content ?? string.Empty;
            finishReason ??= choice?.FinishReason;
            promptTokens ??= chunk.Usage?.PromptTokens;
            completionTokens = chunk.Usage?.CompletionTokens ?? completionTokens;

            AccumulateToolCalls(choice?.Delta?.ToolCalls, toolCalls);

            if (!string.IsNullOrEmpty(text))
            {
                var mappedChunk = new OllamaChatResponse(
                    model,
                    DateTimeOffset.UtcNow,
                    new OllamaChatMessage("assistant", text),
                    false,
                    null,
                    null,
                    null,
                    promptTokens,
                    null,
                    completionTokens,
                    null);

                await writer.WriteLineAsync(JsonSerializer.Serialize(mappedChunk, jsonOptions));
                await writer.FlushAsync(cancellationToken);
            }
        }

        var accumulatedToolCalls = BuildOllamaToolCalls(toolCalls);
        if (accumulatedToolCalls is { Count: > 0 })
        {
            var mappedToolChunk = new OllamaChatResponse(
                model,
                DateTimeOffset.UtcNow,
                new OllamaChatMessage("assistant", null, accumulatedToolCalls),
                false,
                null,
                null,
                null,
                promptTokens,
                null,
                completionTokens,
                null);

            await writer.WriteLineAsync(JsonSerializer.Serialize(mappedToolChunk, jsonOptions));
            await writer.FlushAsync(cancellationToken);
        }

        var finalChunk = new OllamaChatResponse(
            model,
            DateTimeOffset.UtcNow,
            new OllamaChatMessage("assistant", string.Empty),
            true,
            finishReason ?? "stop",
            null,
            null,
            promptTokens,
            null,
            completionTokens,
            null);

        await writer.WriteLineAsync(JsonSerializer.Serialize(finalChunk, jsonOptions));
        await writer.FlushAsync(cancellationToken);
    }

    private static void AccumulateToolCalls(
        IReadOnlyList<LmStudioStreamingToolCall>? deltas,
        Dictionary<int, StreamingToolCallAccumulator> toolCalls)
    {
        if (deltas is not { Count: > 0 })
        {
            return;
        }

        foreach (var delta in deltas)
        {
            var index = delta.Index ?? 0;
            if (!toolCalls.TryGetValue(index, out var accumulator))
            {
                accumulator = new StreamingToolCallAccumulator(index);
                toolCalls[index] = accumulator;
            }

            accumulator.Apply(delta);
        }
    }

    private static List<OllamaToolCall>? BuildOllamaToolCalls(Dictionary<int, StreamingToolCallAccumulator> toolCalls)
    {
        if (toolCalls.Count == 0)
        {
            return null;
        }

        return toolCalls
            .OrderBy(pair => pair.Key)
            .Select(pair => new OllamaToolCall(
                pair.Value.Type ?? "function",
                new OllamaToolFunctionCall(
                    pair.Key,
                    pair.Value.Name ?? string.Empty,
                    AdapterMapper.ParseToolArguments(pair.Value.Arguments.ToString()))))
            .ToList();
    }

    private static async IAsyncEnumerable<string> EnumerateSsePayloadsAsync(Stream stream, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream);
        var builder = new StringBuilder();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                if (builder.Length > 0)
                {
                    yield return builder.ToString();
                    builder.Clear();
                }

                continue;
            }

            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(line[5..].TrimStart());
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }
}

internal static class RequestHelpers
{
    public static bool GetStreamFlag(JsonObject request)
    {
        if (request["stream"] is null)
        {
            return false;
        }

        try
        {
            return request["stream"]?.GetValue<bool>() ?? false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

internal static class JsonOptionsFactory
{
    public static JsonSerializerOptions Create()
    {
        return new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = false
        };
    }
}

internal sealed record DownstreamStream(HttpResponseMessage Response, Stream Stream) : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        await Stream.DisposeAsync();
        Response.Dispose();
    }
}

internal sealed class AdapterHttpException(int statusCode, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public int StatusCode { get; } = statusCode;
}

internal sealed class LmStudioOptions
{
    public const string SectionName = "LmStudio";

    public string BaseUrl { get; set; } = "http://localhost:1234/v1/";
    public int TimeoutSeconds { get; set; } = 300;
    public string? ApiKey { get; set; }
}

internal sealed class AdapterOptions
{
    public const string SectionName = "Adapter";

    public string Version { get; set; } = "0.21.0";
    public int DefaultGenerateMaxTokens { get; set; } = 512;
    public int DefaultChatMaxTokens { get; set; } = 512;
}

internal sealed record AdapterErrorResponse(string Error)
{
    public static AdapterErrorResponse FromMessage(string message) => new(message);
}

internal sealed record OpenAiModelsResponse(
    [property: JsonPropertyName("data")] OpenAiModel[] Data,
    [property: JsonPropertyName("object")] string Object = "list");

internal sealed record OpenAiModel(
    string Id,
    long Created,
    [property: JsonPropertyName("owned_by")] string OwnedBy,
    [property: JsonPropertyName("object")] string Object = "model");

internal sealed record OllamaTagsResponse(OllamaModelTag[] Models);

internal sealed record OllamaModelTag(
    string Name,
    string Model,
    DateTimeOffset ModifiedAt,
    string Digest,
    long Size,
    OllamaModelDetails Details);

internal sealed record OllamaShowRequest(string Model);

internal sealed record OllamaShowResponse(
    string License,
    string Modelfile,
    string Parameters,
    string Template,
    OllamaModelDetails Details,
    [property: JsonPropertyName("model_info")] Dictionary<string, JsonNode?> ModelInfo,
    string[] Capabilities,
    [property: JsonPropertyName("modified_at")] DateTimeOffset? ModifiedAt = null,
    [property: JsonPropertyName("size")] long? Size = null,
    [property: JsonPropertyName("digest")] string? Digest = null);

internal sealed record OllamaModelDetails(
    string Format,
    string Family,
    string[] Families,
    string ParameterSize,
    string QuantizationLevel);

internal sealed record OllamaGenerateRequest(
    string Model,
    string Prompt,
    bool Stream = true,
    string? Format = null,
    OllamaRequestOptions? Options = null,
    bool Raw = false,
    string? Suffix = null,
    string[]? Images = null);

internal sealed record OllamaGenerateResponse(
    string Model,
    DateTimeOffset CreatedAt,
    string Response,
    bool Done,
    string? DoneReason,
    int[]? Context,
    long? TotalDuration,
    long? LoadDuration,
    int? PromptEvalCount,
    long? PromptEvalDuration,
    int? EvalCount,
    long? EvalDuration);

internal sealed record OllamaChatRequest(
    string Model,
    List<OllamaChatMessage> Messages,
    bool Stream = true,
    string? Format = null,
    OllamaRequestOptions? Options = null,
    List<JsonObject>? Tools = null);

internal sealed record OllamaChatResponse(
    string Model,
    DateTimeOffset CreatedAt,
    OllamaChatMessage Message,
    bool Done,
    string? DoneReason,
    long? TotalDuration,
    long? LoadDuration,
    int? PromptEvalCount,
    long? PromptEvalDuration,
    int? EvalCount,
    long? EvalDuration);

internal sealed record OllamaChatMessage(
    string Role,
    string? Content,
    List<OllamaToolCall>? ToolCalls = null,
    string? ToolName = null);

internal sealed record OllamaToolCall(
    string Type,
    OllamaToolFunctionCall Function);

internal sealed record OllamaToolFunctionCall(
    int? Index,
    string Name,
    JsonNode? Arguments);

internal sealed record OllamaRequestOptions(
    float? Temperature = null,
    float? TopP = null,
    int? NumPredict = null,
    string[]? Stop = null,
    int? Seed = null,
    int? NumGpu = null,
    int? NumThread = null,
    int? NumCtx = null);

internal sealed record LmStudioModelsResponse(LmStudioModel[] Data);

internal sealed record LmStudioModel(string Id, long Created, [property: JsonPropertyName("owned_by")] string OwnedBy)
{
    public string CreatedText => DateTimeOffset.FromUnixTimeSeconds(Created).UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}

internal sealed record LmStudioCompletionRequest(
    string Model,
    string Prompt,
    bool Stream,
    [property: JsonPropertyName("response_format")] JsonObject? ResponseFormat,
    float? Temperature,
    [property: JsonPropertyName("top_p")] float? TopP,
    string[]? Stop,
    [property: JsonPropertyName("max_tokens")] int? MaxTokens);

internal sealed record LmStudioCompletionResponse(
    string Id,
    LmStudioCompletionChoice[] Choices,
    LmStudioUsage? Usage);

internal sealed record LmStudioCompletionChoice(
    string Text,
    [property: JsonPropertyName("finish_reason")] string? FinishReason);

internal sealed record LmStudioCompletionChunk(
    LmStudioCompletionChunkChoice[] Choices,
    LmStudioUsage? Usage);

internal sealed record LmStudioCompletionChunkChoice(
    string Text,
    [property: JsonPropertyName("finish_reason")] string? FinishReason);

internal sealed record LmStudioChatCompletionRequest(
    string Model,
    LmStudioChatMessage[] Messages,
    bool Stream,
    [property: JsonPropertyName("response_format")] JsonObject? ResponseFormat,
    float? Temperature,
    [property: JsonPropertyName("top_p")] float? TopP,
    string[]? Stop,
    [property: JsonPropertyName("max_tokens")] int? MaxTokens,
    JsonObject[]? Tools = null);

internal sealed record LmStudioChatCompletionResponse(
    string Id,
    LmStudioChatChoice[] Choices,
    LmStudioUsage? Usage);

internal sealed record LmStudioChatChoice(
    LmStudioChatMessage Message,
    [property: JsonPropertyName("finish_reason")] string? FinishReason);

internal sealed record LmStudioChatCompletionChunk(
    LmStudioChatChunkChoice[] Choices,
    LmStudioUsage? Usage);

internal sealed record LmStudioChatChunkChoice(
    LmStudioChatDelta Delta,
    [property: JsonPropertyName("finish_reason")] string? FinishReason);

internal sealed record LmStudioChatMessage(
    string Role,
    string? Content,
    LmStudioToolCall[]? ToolCalls = null,
    [property: JsonPropertyName("tool_name")] string? ToolName = null,
    [property: JsonPropertyName("tool_call_id")] string? ToolCallId = null);

internal sealed record LmStudioChatDelta(
    string? Role,
    string? Content,
    LmStudioStreamingToolCall[]? ToolCalls = null);

internal sealed record LmStudioToolCall(
    string Id,
    string Type,
    LmStudioToolFunctionCall Function);

internal sealed record LmStudioToolFunctionCall(
    string Name,
    string Arguments);

internal sealed record LmStudioStreamingToolCall(
    int? Index,
    string? Id,
    string? Type,
    LmStudioStreamingToolFunctionCall? Function);

internal sealed record LmStudioStreamingToolFunctionCall(
    string? Name,
    string? Arguments);

internal sealed class StreamingToolCallAccumulator(int index)
{
    public int Index { get; } = index;
    public string? Id { get; private set; }
    public string? Type { get; private set; }
    public string? Name { get; private set; }
    public StringBuilder Arguments { get; } = new();

    public void Apply(LmStudioStreamingToolCall delta)
    {
        Id ??= delta.Id;
        Type ??= delta.Type;

        if (!string.IsNullOrWhiteSpace(delta.Function?.Name))
        {
            Name = delta.Function.Name;
        }

        if (!string.IsNullOrEmpty(delta.Function?.Arguments))
        {
            Arguments.Append(delta.Function.Arguments);
        }
    }
}

internal sealed record LmStudioUsage(
    [property: JsonPropertyName("prompt_tokens")] int? PromptTokens,
    [property: JsonPropertyName("completion_tokens")] int? CompletionTokens,
    [property: JsonPropertyName("total_tokens")] int? TotalTokens);
