using System.Threading.RateLimiting;
using System.Security.Claims;
using Azure.Core;
using Azure.Identity;
using ImageChat;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;

var createAdmin = args.Contains("--create-admin", StringComparer.Ordinal);
var worker = args.Contains("--worker", StringComparer.Ordinal);
var dispatch = args.Contains("--dispatch", StringComparer.Ordinal);
var initialize = args.Contains("--initialize-storage", StringComparer.Ordinal);
var resetAdminPassword = args.Contains("--reset-admin-password", StringComparer.Ordinal);
if (createAdmin && resetAdminPassword)
{
    Console.Error.WriteLine("Choose either --create-admin or --reset-admin-password, not both.");
    Environment.ExitCode = 1;
    return;
}
var hostArgs = args.Where(arg => arg is not ("--create-admin" or "--reset-admin-password" or "--worker" or "--dispatch" or "--initialize-storage")).ToArray();
var builder = WebApplication.CreateBuilder(hostArgs);
// Repo-local secrets for development; environment variables always win in containers/cloud.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
    .AddJsonFile(Environment.GetEnvironmentVariable("IMAGECHAT_CONFIG_FILE") ?? "appsettings.Cloud.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables().AddCommandLine(hostArgs);
var cloudMode = builder.Configuration["Storage:Provider"] == "Azure";
AzureStorage? cloud = cloudMode ? new AzureStorage(builder.Configuration, new DefaultAzureCredential()) : null;
if (initialize)
{
    if (cloud == null) throw new InvalidOperationException("Azure storage is required for initialization.");
    cloud.Initialize();
    return;
}
if (worker || dispatch)
{
    if (cloud == null) throw new InvalidOperationException("Azure storage is required for jobs.");
    var cloudStore = new AzureChatStore(cloud);
    if (dispatch) cloudStore.DispatchPendingJobs();
    else
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        var foundry = builder.Configuration.GetSection("Foundry").Get<FoundryOptions>() ?? new();
        await cloudStore.ProcessOneAsync(new FoundryClient(http, foundry, new DefaultAzureCredential()), CancellationToken.None);
    }
    return;
}
builder.Services.AddSingleton(services => new AppPaths(Path.GetFullPath(
    services.GetRequiredService<IConfiguration>()["Storage:Directory"] ?? "data",
    services.GetRequiredService<IWebHostEnvironment>().ContentRootPath)));
if (createAdmin || resetAdminPassword)
{
    if (cloud != null) BootstrapAdmin.Run(() => new UserStore(new AzureAccountRepository(cloud)), resetAdminPassword);
    else BootstrapAdmin.Run(Path.GetFullPath(builder.Configuration["Storage:Directory"] ?? "data", builder.Environment.ContentRootPath), resetAdminPassword);
    return;
}
// Do not allow the platform convenience switch to enable trust-all forwarded headers.
// Client identity is resolved only by the explicit allowlist configured below.
builder.WebHost.UseSetting("FORWARDEDHEADERS_ENABLED", "false");
builder.Services.AddAccountSecurity(builder.Configuration, cloud);
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    if (builder.Configuration.GetValue<bool>("ReverseProxy:Enabled")) ProxySecurity.Configure(options, builder.Configuration);
});
builder.Services.AddSingleton(services => services.GetRequiredService<IConfiguration>()
    .GetSection("Foundry").Get<FoundryOptions>() ?? new());
builder.Services.AddSingleton<ModelCatalog>();
builder.Services.AddSingleton(services => services.GetRequiredService<IConfiguration>()
    .GetSection("Foundry:Chat").Get<ChatModelOptions>() ?? new());
