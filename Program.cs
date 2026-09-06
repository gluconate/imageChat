using System.Threading.RateLimiting;
using Azure.Core;
using Azure.Identity;
using ImageChat;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
// Repo-local secrets for development; environment variables always win in containers/cloud.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables().AddCommandLine(args);
builder.Services.AddSingleton(services => services.GetRequiredService<IConfiguration>()
    .GetSection("Foundry").Get<FoundryOptions>() ?? new());
builder.Services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential());
builder.Services.AddHttpClient<IImageGenerator, FoundryClient>(http => http.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton(services => new ChatStore(Path.GetFullPath(
    services.GetRequiredService<IConfiguration>()["Storage:Directory"] ?? "data",
    services.GetRequiredService<IWebHostEnvironment>().ContentRootPath)));
builder.Services.AddSingleton(new SemaphoreSlim(1, 1));
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = InputRules.MaxImageBytes + 1024 * 1024);
builder.Services.Configure<FormOptions>(f =>
{
    f.MultipartBodyLengthLimit = InputRules.MaxImageBytes + 1024 * 1024;
    f.ValueLengthLimit = 32000;
    // Avoid ASP.NET spooling uploaded private images to a machine-global temp directory.
    f.MemoryBufferThreshold = InputRules.MaxImageBytes + 1024 * 1024;
});
builder.Services.AddRateLimiter(rate =>
{
    rate.RejectionStatusCode = 429;
    rate.AddFixedWindowLimiter("generation", limiter =>
    {
        limiter.PermitLimit = 5;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
    rate.OnRejected = async (context, token) =>
        await context.HttpContext.Response.WriteAsJsonAsync(new { error = "Too many requests. Wait a minute before generating another image." }, token);
});
var app = builder.Build();
var options = app.Services.GetRequiredService<FoundryOptions>();
// Recover any interrupted turns once per app startup, not on the first generation.
_ = app.Services.GetRequiredService<ChatStore>();

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' blob: data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.Headers.CacheControl = "no-store";
        // Custom header + no CORS prevents cross-origin form submissions to a local billable API.
        if (context.Request.Method is not ("GET" or "HEAD") && context.Request.Headers["X-ImageChat-Request"] != "1")
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new { error = "This request must come from the ImageChat application." });
            return;
        }
        if (context.Request.Headers["Sec-Fetch-Site"] == "cross-site")
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new { error = "Cross-site API requests are not allowed." });
            return;
        }
    }
    try { await next(); }
    catch (Exception ex) when (!context.Response.HasStarted)
    {
        var (status, message) = ex switch
        {
            AppException e => (e.StatusCode, e.Message),
            BadHttpRequestException e => (e.StatusCode, "Invalid request or upload too large (20 MB maximum)."),
            InvalidDataException => (400, "Invalid multipart upload or upload too large."),
            OperationCanceledException => (504, "Generation timed out or was interrupted. Check the chat before retrying; the upstream request may still incur a charge."),
            HttpRequestException => (502, "Could not reach Foundry. Check network access and the configured endpoint."),
            _ => (500, "An unexpected server error occurred. Check local storage permissions and server logs.")
        };
        // Do not log exception messages/bodies: SDK exceptions can contain private prompts or endpoints.
        app.Logger.LogWarning("Request failed with {Status}; exception type {ExceptionType}", status, ex.GetType().Name);
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { error = message });
    }
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/status", () => new { configured = options.ConfigurationError() == null, deployment = options.Deployment,
    authMode = options.AuthMode, message = options.ConfigurationError() ?? "Configuration ready. The connection is verified when you generate an image." });
app.MapGet("/api/chats", (ChatStore store) => store.List());
app.MapPost("/api/chats", (ChatStore store) => Results.Json(store.Create(), statusCode: 201));
app.MapGet("/api/chats/{id}", (string id, ChatStore store) => store.Get(id));
app.MapDelete("/api/chats/{id}", async (string id, ChatStore store, SemaphoreSlim gate) =>
{
    if (!await gate.WaitAsync(0)) throw new AppException(409, "Wait for image generation to finish before deleting a chat.");
    try { store.Delete(id); return Results.NoContent(); }
    finally { gate.Release(); }
});
app.MapGet("/api/images/{id}", (string id, bool? download, ChatStore store) =>
{
    var image = store.GetImage(id) ?? throw new AppException(404, "Image not found.");
    var extension = image.ContentType == "image/png" ? "png" : "jpg";
    return Results.File(image.Bytes, image.ContentType, download == true ? $"image-{id}.{extension}" : null);
});
app.MapPost("/api/chats/{id}/messages", async (string id, HttpRequest request, ChatStore store,
    IImageGenerator generator, SemaphoreSlim gate, IHostApplicationLifetime lifetime) =>
{
    if (options.ConfigurationError() is { } configurationError) throw new AppException(503, configurationError);
    if (!await gate.WaitAsync(0)) throw new AppException(409, "An image is already being generated. Wait for it to finish.");
    string? assistantId = null;
    try
    {
        var chat = store.Get(id);
        if (!request.HasFormContentType) throw new AppException(400, "Send text and images as multipart form data.");
        var form = await request.ReadFormAsync(request.HttpContext.RequestAborted);
        var size = form["size"].ToString();
        var quality = form["quality"].ToString();
        InputRules.ValidateOptions(size, quality);
        if (form.Files.Count > 1 || (form.Files.Count == 1 && form.Files[0].Name != "image"))
            throw new AppException(400, "Attach one image per message.");
        ImageData? attachment = null;
        if (form.Files.GetFile("image") is { } file)
        {
            if (file.Length > InputRules.MaxImageBytes) throw new AppException(400, "Images must be 20 MB or smaller.");
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, request.HttpContext.RequestAborted);
            attachment = InputRules.ValidateImage(buffer.ToArray());
        }
        var prompt = InputRules.ValidatePrompt(form["prompt"].ToString(), attachment != null);
        var usePrevious = form["usePreviousImage"].ToString() != "false";
        // An explicit attachment supersedes the latest generated image. History text remains context.
        var reference = attachment ?? (usePrevious ? store.LatestImage(chat) : null);
        var input = new GenerationInput(InputRules.BuildPrompt(chat, prompt), size, quality, reference);
        assistantId = store.BeginTurn(chat, prompt, size, quality, attachment);
        // Persist the result even if the browser disconnects. Only timeout/app shutdown cancels generation.
        var image = await generator.GenerateAsync(input, lifetime.ApplicationStopping);
        store.FinishTurn(id, assistantId, image, null);
        return Results.Ok(store.Get(id));
    }
    catch (Exception ex)
    {
        if (assistantId != null)
            store.FinishTurn(id, assistantId, null, ex is AppException ae ? ae.Message :
                "Generation was interrupted or failed. Check your connection and configuration. Retrying may incur another charge.");
        throw;
    }
    finally { gate.Release(); }
}).RequireRateLimiting("generation");

app.Run();

public partial class Program { }