using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, ValidateIssuerSigningKey = true,
        ValidIssuer = cfg["Jwt:Issuer"], ValidAudience = cfg["Jwt:Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(cfg["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key is required.")))
    };
});
builder.Services.AddAuthorization();

builder.Services.AddReverseProxy().LoadFromConfig(cfg.GetSection("ReverseProxy"));

builder.Services.AddRateLimiter(o =>
{
    o.OnRejected = (ctx, ct) =>
    {
        ctx.HttpContext.Response.StatusCode = 429;
        ctx.HttpContext.Response.Headers["Retry-After"] = "60";
        return ValueTask.CompletedTask;
    };

    // MapReverseProxy() is a single catch-all route, so named policies attached via
    // .RequireRateLimiting("x") never apply to it. Use a global partitioned limiter instead,
    // keyed on client IP + a bucket derived from the request path, so /transfers, /admin and
    // everything else each get their own window without needing per-endpoint routing.
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
    {
        var path = httpContext.Request.Path.Value ?? string.Empty;
        var clientKey = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        var (bucket, permitLimit) = path switch
        {
            var p when p.Contains("/transfers", StringComparison.OrdinalIgnoreCase) => ("transfer", 20),
            var p when p.Contains("/admin", StringComparison.OrdinalIgnoreCase) => ("admin", 10),
            _ => ("api", 100)
        };

        return RateLimitPartition.GetFixedWindowLimiter($"{bucket}:{clientKey}", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = TimeSpan.FromMinutes(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0
        });
    });
});

builder.Services.AddCors(o =>
{
    o.AddDefaultPolicy(p => p
        .SetIsOriginAllowed(origin =>
        {
            if (Uri.TryCreate(origin, UriKind.Absolute, out var uri))
                return uri.Host is "localhost" or "127.0.0.1"
                    || uri.Host.EndsWith(".novawallet.ng")
                    || uri.Host.EndsWith(".novawallet.com");
            return false;
        })
        .WithMethods("GET", "POST")
        .WithHeaders("Authorization", "Content-Type", "Idempotency-Key", "X-Correlation-Id", "X-Request-Id")
        .AllowCredentials()
        .SetPreflightMaxAge(TimeSpan.FromMinutes(10)));
});

builder.WebHost.UseKestrel(o =>
{
    o.Limits.MaxRequestBodySize = 64 * 1024;
    o.Limits.MaxConcurrentConnections = 200;
    o.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
    o.AddServerHeader = false;
});

var swaggerOpts = cfg.GetSection("Swagger").Get<SwaggerOptions>() ?? new SwaggerOptions();
if (swaggerOpts.Enabled)
{
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();
}

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
    app.UseHsts();
}

app.Use(async (ctx, next) =>
{
    var headers = ctx.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["X-XSS-Protection"] = "1; mode=block";
    headers["Referrer-Policy"] = "no-referrer";
    headers["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";
    headers["X-Powered-By"] = "NovaWallet";
    headers["Cache-Control"] = "no-store";

    if (ctx.Request.Headers.TryGetValue("X-Correlation-Id", out var corr) && !string.IsNullOrWhiteSpace(corr))
        headers["X-Correlation-Id"] = corr.ToString();
    else
        headers["X-Correlation-Id"] = Guid.NewGuid().ToString("N");

    await next();
});

app.Use(async (ctx, next) =>
{
    if (ctx.Request.ContentLength is > 64 * 1024)
    {
        ctx.Response.StatusCode = 413;
        await ctx.Response.WriteAsync("Payload too large.");
        return;
    }
    await next();
});

if (swaggerOpts.Enabled)
{
    app.UseSwagger(o => o.RouteTemplate = swaggerOpts.RoutePrefix + "/{documentName}/swagger.json");
    app.UseSwaggerUI(o =>
    {
        o.SwaggerEndpoint($"/{swaggerOpts.RoutePrefix}/{swaggerOpts.Version}/swagger.json", swaggerOpts.Title);
        o.RoutePrefix = swaggerOpts.RoutePrefix;
    });
}

app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    if (path.Contains("/admin") && !ctx.User.IsInRole("admin") && !ctx.User.IsInRole("product-owner"))
    {
        ctx.Response.StatusCode = 403;
        await ctx.Response.WriteAsync("Admin access required.");
        return;
    }
    await next();
});

app.MapReverseProxy().RequireAuthorization();

app.Run();

public sealed class SwaggerOptions
{
    public bool Enabled { get; set; } = true;
    public string RoutePrefix { get; set; } = "swagger";
    public string Title { get; set; } = "NovaWallet Gateway API";
    public string Description { get; set; } = "ProxyApi gateway - forwards to WalletApi. All endpoints require JWT.";
    public string Version { get; set; } = "v1";
}