builder.Services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential());
builder.Services.AddHttpClient<ITextChatGenerator, ResponsesChatClient>(http => http.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient<IImageGenerator, FoundryClient>(http => http.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton(services => new ChatStore(services.GetRequiredService<AppPaths>().DataDirectory));
builder.Services.AddSingleton<IChatStore>(services => cloud == null ? services.GetRequiredService<ChatStore>() : new AzureChatStore(cloud));
builder.Services.AddSingleton<ChatOperations>();
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = InputRules.MaxImageBytes + 1024 * 1024);
builder.Services.Configure<FormOptions>(f =>
{
    f.MultipartBodyLengthLimit = InputRules.MaxImageBytes + 1024 * 1024;
    f.ValueLengthLimit = 256000;
    // Avoid ASP.NET spooling uploaded private images to a machine-global temp directory.
    f.MemoryBufferThreshold = InputRules.MaxImageBytes + 1024 * 1024;
});
builder.Services.AddRateLimiter(rate =>
{
    rate.RejectionStatusCode = 429;
    rate.AddPolicy("chat", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
        }));
    rate.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        TelemetryFilter.NormalizeIp(context.Connection.RemoteIpAddress) ?? "unknown", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
        }));
    rate.AddFixedWindowLimiter("generation", limiter =>
    {
        limiter.PermitLimit = 5;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
    rate.OnRejected = async (context, token) =>
        await context.HttpContext.Response.WriteAsJsonAsync(new { error = "Too many requests. Wait a minute before trying again." }, token);
});
var app = builder.Build();
var options = app.Services.GetRequiredService<FoundryOptions>();
// Recover any interrupted turns once per app startup, not on the first generation.
var store = app.Services.GetRequiredService<IChatStore>();
var users = app.Services.GetRequiredService<UserStore>();
// Pre-login single-user data belongs only to the original bootstrap administrator.
var firstAdmin = users.List().Where(user => user.Role == "admin").OrderBy(user => user.CreatedAt).FirstOrDefault();
if (firstAdmin != null) store.ClaimLegacyChats(firstAdmin.Id);

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' blob: data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    context.Response.Headers.CacheControl = "no-store";
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
if (builder.Configuration.GetValue<bool>("ReverseProxy:Enabled")) app.UseForwardedHeaders();
if (builder.Configuration["ReverseProxy:Provider"] == "ContainerApps")
{
    if (!cloudMode || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CONTAINER_APP_NAME")))
        throw new InvalidOperationException("ContainerApps forwarding requires Azure storage and the Container Apps host.");
    app.Use(async (context, next) => { ContainerAppsIngress.Apply(context); await next(); });
}
app.Use(async (context, next) =>
{
    if (context.Request.Path != "/healthz" && builder.Configuration.GetValue<bool>("Security:RequireHttps") && !context.Request.IsHttps)
        throw new AppException(400, "HTTPS is required. Configure TLS and trusted proxy forwarding before signing in.");
    if (context.Request.Path.StartsWithSegments("/api") && context.Request.Method is not ("GET" or "HEAD"))
    {
        if (context.Request.Headers.TryGetValue("Origin", out var origin) &&
            !string.Equals(origin.ToString(), $"{context.Request.Scheme}://{context.Request.Host}", StringComparison.OrdinalIgnoreCase))
            throw new AppException(403, "Cross-origin requests are not allowed.");
        if (context.Request.Path.StartsWithSegments("/api/auth") || context.Request.Path.StartsWithSegments("/api/admin/users"))
        {
            var limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = 16 * 1024;
            if (context.Request.ContentLength > 16 * 1024) throw new AppException(413, "Account request is too large.");
        }
    }
    await next();
});
app.UseAuthentication();
app.UseAuthorization();
// Gate static HTML as well as APIs: no alternate /index.html or /admin.html bypass.
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value?.ToLowerInvariant() ?? "/";
    var publicPath = path is "/login" or "/login.html" or "/login.js" or "/portal.css" or "/app.css" or "/app.js" or "/admin.js" or "/healthz" or "/api/auth/session" or "/api/auth/login";
    if (!publicPath && context.User.Identity?.IsAuthenticated != true)
    {
        await context.ChallengeAsync();
        return;
    }
    if ((path == "/admin.html" || path == "/admin" || path.StartsWith("/admin/") || path.StartsWith("/api/admin/")) && !context.User.IsInRole("admin"))
    {
        await context.ForbidAsync();
        return;
    }
    await next();
});
app.Use(async (context, next) =>
{
    string? telemetryId = null;
    if (context.Request.Method == "POST" && context.Request.Path.StartsWithSegments("/api/chats") &&
        (context.Request.Path.Value!.EndsWith("/messages", StringComparison.OrdinalIgnoreCase) ||
         context.Request.Path.Value!.EndsWith("/text-messages", StringComparison.OrdinalIgnoreCase)))
    {
        telemetryId = store.StartRequest(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!,
            context.User.Identity!.Name!, TelemetryFilter.NormalizeIp(context.Connection.RemoteIpAddress));
        context.Items["telemetryId"] = telemetryId;
    }
    var failureStatus = 0;
    try { await next(); }
    catch (Exception exception)
    {
        failureStatus = exception switch
        {
            AppException e => e.StatusCode,
            BadHttpRequestException e => e.StatusCode,
            InvalidDataException => 400,
            OperationCanceledException => 504,
            HttpRequestException => 502,
            _ => 500
        };
        throw;
    }
    finally
    {
        if (telemetryId != null) store.EndRequest(telemetryId, failureStatus != 0 ? failureStatus : context.Response.StatusCode);
    }
});
app.Use(async (context, next) =>
{
    if (store is AzureChatStore shared && context.Request.Method == "POST")
    {
        if (context.Request.Path == "/api/auth/login" || context.Request.Path == "/api/auth/password")
            shared.CheckRateLimit("login:" + TelemetryFilter.NormalizeIp(context.Connection.RemoteIpAddress), 10);
        else if (context.Request.Path.Value!.EndsWith("/text-messages", StringComparison.OrdinalIgnoreCase))
            shared.CheckRateLimit("chat:" + context.User.FindFirstValue(ClaimTypes.NameIdentifier), 20);
        else if (context.Items.ContainsKey("telemetryId")) shared.CheckRateLimit("generation", 5);
    }
    await next();
});
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapAccountEndpoints();
app.MapTextChat();
app.MapGet("/login", (IWebHostEnvironment env) => Results.File(Path.Combine(env.WebRootPath, "login.html"), "text/html; charset=utf-8")).AllowAnonymous();
app.MapGet("/admin", (IWebHostEnvironment env) => Results.File(Path.Combine(env.WebRootPath, "admin.html"), "text/html; charset=utf-8"))
    .RequireAuthorization(policy => policy.RequireRole("admin"));
