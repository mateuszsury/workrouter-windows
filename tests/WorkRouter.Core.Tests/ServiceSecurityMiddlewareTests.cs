using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using WorkRouter.Service;

namespace WorkRouter.Tests;

public sealed class ServiceSecurityMiddlewareTests
{
    [Fact]
    public async Task HttpLoopbackSessionCookieRemainsUsableWithoutSecureFlag()
    {
        var context = CreateSessionContext("http");
        var tokens = new ServiceTokenManager(new ConfigurationBuilder().Build());
        context.Request.Headers["X-WorkRouter-Token"] = tokens.Token;
        var middleware = new ServiceSecurityMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context, tokens);

        var cookie = context.Response.Headers.SetCookie.ToString();
        Assert.Contains("workrouter-session=", cookie, StringComparison.Ordinal);
        Assert.DoesNotContain("Secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HttpOnly", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HttpsSessionCookieSetsSecureFlag()
    {
        var context = CreateSessionContext("https");
        var tokens = new ServiceTokenManager(new ConfigurationBuilder().Build());
        context.Request.Headers["X-WorkRouter-Token"] = tokens.Token;
        var middleware = new ServiceSecurityMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context, tokens);

        var cookie = context.Response.Headers.SetCookie.ToString();
        Assert.Contains("workrouter-session=", cookie, StringComparison.Ordinal);
        Assert.Contains("Secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HttpOnly", cookie, StringComparison.OrdinalIgnoreCase);
    }

    private static DefaultHttpContext CreateSessionContext(string scheme)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = scheme;
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/session";
        return context;
    }
}
