namespace WorkRouter.Service;

internal sealed class ServiceSecurityMiddleware
{
    private readonly RequestDelegate _next;

    public ServiceSecurityMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, ServiceTokenManager tokens)
    {
        AddSecurityHeaders(context.Response.Headers);

        if (context.Request.Path.Equals("/bootstrap-ticket", StringComparison.OrdinalIgnoreCase) &&
            HttpMethods.IsGet(context.Request.Method))
        {
            if (!tokens.TryConsumeBootstrapTicket(context.Request.Query["ticket"].ToString()))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("Bilet uruchomieniowy wygasł. Otwórz panel ponownie ze skrótu.")
                    .ConfigureAwait(false);
                return;
            }

            AppendSessionCookie(context.Response, tokens.Token);
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Redirect("/");
            return;
        }

        if (context.Request.Path.Equals("/api/session", StringComparison.OrdinalIgnoreCase) &&
            HttpMethods.IsPost(context.Request.Method))
        {
            var candidate = context.Request.Headers["X-WorkRouter-Token"].ToString();
            if (!tokens.IsValid(candidate))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            AppendSessionCookie(context.Response, candidate);
            context.Response.Headers.CacheControl = "no-store";
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        if (context.Request.Path.StartsWithSegments("/api"))
        {
            var header = context.Request.Headers["X-WorkRouter-Token"].ToString();
            var cookie = context.Request.Cookies[ServiceTokenManager.CookieName];
            if (!tokens.IsValid(header) && !tokens.IsValid(cookie))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new
                {
                    success = false,
                    code = "unauthorized",
                    message = "Otwórz panel przez aplikację WorkRouter."
                }).ConfigureAwait(false);
                return;
            }

            context.Response.Headers.CacheControl = "no-store";
        }

        await _next(context).ConfigureAwait(false);
    }

    private static void AppendSessionCookie(HttpResponse response, string token) =>
        response.Cookies.Append(ServiceTokenManager.CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Strict,
            Secure = false,
            MaxAge = TimeSpan.FromHours(12),
            Path = "/"
        });

    private static void AddSecurityHeaders(IHeaderDictionary headers)
    {
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
        headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
    }
}