app.MapGet("/api/admin/telemetry", (string? from, string? to, string? ip, int? page, int? pageSize, IChatStore chats) =>
    chats.Report(TelemetryFilter.Parse(from, to, ip, page, pageSize))).RequireAuthorization(policy => policy.RequireRole("admin"));
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/status", (ModelCatalog models, ChatModelOptions chatOptions) => new { configured = options.ConfigurationError() == null, deployment = options.Deployment,
    authMode = options.AuthMode, message = options.ConfigurationError() ?? "Configuration ready. The connection is verified when you generate an image.",
    defaultModel = ModelCatalog.Gpt, models = models.Statuses(), chatModel = TextChatEndpoints.Status(chatOptions) });
app.MapGet("/api/chats", (IChatStore store, ClaimsPrincipal user) => store.List(user.FindFirstValue(ClaimTypes.NameIdentifier)!));
app.MapPost("/api/chats", (IChatStore store, ClaimsPrincipal user) => Results.Json(store.Create(user.FindFirstValue(ClaimTypes.NameIdentifier)!), statusCode: 201));
app.MapGet("/api/chats/{id}", (string id, IChatStore store, ClaimsPrincipal user) => store.Get(id, user.FindFirstValue(ClaimTypes.NameIdentifier)!));
app.MapDelete("/api/chats/{id}", (string id, IChatStore store, ChatOperations operations, ClaimsPrincipal user) =>
{
    store.Get(id, user.FindFirstValue(ClaimTypes.NameIdentifier)!);
    using var operation = operations.Acquire(id);
    store.Delete(id, user.FindFirstValue(ClaimTypes.NameIdentifier)!);
    return Results.NoContent();
});
app.MapGet("/api/images/{id}", (string id, bool? download, IChatStore store, ClaimsPrincipal user) =>
{
    var image = store.GetImage(id, user.FindFirstValue(ClaimTypes.NameIdentifier)!) ?? throw new AppException(404, "Image not found.");
    var extension = image.ContentType == "image/png" ? "png" : "jpg";
    return Results.File(image.Bytes, image.ContentType, download == true ? $"image-{id}.{extension}" : null);
});
app.MapPost("/api/chats/{id}/messages", async (string id, HttpRequest request, IChatStore store,
    IImageGenerator generator, ChatOperations operations, IHostApplicationLifetime lifetime, ModelCatalog models) =>
{
    var ownerId = request.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    store.Get(id, ownerId);
    using var operation = operations.Acquire(id);
    string? assistantId = null;
    try
    {
        var chat = store.Get(id, ownerId);
        if (!request.HasFormContentType) throw new AppException(400, "Send text and images as multipart form data.");
        var form = await request.ReadFormAsync(request.HttpContext.RequestAborted);
        var selected = models.Resolve(form.ContainsKey("model") ? form["model"].ToString() : null);
        if (request.HttpContext.Items["telemetryId"] is string telemetryId)
            store.SetRequestModel(telemetryId, selected.Id, selected.Connection.Deployment);
        if (selected.ConfigurationError is { } configurationError) throw new AppException(503, configurationError);
        var size = form["size"].ToString();
        var quality = selected.Qualities.Contains("default") && !form.ContainsKey("quality") ? "default" : form["quality"].ToString();
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
    selected.Validate(size, quality, reference != null);
    var input = new GenerationInput(InputRules.BuildPrompt(chat, prompt), size, quality, reference, selected.Id);
        if (store is AzureChatStore queuedStore)
            return Results.Json(queuedStore.QueueTurn(chat, prompt, size, quality, attachment,
                request.HttpContext.Items["telemetryId"] as string, input, selected.Connection.Deployment), statusCode: 202);
        assistantId = store.BeginTurn(chat, prompt, size, quality, attachment,
            request.HttpContext.Items["telemetryId"] as string, reference == null ? "generation" : "edit", selected.Id, selected.Connection.Deployment);
        // Persist the result even if the browser disconnects. Only timeout/app shutdown cancels generation.
        var image = await generator.GenerateAsync(input, lifetime.ApplicationStopping);
        store.FinishTurn(id, assistantId, image, null);
        return Results.Ok(store.Get(id, ownerId));
    }
    catch (Exception ex)
    {
        if (assistantId != null)
            store.FinishTurn(id, assistantId, null, ex is AppException ae ? ae.Message :
                "Generation was interrupted or failed. Check your connection and configuration. Retrying may incur another charge.");
        throw;
    }
}).RequireRateLimiting("generation");

app.Run();

public partial class Program { }