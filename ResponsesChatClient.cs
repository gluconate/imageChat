using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;

namespace ImageChat;

public sealed class ChatModelOptions
{
    public const string ModelId = "gpt-5.6-sol";
    private const string ResponsesPath = "/openai/v1/responses";

    public string Endpoint { get; set; } = "https://embedding-0483-resource.services.ai.azure.com/openai/v1/responses";
    public string Deployment { get; set; } = ModelId;
    public string AuthMode { get; set; } = "ApiKey";
    public string ApiKey { get; set; } = "";
    public string TokenScope { get; set; } = "https://cognitiveservices.azure.com/.default";
    public int TimeoutSeconds { get; set; } = 180;
    public int MaxOutputTokens { get; set; } = 2048;
    public bool Streaming { get; set; } = true;
    // Expose only operator-approved values for this deployment. "default" in the UI
    // means omit reasoning entirely, not disable reasoning with effort "none".
    public string[] ReasoningEfforts { get; set; } = ["low", "medium", "high"];
    private static readonly string[] KnownReasoningEfforts = ["none", "minimal", "low", "medium", "high", "xhigh", "max"];

    // Application resource budgets, not claims about the deployment's token window.
    // Input characters and image bytes are independent of the output/storage limit.
    public int MaxPromptCharacters { get; set; } = 64000;
    public int MaxContextCharacters { get; set; } = 256000;
    public int MaxContextMessages { get; set; } = 200;
    public int MaxContextImages { get; set; } = 8;
    public int MaxContextImageBytes { get; set; } = 32 * 1024 * 1024;
    public bool SupportsImages { get; set; } = true;

    public string? ConfigurationError()
    {
        if (!TryRequestUri(out _)) return "Chat endpoint must be an HTTPS root or exact /openai/v1/responses URL, without credentials, query, or fragment.";
        if (string.IsNullOrWhiteSpace(Deployment) || Deployment.Any(char.IsControl)) return "Set a valid chat deployment.";
        if (AuthMode is not ("ApiKey" or "DefaultAzureCredential")) return "Chat authentication must be ApiKey or DefaultAzureCredential.";
        if (AuthMode == "ApiKey" && (string.IsNullOrWhiteSpace(ApiKey) || ApiKey.Any(c => c < '!' || c > '~')))
            return "Set a valid chat API key.";
        if (AuthMode == "DefaultAzureCredential" && (string.IsNullOrWhiteSpace(TokenScope) || TokenScope.Any(char.IsWhiteSpace) || TokenScope.Any(char.IsControl)))
            return "Set a valid chat token scope.";
        if (TimeoutSeconds is < 30 or > 210) return "Chat timeout must be between 30 and 210 seconds.";
        if (MaxOutputTokens <= 0) return "Chat maximum output tokens must be positive.";
        if (ReasoningConfigurationError() is { } reasoningError) return reasoningError;
        return ContextConfigurationError();
    }

    private string? ReasoningConfigurationError() => ReasoningEfforts == null ||
        ReasoningEfforts.Length > KnownReasoningEfforts.Length ||
        ReasoningEfforts.Distinct(StringComparer.Ordinal).Count() != ReasoningEfforts.Length ||
        ReasoningEfforts.Any(effort => !KnownReasoningEfforts.Contains(effort))
            ? "Chat reasoning efforts must be unique, supported API effort names." : null;

    public string? ValidateReasoningEffort(string? effort)
    {
        if (ReasoningConfigurationError() is { } error) throw new AppException(503, error);
        if (effort is null or "default") return null;
        if (!ReasoningEfforts.Contains(effort, StringComparer.Ordinal))
            throw new AppException(400, "Choose a thinking effort supported by this chat deployment.");
        return effort;
    }

    // Context construction does not need provider credentials or an endpoint.
    internal string? ContextConfigurationError()
    {
        if (MaxPromptCharacters is < 1 or > 256000) return "Chat prompt character budget must be between 1 and 256,000.";
        if (MaxContextCharacters < MaxPromptCharacters || MaxContextCharacters > 1000000)
            return "Chat context character budget must cover the prompt budget and be at most 1,000,000.";
        if (MaxContextMessages is < 2 or > 1000) return "Chat context message budget must be between 2 and 1,000.";
        if (MaxContextImages is < 1 or > 16) return "Chat context image budget must be between 1 and 16.";
        if (MaxContextImageBytes is < 20 * 1024 * 1024 or > 64 * 1024 * 1024)
            return "Chat context image byte budget must be between 20 MB and 64 MB.";
        return null;
    }

