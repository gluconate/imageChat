using System.Net;

namespace ImageChat;

public static class ContainerAppsIngress
{
    public static void Apply(HttpContext context)
    {
        if (context.Request.Path == "/healthz") return;
        if (context.Request.Headers["X-Forwarded-Proto"] != "https")
            throw new AppException(400, "HTTPS through the configured Container Apps ingress is required.");
        var forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
        var rightmost = forwarded.Split(',').LastOrDefault()?.Trim();
        if (!IPAddress.TryParse(rightmost, out var address))
            throw new AppException(400, "The platform ingress did not provide a valid client address.");
        context.Request.Scheme = "https";
        context.Connection.RemoteIpAddress = address;
    }
}