namespace InjuryReporting.Web.Security;

/// <summary>Adds browser hardening headers and prevents caching of dynamic (PHI-bearing) pages.</summary>
public class SecurityHeadersMiddleware
{
    private const string Csp =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; " +
        "connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    private readonly RequestDelegate _next;
    public SecurityHeadersMiddleware(RequestDelegate next) => _next = next;

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var h = context.Response.Headers;
            h["Content-Security-Policy"] = Csp;
            h["X-Content-Type-Options"] = "nosniff";
            h["X-Frame-Options"] = "DENY";
            h["Referrer-Policy"] = "no-referrer";
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
            h["Cross-Origin-Opener-Policy"] = "same-origin";
            if (!h.ContainsKey("Cache-Control"))
            {
                h["Cache-Control"] = "no-store";
                h["Pragma"] = "no-cache";
            }
            return Task.CompletedTask;
        });
        return _next(context);
    }
}
