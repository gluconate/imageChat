using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;

namespace ImageChat;

public sealed record TextChatRequest([property: JsonRequired] string Prompt, string Model = ChatModelOptions.ModelId);

public static class TextChatEndpoints
{
    public static object Status(ChatModelOptions options) => new
    {
        id = ChatModelOptions.ModelId, label = "GPT-5.6-sol", mode = "chat",
        configured = options.ConfigurationError() == null, deployment = options.Deployment,
        authMode = options.AuthMode, streaming = options.Streaming, supportsImages = options.SupportsImages,
        maxPromptCharacters = options.MaxPromptCharacters, maxContextCharacters = options.MaxContextCharacters,
        maxContextMessages = options.MaxContextMessages, maxContextImages = options.MaxContextImages,
        maxContextImageBytes = options.MaxContextImageBytes,
        message = options.ConfigurationError() ?? "Configuration ready. Deployment capabilities are verified on your first request."
    };

    public static void MapTextChat(this WebApplication app) => app.MapPost("/api/chats/{id}/text-messages", Handle)
        .RequireAuthorization().RequireRateLimiting("chat");

    private static async Task Handle(string id, HttpContext context, IChatStore store, ChatOperations operations,
        ITextChatGenerator generator, ChatModelOptions options, IHostApplicationLifetime lifetime, ILoggerFactory logs)
    {
        var owner = context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var chat = store.Get(id, owner);
        using var lease = operations.Acquire(id);
        // The persisted cloud Busy field also guards cross-instance concurrency in BeginTurn.
        if (chat.Messages.Any(message => message.Status == "pending"))
            throw new AppException(409, "This chat already has an outstanding turn. Use another chat while it finishes.");
        if (options.ConfigurationError() is { } configurationError) throw new AppException(503, configurationError);
        string prompt;
        string model;
        ImageData? attachment = null;
        if (context.Request.HasFormContentType)
        {
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            prompt = form["prompt"].ToString();
            model = form.ContainsKey("model") ? form["model"].ToString() : ChatModelOptions.ModelId;
            if (form.Files.Count > 1 || (form.Files.Count == 1 && form.Files[0].Name != "image"))
                throw new AppException(400, "Attach one PNG or JPEG per chat message.");
            if (form.Files.GetFile("image") is { } file)
            {
                if (file.Length > InputRules.MaxImageBytes) throw new AppException(400, "Images must be 20 MB or smaller.");
                using var bytes = new MemoryStream();
                await file.CopyToAsync(bytes, context.RequestAborted);
                attachment = InputRules.ValidateImage(bytes.ToArray());
            }
        }
        else if (context.Request.HasJsonContentType())
        {
            var input = await context.Request.ReadFromJsonAsync<TextChatRequest>(context.RequestAborted)
                ?? throw new AppException(400, "Supply a chat prompt.");
            prompt = input.Prompt;
            model = input.Model;
        }
        else throw new AppException(400, "Use JSON for text or multipart form data for a prompt and image.");
        if (model != ChatModelOptions.ModelId) throw new AppException(400, "Choose the supported chat model.");
        prompt = TextChatContext.ValidatePrompt(prompt, attachment != null, options);
        var requestId = context.Items["telemetryId"] as string;
        if (requestId != null) store.SetRequestModel(requestId, ChatModelOptions.ModelId, options.Deployment);
        var history = TextChatContext.Build(chat, prompt, attachment, store, owner, options);
        context.RequestAborted.ThrowIfCancellationRequested();
        var assistantId = store.BeginTurn(chat, prompt, "text", "text", attachment, requestId, "chat", ChatModelOptions.ModelId, options.Deployment);
        var responseText = new StringBuilder();
        var disconnected = false;
        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        async Task Send(string type, object data)
        {
            if (disconnected) return;
            try
            {
                // Never let a slow/disconnected reader keep a billable operation or lease alive indefinitely.
                using var write = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping);
                write.CancelAfter(TimeSpan.FromSeconds(5));
                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                await context.Response.WriteAsync($"event: {type}\ndata: {json}\n\n", write.Token);
                await context.Response.Body.FlushAsync(write.Token);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException)
            {
                disconnected = true; // Continue to persist a result for reload/recovery; do not retry the POST.
            }
        }

        // Only include the current pair in each envelope. Long prompts/history could exceed
        // event limits; the browser already owns the prompt and merges this explicit snapshot.
        object Envelope(Chat saved, string? error = null) => new
        {
            messageId = assistantId, partialHistory = true, contextTruncated = history.Truncated, error,
            chat = saved with { Messages = saved.Messages.TakeLast(2).Select(message =>
                message.Role == "user" ? message with { Text = "" } : message).ToList() }
        };

        try
        {
            await Send("started", Envelope(store.Get(id, owner)));
            var completed = false;
            await foreach (var item in generator.GenerateAsync(history.Input, timeout.Token).WithCancellation(timeout.Token))
            {
                if (item.Type == "delta")
                {
                    if (item.Text == null || item.Text.Length > ResponsesChatClient.MaxResponseCharacters - responseText.Length)
                        throw new AppException(502, "The chat reply exceeded the application output limit.");
                    responseText.Append(item.Text);
                    if (stopwatch.ElapsedMilliseconds >= 500)
                    {
                        store.UpdateTextTurn(id, assistantId, responseText.ToString());
                        stopwatch.Restart();
                    }
                    await Send("delta", new { messageId = assistantId, text = item.Text });
                }
                else if (item.Type == "completed")
                {
                    if (string.IsNullOrEmpty(item.Text) || item.Text.Length > ResponsesChatClient.MaxResponseCharacters)
                        throw new AppException(502, "The chat provider returned an invalid final reply.");
                    store.FinishTextTurn(id, assistantId, item.Text, null);
                    completed = true;
                    await Send("completed", Envelope(store.Get(id, owner)));
                    break;
                }
                else throw new AppException(502, "The chat provider returned an unexpected event.");
            }
            if (!completed) throw new AppException(502, "The chat stream ended before confirming a completed reply.");
        }
        catch (Exception error)
        {
            var status = error is OperationCanceledException ? 504 : 502;
            var safeError = error is OperationCanceledException
                ? "The chat request timed out or the server stopped. The partial reply is saved. Check history before retrying; another request may incur another charge."
                : "The chat response failed or was interrupted. The partial reply is saved. Check history before retrying; another request may incur another charge.";
            logs.CreateLogger("TextChat").LogWarning("Chat request failed with {Status}; exception type {Type}", status, error.GetType().Name);
            store.FinishTextTurn(id, assistantId, responseText.ToString(), safeError, status);
            await Send("failed", Envelope(store.Get(id, owner), safeError));
        }
    }
}