    public Uri RequestUri() => TryRequestUri(out var uri)
        ? uri!
        : throw new AppException(503, "The chat endpoint is not configured correctly.");

    private bool TryRequestUri(out Uri? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(Endpoint) || !Endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            Endpoint.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '\\' or '?' or '#' or '@') ||
            !Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.Host.Length == 0 || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return false;

        // Check the original path too: Uri normalizes dot segments and some escaped paths.
        var pathStart = Endpoint.IndexOf('/', "https://".Length);
        var path = pathStart < 0 ? "" : Endpoint[pathStart..];
        if (path is not ("" or "/" or ResponsesPath)) return false;
        result = new Uri(uri.GetLeftPart(UriPartial.Authority) + ResponsesPath);
        return true;
    }
}

public record ChatInputMessage(string Role, string Text, IReadOnlyList<ImageData>? Images = null);
public record TextChatInput(IReadOnlyList<ChatInputMessage> Messages, string? ReasoningEffort = null);
public record TextChatEvent(string Type, string Text);

public interface ITextChatGenerator
{
    IAsyncEnumerable<TextChatEvent> GenerateAsync(TextChatInput input, CancellationToken cancellationToken);
}

/// <summary>
/// One Responses POST, with no retry, fallback, storage, or follow-up polling.
/// The parent configures HttpClient.Timeout = Timeout.InfiniteTimeSpan and disables
/// handler redirects/retries. Transport exceptions and cancellation are mapped by the parent.
/// </summary>
public sealed class ResponsesChatClient(HttpClient client, ChatModelOptions options, TokenCredential credential) : ITextChatGenerator
{
    public const int MaxResponseCharacters = 12000;
    private const int MaxProviderBytes = 2 * 1024 * 1024;
    private const int MaxEventDataBytes = 256 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async IAsyncEnumerable<TextChatEvent> GenerateAsync(TextChatInput input,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (options.ConfigurationError() is { } error) throw new AppException(503, error);
        // Never inherit credentials from another model's shared HttpClient.
        if (client.DefaultRequestHeaders.Contains("api-key") || client.DefaultRequestHeaders.Contains("Authorization"))
            throw new AppException(503, "Chat requires a client without default authentication headers.");
        var messages = ValidateInput(input);
        var reasoningEffort = options.ValidateReasoningEffort(input.ReasoningEffort);

        // Ownership and whole-turn history selection belong to the parent/context builder.
        // Recheck application budgets here before credentials, base64 encoding, or HTTP.
        var streaming = options.Streaming;
        var authMode = options.AuthMode;
        var apiKey = options.ApiKey;
        var scope = options.TokenScope;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 30, 210)));
        var token = deadline.Token;
        token.ThrowIfCancellationRequested();
        var requestPayload = new Dictionary<string, object>
        {
            ["model"] = options.Deployment,
            ["input"] = messages.Select(message => new { role = message.Role, content = MessageContent(message) }).ToArray(),
            ["stream"] = streaming,
            ["store"] = false,
            ["max_output_tokens"] = options.MaxOutputTokens
        };
        if (reasoningEffort != null) requestPayload["reasoning"] = new { effort = reasoningEffort };
        using var request = new HttpRequestMessage(HttpMethod.Post, options.RequestUri())
        {
            Content = JsonContent.Create(requestPayload)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(streaming ? "text/event-stream" : "application/json"));
        if (authMode == "ApiKey")
        {
            // Azure Responses REST uses api-key, not an API key in Authorization: Bearer.
            request.Headers.Add("api-key", apiKey);
        }
        else
        {
            AccessToken accessToken;
            try
            {
                accessToken = await credential.GetTokenAsync(new TokenRequestContext([scope]), token);
            }
            catch (AuthenticationFailedException)
            {
                throw new AppException(502, "Chat provider authentication failed.");
            }
            if (string.IsNullOrWhiteSpace(accessToken.Token) || accessToken.Token.Any(c => c < '!' || c > '~'))
                throw new AppException(502, "Chat provider authentication failed.");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode) throw InvalidResponse(); // Never read or expose provider error bodies.
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        var isJson = string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase);
        if (!isJson && (!streaming || !string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase)))
            throw InvalidResponse();
        if (response.Content.Headers.ContentLength is > MaxProviderBytes) throw InvalidResponse();
        using var stream = await response.Content.ReadAsStreamAsync(token);
        if (isJson)
        {
            using var body = new MemoryStream();
            await foreach (var chunk in ReadChunksAsync(stream, token)) body.Write(chunk.Span);
            var bytes = body.ToArray();
            var offset = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
            using var json = ParseJson(DecodeUtf8(bytes.AsSpan(offset)));
            var text = CompletedText(json.RootElement);
            token.ThrowIfCancellationRequested();
            yield return new TextChatEvent("completed", text);
            yield break;
        }

        var data = new StringBuilder();
        var hasData = false;
        var eventBytes = 0;
        var outputCharacters = 0;
        await foreach (var line in ReadLinesAsync(stream, token))
        {
            // Cancellation must also stop events already buffered by a previous read.
            token.ThrowIfCancellationRequested();
            if (line.Length == 0)
            {
                if (!hasData) continue;
                var payload = data.ToString();
                data.Clear();
                hasData = false;
                eventBytes = 0;
                // [DONE] is not proof of success. Only a validated response.completed is.
                if (payload.Trim() == "[DONE]") throw InvalidResponse();
                using var json = ParseJson(payload);
                var root = json.RootElement;
                var type = RequiredString(root, "type");
                if (string.IsNullOrWhiteSpace(type)) throw InvalidResponse();
                switch (type)
                {
                    case "response.output_text.delta":
                    case "response.refusal.delta":
                        var delta = RequiredString(root, "delta");
                        if (delta.Length > MaxResponseCharacters - outputCharacters) throw InvalidResponse();
                        outputCharacters += delta.Length;
                        yield return new TextChatEvent("delta", delta);
                        break;
                    case "response.completed":
                        var text = CompletedText(RequiredProperty(root, "response"));
                        token.ThrowIfCancellationRequested();
                        yield return new TextChatEvent("completed", text);
                        yield break;
                    case "response.failed":
                    case "response.incomplete":
                    case "response.error":
                    case "error":
                        throw InvalidResponse();
                    // Lifecycle, reasoning, tool calls, and *.done events are not visible text.
                }
                continue;
            }

            if (line[0] == ':') continue;
            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            if (field != "data") continue; // event/id/retry and unknown SSE fields are advisory.
            var value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' ')) value = value[1..];
            var size = StrictUtf8.GetByteCount(value) + (hasData ? 1 : 0);
            if (size > MaxEventDataBytes - eventBytes) throw InvalidResponse();
            eventBytes += size;
            if (hasData) data.Append('\n');
            data.Append(value);
            hasData = true;
        }
        // EOF (including an unterminated final event) must never commit partial output.
        throw InvalidResponse();
    }

    private IReadOnlyList<ChatInputMessage> ValidateInput(TextChatInput input)
    {
        if (input?.Messages is not { Count: > 0 } messages)
            throw new AppException(400, "Add at least one chat message.");
        if (messages.Count > options.MaxContextMessages)
            throw new AppException(400, "Chat context exceeds the message budget.");

        var result = new List<ChatInputMessage>(messages.Count);
        var characters = 0;
        var imageCount = 0;
        var imageBytes = 0;
        string? latestPrompt = null;
        foreach (var message in messages)
        {
            if (message is null || message.Role is not ("user" or "assistant") || message.Text is null)
                throw new AppException(400, "Chat messages must contain user or assistant text, with images only in user messages.");
            if (message.Text.Length > options.MaxContextCharacters - characters)
                throw new AppException(400, "Chat context exceeds the character budget.");
            characters += message.Text.Length;
            if (message.Role == "user") latestPrompt = message.Text;

            if (message.Images is not { Count: > 0 } images)
            {
                result.Add(message with { Images = null });
                continue;
            }
            if (!options.SupportsImages)
                throw new AppException(400, "Images are disabled for this chat model.");
            if (message.Role != "user")
                throw new AppException(400, "Images must be supplied as user references, not assistant messages.");
            if (images.Count > options.MaxContextImages - imageCount)
                throw new AppException(400, "Chat context exceeds the image count budget.");
            imageCount += images.Count;
            var validated = new List<ImageData>(images.Count);
            foreach (var image in images)
            {
                if (image?.Bytes is not { } bytes)
                    throw new AppException(400, "Supply a valid PNG or JPEG image.");
                if (bytes.Length > options.MaxContextImageBytes - imageBytes)
                    throw new AppException(400, "Chat context exceeds the image byte budget.");
                imageBytes += bytes.Length;
                // Trust the signature, never the supplied MIME type or a URL/file name.
                validated.Add(InputRules.ValidateImage(bytes));
            }
            result.Add(message with { Images = validated.ToArray() });
        }
        if (latestPrompt?.Length > options.MaxPromptCharacters)
            throw new AppException(400, "The current chat prompt exceeds the prompt character budget.");
        return result;
    }

    private static object MessageContent(ChatInputMessage message)
    {
        // Preserve the existing Responses string-content contract for text-only messages.
        if (message.Images is not { Count: > 0 } images) return message.Text;
        var content = new List<object>(images.Count + 1) { new { type = "input_text", text = message.Text } };
        foreach (var image in images)
            content.Add(new { type = "input_image", image_url = $"data:{image.ContentType};base64,{Convert.ToBase64String(image.Bytes)}", detail = "auto" });
        return content;
    }

    private static string CompletedText(JsonElement response)
    {
        if (RequiredString(response, "status") != "completed") throw InvalidResponse();
        var output = RequiredProperty(response, "output");
        if (output.ValueKind != JsonValueKind.Array) throw InvalidResponse();
        var text = new StringBuilder();
        foreach (var item in output.EnumerateArray())
        {
            if (RequiredString(item, "type") != "message") continue;
            if (RequiredString(item, "role") != "assistant") continue;
            var content = RequiredProperty(item, "content");
            if (content.ValueKind != JsonValueKind.Array) throw InvalidResponse();
            foreach (var part in content.EnumerateArray())
            {
                var visible = RequiredString(part, "type") switch
                {
                    "output_text" => RequiredString(part, "text"),
                    "refusal" => RequiredString(part, "refusal"),
                    _ => null
                };
                if (visible is null) continue;
                if (visible.Length > MaxResponseCharacters - text.Length) throw InvalidResponse();
                text.Append(visible);
            }
        }
        if (text.Length == 0) throw InvalidResponse();
        // Canonical final text replaces deltas; never append it to or double-count deltas.
        return text.ToString();
    }

    private static JsonElement RequiredProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value : throw InvalidResponse();

    private static string RequiredString(JsonElement element, string name)
    {
        var value = RequiredProperty(element, name);
        if (value.ValueKind != JsonValueKind.String) throw InvalidResponse();
        try { return value.GetString()!; }
        // JSON can be syntactically valid but contain an invalid escaped surrogate.
        catch (InvalidOperationException) { throw InvalidResponse(); }
    }

    private static JsonDocument ParseJson(string value)
    {
        try { return JsonDocument.Parse(value); }
        catch (JsonException) { throw InvalidResponse(); }
    }

    private static string DecodeUtf8(ReadOnlySpan<byte> bytes)
    {
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { throw InvalidResponse(); }
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(Stream stream,
        [EnumeratorCancellation] CancellationToken token)
    {
        var buffer = new byte[4096];
        var total = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(buffer.AsMemory(), token);
            if (read == 0) yield break;
            if (read > MaxProviderBytes - total) throw InvalidResponse();
            total += read;
            // The memory is valid until the next MoveNextAsync; consumers do not retain it.
            yield return buffer.AsMemory(0, read);
        }
    }

    private static async IAsyncEnumerable<string> ReadLinesAsync(Stream stream,
        [EnumeratorCancellation] CancellationToken token)
    {
        using var line = new MemoryStream();
        var previousCr = false;
        var firstLine = true;
        await foreach (var chunk in ReadChunksAsync(stream, token))
        {
            for (var index = 0; index < chunk.Length; index++)
            {
                var value = chunk.Span[index];
                if (previousCr && value == '\n') { previousCr = false; continue; }
                previousCr = value == '\r';
                if (value == '\r' || value == '\n')
                {
                    var decoded = DecodeUtf8(line.GetBuffer().AsSpan(0, (int)line.Length));
                    line.SetLength(0);
                    if (firstLine && decoded.StartsWith('\ufeff')) decoded = decoded[1..];
                    firstLine = false;
                    yield return decoded;
                }
                else line.WriteByte(value);
            }
        }
        if (line.Length > 0)
            yield return DecodeUtf8(line.GetBuffer().AsSpan(0, (int)line.Length));
    }

    private static AppException InvalidResponse() => new(502, "The chat provider returned an invalid or unsuccessful response.");
}