using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImageChat;

public record AppPaths(string DataDirectory);
public sealed record LoginInput([property: JsonRequired] string Username, [property: JsonRequired] string Password)
{
    public override string ToString() => "LoginInput { [redacted] }";
}
public sealed record PasswordInput([property: JsonRequired] string CurrentPassword, [property: JsonRequired] string NewPassword)
{
    public override string ToString() => "PasswordInput { [redacted] }";
}
public sealed record CreateUserInput([property: JsonRequired] string Username, [property: JsonRequired] string Password,
    [property: JsonRequired] string Role)
{
    public override string ToString() => "CreateUserInput { [redacted] }";
}
public sealed record UpdateUserInput([property: JsonRequired] string Role, [property: JsonRequired] bool Enabled, string? Password = null)
{
    public override string ToString() => "UpdateUserInput { [redacted] }";
}

public static class AccountSecurity
{
    internal const string StampClaim = "security_stamp";
    private const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;

    public static void AddAccountSecurity(this IServiceCollection services, IConfiguration config, AzureStorage? cloud = null)
    {
        services.AddSingleton(provider => cloud == null
            ? new UserStore(DataDirectory(provider.GetRequiredService<AppPaths>(), provider))
            : new UserStore(new AzureAccountRepository(cloud)));
        var protection = services.AddDataProtection().SetApplicationName("ImageChat");
        if (cloud != null)
        {
            protection.PersistKeysToAzureBlobStorage(cloud.Keys.GetBlobClient("keyring.xml"));
            if (config["Storage:KeyEncryptionKeyUri"] is { Length: > 0 } keyUri)
                protection.ProtectKeysWithAzureKeyVault(new Uri(keyUri), new Azure.Identity.DefaultAzureCredential());
            else if (string.IsNullOrEmpty(config["Storage:ConnectionString"]))
                throw new InvalidOperationException("Configure Storage:KeyEncryptionKeyUri to protect cloud session keys.");
        }
        // Resolve paths only when options are built, after test/host DI overrides have been applied.
        if (cloud == null) services.AddOptions<KeyManagementOptions>().PostConfigure<AppPaths, IServiceProvider>((options, paths, provider) =>
        {
            var keys = new DirectoryInfo(Path.Combine(DataDirectory(paths, provider), "keys"));
            options.XmlRepository = new FileSystemXmlRepository(keys, NullLoggerFactory.Instance);
        });
        services.AddAuthentication(Scheme).AddCookie(Scheme, options =>
        {
            options.Cookie.Name = "ImageChat.Session";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = config.GetValue<bool>("Security:RequireHttps")
                ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
            options.Cookie.Path = "/";
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = false;
            options.LoginPath = "/login";
            options.AccessDeniedPath = "/";
            options.Events.OnValidatePrincipal = async context =>
            {
                Account? account = null;
                try { account = CurrentAccount(context.Principal, context.HttpContext.RequestServices.GetRequiredService<UserStore>()); }
                catch (AppException) { /* Missing or corrupt storage invalidates cookies, never trusts cached claims. */ }
                if (account != null) return;
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(Scheme);
            };
            options.Events.OnRedirectToLogin = context =>
            {
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return context.Response.WriteAsJsonAsync(new { error = "Authentication required." });
                }
                context.Response.Redirect("/login");
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return context.Request.Path.StartsWithSegments("/api")
                    ? context.Response.WriteAsJsonAsync(new { error = "Access denied." })
                    : Task.CompletedTask;
            };
        });
        services.AddAuthorization();
    }

    public static void MapAccountEndpoints(this WebApplication app)
    {
        app.MapGet("/api/auth/session", (HttpContext context, UserStore store) =>
        {
            var setupRequired = !store.HasUsers;
            var account = CurrentAccount(context.User, store);
            return Results.Ok(new { authenticated = account != null, setupRequired, user = account?.ToView() });
        }).AllowAnonymous();

        app.MapPost("/api/auth/login", async Task<IResult> (LoginInput input, HttpContext context, UserStore store) =>
        {
            if (!store.HasUsers) return Results.Json(new { error = UserStore.SetupMessage }, statusCode: 503);
            var account = store.Authenticate(input.Username, input.Password);
            if (account == null) return Results.Json(new { error = UserStore.InvalidLoginMessage }, statusCode: 401);
            await context.SignInAsync(Scheme, Principal(account), new AuthenticationProperties
            {
                IsPersistent = false,
                AllowRefresh = false
            });
            return Results.Ok(new { user = account.ToView() });
        }).AllowAnonymous().RequireRateLimiting("login");

        app.MapPost("/api/auth/logout", async Task<IResult> (HttpContext context, UserStore store) =>
        {
            var account = CurrentAccount(context.User, store);
            if (account != null) store.RevokeSessions(account.Id, account.SecurityStamp);
            await context.SignOutAsync(Scheme);
            return Results.NoContent();
        }).RequireAuthorization();

        app.MapPost("/api/auth/password", async Task<IResult> (PasswordInput input, HttpContext context, UserStore store) =>
        {
            var account = CurrentAccount(context.User, store);
            if (account == null) return Results.Json(new { error = UserStore.InvalidLoginMessage }, statusCode: 401);
            store.ChangePassword(account.Id, input.CurrentPassword, input.NewPassword, account.SecurityStamp);
            await context.SignOutAsync(Scheme);
            return Results.NoContent();
        }).RequireAuthorization().RequireRateLimiting("login");

        var admin = app.MapGroup("/api/admin/users").RequireAuthorization(policy => policy.RequireRole("admin"));
        admin.MapGet("", (UserStore store) => Results.Ok(store.List()));
        admin.MapPost("", (CreateUserInput input, UserStore store) =>
            Results.Json(store.Create(input.Username, input.Password, input.Role), statusCode: 201));
        admin.MapPut("/{id}", (string id, UpdateUserInput input, UserStore store) =>
            Results.Ok(store.Update(id, input.Role, input.Enabled, input.Password)));
        admin.MapDelete("/{id}", (string id, UserStore store) =>
        {
            store.Delete(id);
            return Results.NoContent();
        });
    }

    private static string DataDirectory(AppPaths paths, IServiceProvider provider)
    {
        var directory = UserStore.ResolveDataDirectory(paths.DataDirectory);
        var webRoot = provider.GetService<IWebHostEnvironment>()?.WebRootPath;
        if (!string.IsNullOrWhiteSpace(webRoot))
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(webRoot), directory);
            if (!Path.IsPathRooted(relative) && relative != ".." &&
                !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                throw new AppException(503, "Account storage must be outside the web root.");
        }
        return directory;
    }

    private static Account? CurrentAccount(ClaimsPrincipal? principal, UserStore store)
    {
        if (principal?.Identity?.IsAuthenticated != true) return null;
        var id = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (id == null) return null;
        var account = store.Find(id);
        return account is { Enabled: true } && account.SecurityStamp == principal.FindFirstValue(StampClaim) &&
            account.Role == principal.FindFirstValue(ClaimTypes.Role) && account.Username == principal.FindFirstValue(ClaimTypes.Name)
            ? account : null;
    }

    private static ClaimsPrincipal Principal(Account account) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, account.Id), new Claim(ClaimTypes.Name, account.Username),
            new Claim(ClaimTypes.Role, account.Role), new Claim(StampClaim, account.SecurityStamp)], Scheme));
}