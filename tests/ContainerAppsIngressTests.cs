using ImageChat;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace ImageChat.Tests;

public sealed class ContainerAppsIngressTests
{
    [Fact]
    public void UsesOnlyPlatformAppendedClientAddress()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        context.Request.Headers["X-Forwarded-For"] = "1.2.3.4, 203.0.113.42";
        ContainerAppsIngress.Apply(context);
        Assert.Equal("203.0.113.42", context.Connection.RemoteIpAddress!.ToString());
        Assert.True(context.Request.IsHttps);
    }

    [Theory]
    [InlineData("http", "203.0.113.42")]
    [InlineData("https,http", "203.0.113.42")]
    [InlineData("https", "invalid")]
    [InlineData("https", "")]
    public void RejectsMissingOrInvalidPlatformHeaders(string protocol, string address)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Forwarded-Proto"] = protocol;
        context.Request.Headers["X-Forwarded-For"] = address;
        Assert.Equal(400, Assert.Throws<AppException>(() => ContainerAppsIngress.Apply(context)).StatusCode);
    }
}