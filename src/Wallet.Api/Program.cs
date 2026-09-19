using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using NovaWallet.Application;
using NovaWallet.Application.Repositories;
using NovaWallet.Domain;
using NovaWallet.Infrastructure;
using NovaWallet.Infrastructure.Repositories;
using NovaWallet.Infrastructure.Services;
using NovaWallet.WalletApi;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlServer(config.GetConnectionString("LedgerDb")));

builder.Services.Configure<FeePolicyOptions>(config.GetSection("Fees"));
builder.Services.AddSingleton<FeePolicy>(sp =>
{
    var opt = sp.GetRequiredService<IConfiguration>().GetSection("Fees").Get<FeePolicyOptions>() ?? new FeePolicyOptions();
    return new FeePolicy(opt.OutboundFeeKobo, opt.VatRate);
});

builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped<IWalletRepository, WalletRepository>();
builder.Services.AddScoped<ITransferRepository, TransferRepository>();
builder.Services.AddScoped<ILedgerEntryRepository, LedgerEntryRepository>();
builder.Services.AddScoped<IAuditLogRepository, AuditLogRepository>();
builder.Services.AddScoped<IOutboxRepository, OutboxRepository>();
builder.Services.AddScoped<ISettlementJobRepository, SettlementJobRepository>();
builder.Services.AddScoped<IExternalAccountRepository, ExternalAccountRepository>();
builder.Services.AddScoped<IReconciliationRepository, ReconciliationRepository>();
builder.Services.AddScoped<IDailyOutboundCounterRepository, DailyOutboundCounterRepository>();

builder.Services.AddScoped<IWalletService, WalletService>();
builder.Services.AddScoped<ITransferService, TransferService>();
builder.Services.AddScoped<IReconciliationService, ReconciliationService>();
builder.Services.AddScoped<IExternalAccountService, ExternalAccountService>();
builder.Services.AddScoped<IKycService, KycService>();
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IPaymentRail, MockNipPaymentRail>();
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, ValidateIssuerSigningKey = true,
        ValidIssuer = config["Jwt:Issuer"], ValidAudience = config["Jwt:Audience"],
        NameClaimType = "sub", RoleClaimType = "role",
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key is required.")))
    };
});
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("AdminOrProductOwner", p => p.RequireRole(Roles.Admin, Roles.ProductOwner));

var app = builder.Build();

app.UseExceptionHandler();
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }

using (var scope = app.Services.CreateScope())
{
    await DbInitializer.InitializeAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), CancellationToken.None);
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();
app.MapGet("/ready", async (AppDbContext db, CancellationToken ct) => { 
    var canConnect = await db.Database.CanConnectAsync(ct); 
    return canConnect ? Results.Ok(new { status = "ready" }) : Results.Json(new { status = "unavailable" }, statusCode: 503); 
}).AllowAnonymous();

if (app.Environment.IsDevelopment())
{
    app.MapPost("/dev/token", (string sub, string? role, IConfiguration cfg) =>
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(cfg["Jwt:Key"]!));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim> { new("sub", string.IsNullOrWhiteSpace(sub) ? "customer-1" : sub) };
        if (!string.IsNullOrWhiteSpace(role))
            claims.Add(new Claim("role", role));
        var token = new JwtSecurityToken(
            issuer: cfg["Jwt:Issuer"], audience: cfg["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1), signingCredentials: creds);
        return Results.Ok(new { token = new JwtSecurityTokenHandler().WriteToken(token) });
    }).AllowAnonymous();
}

var api = app.MapGroup("/api/v1").RequireAuthorization();

api.MapPost("/wallets", async (CreateWalletRequest req, ClaimsPrincipal user, IWalletService service, CancellationToken ct) =>
{
    var customerId = user.FindFirst("sub")?.Value ?? throw new DomainException("auth.subject_missing", "Authenticated subject is missing.");
    var currency = string.IsNullOrWhiteSpace(req?.Currency) ? "NGN" : req.Currency.ToUpperInvariant();
    var accountName = req?.AccountName;
    var wallet = await service.CreateAsync(customerId, currency, accountName, ct);
    return Results.Created($"/api/v1/wallets/{wallet.Id}", wallet);
});

api.MapGet("/wallets", async (ClaimsPrincipal user, IWalletService service, CancellationToken ct) =>
{
    var customerId = user.FindFirst("sub")?.Value ?? throw new DomainException("auth.subject_missing", "Authenticated subject is missing.");
    return Results.Ok(await service.ListByCustomerAsync(customerId, ct));
});

api.MapGet("/wallets/{walletId:guid}/balance", async (Guid walletId, ClaimsPrincipal user, IWalletService service, CancellationToken ct) =>
{
    var customerId = user.FindFirst("sub")?.Value ?? throw new DomainException("auth.subject_missing", "Authenticated subject is missing.");
    var wallet = await service.GetAsync(walletId, customerId, ct) ?? throw new DomainException("wallet.not_found", "Wallet not found.");
    return Results.Ok(wallet);
});

api.MapPost("/wallets/{walletId:guid}/credits", async (Guid walletId, CreditRequest req, ClaimsPrincipal user, IWalletService service, HttpContext http, CancellationToken ct) =>
{
    var customerId = user.FindFirst("sub")?.Value ?? throw new DomainException("auth.subject_missing", "Authenticated subject is missing.");
    var wallet = await service.GetAsync(walletId, customerId, ct) ?? throw new DomainException("wallet.not_found", "Wallet not found.");
    var updated = await service.CreditAsync(walletId, req.AmountKobo, customerId, Correlation(http), ct);
    return Results.Ok(new { walletId, req.AmountKobo, updated.BalanceKobo });
});

api.MapGet("/wallets/{walletId:guid}/transactions", async (Guid walletId, int? page, int? pageSize, ClaimsPrincipal user, IWalletService service, CancellationToken ct) =>
{
    var customerId = user.FindFirst("sub")?.Value ?? throw new DomainException("auth.subject_missing", "Authenticated subject is missing.");
    var wallet = await service.GetAsync(walletId, customerId, ct) ?? throw new DomainException("wallet.not_found", "Wallet not found.");
    return Results.Ok(await service.StatementAsync(walletId, page ?? 1, pageSize ?? 25, ct));
});

api.MapGet("/name-enquiry/{accountNumber}", async (string accountNumber, IWalletService service, CancellationToken ct) =>
{
    var result = await service.NameEnquiryAsync(accountNumber, ct);
    return result is null ? Results.NotFound(new { code = "name_enquiry.not_found", message = "Account number not found." }) : Results.Ok(result);
});

api.MapGet("/me/kyc", async (ClaimsPrincipal user, IKycService service, CancellationToken ct) =>
{
    var customerId = user.FindFirst("sub")?.Value ?? throw new DomainException("auth.subject_missing", "Authenticated subject is missing.");
    var result = await service.GetByCustomerAsync(customerId, ct);
    return result is null ? Results.NotFound(new { code = "user.not_found", message = "User not found." }) : Results.Ok(result);
});

api.MapPost("/me/kyc", async (SubmitKycRequest req, ClaimsPrincipal user, IKycService service, CancellationToken ct) =>
{
    var customerId = user.FindFirst("sub")?.Value ?? throw new DomainException("auth.subject_missing", "Authenticated subject is missing.");
    var kycStatus = await service.GetByCustomerAsync(customerId, ct);
    if (kycStatus is null) return Results.NotFound(new { code = "user.not_found", message = "User not found." });
    var result = await service.SubmitAsync(kycStatus.UserId, req.DocumentType, req.DocumentNumber, ct);
    return result is null ? Results.NotFound(new { code = "user.not_found", message = "User not found." }) : Results.Created($"/api/v1/me/kyc/{result.Id}", result);
});

api.MapGet("/me/kyc/documents", async (ClaimsPrincipal user, IKycService service, CancellationToken ct) =>
{
    var customerId = user.FindFirst("sub")?.Value ?? throw new DomainException("auth.subject_missing", "Authenticated subject is missing.");
    var kycStatus = await service.GetByCustomerAsync(customerId, ct);
    if (kycStatus is null) return Results.NotFound(new { code = "user.not_found", message = "User not found." });
    return Results.Ok(await service.ListAsync(kycStatus.UserId, ct));
});

app.MapPost("/api/v1/transfers/internal", async (InternalTransferRequest req, ClaimsPrincipal user, HttpContext http, ITransferService service, CancellationToken ct) =>
{
    var key = RequireIdempotencyKey(http);
    var customerId = user.FindFirst("sub")?.Value ?? throw new DomainException("auth.subject_missing", "Authenticated subject is missing.");
    var result = await service.TransferInternalAsync(new(customerId, req.SourceWalletId, req.DestinationAccountNumber, req.AmountKobo, key, Correlation(http)), ct);
    return Results.Ok(result);
}).RequireAuthorization();

app.MapPost("/api/v1/transfers/outbound", async (OutboundTransferRequest req, ClaimsPrincipal user, HttpContext http, ITransferService service, CancellationToken ct) =>
{
    var key = RequireIdempotencyKey(http);
    var customerId = user.FindFirst("sub")?.Value ?? throw new DomainException("auth.subject_missing", "Authenticated subject is missing.");
    var result = await service.TransferOutboundAsync(new(customerId, req.SourceWalletId, req.DestinationAccountNumber, req.DestinationBankCode, req.DestinationAccountName, req.AmountKobo, key, Correlation(http)), ct);
    return Results.Ok(result);
}).RequireAuthorization();

app.MapPost("/api/v1/transfers/inbound", async (InboundTransferRequest req, HttpContext http, ITransferService service, CancellationToken ct) =>
{
    var key = RequireIdempotencyKey(http);
    var result = await service.TransferInboundAsync(new(req.DestinationAccountNumber, req.OriginatorBankCode, req.OriginatorAccountNumber, req.OriginatorAccountName, req.AmountKobo, key, Correlation(http)), ct);
    return Results.Ok(result);
}).RequireAuthorization();

app.MapGet("/api/v1/transfers/{transferId:guid}/status", async (Guid transferId, ClaimsPrincipal user, ITransferService service, CancellationToken ct) =>
{
    var customerId = user.FindFirst("sub")?.Value ?? throw new DomainException("auth.subject_missing", "Authenticated subject is missing.");
    var result = await service.QueryTransferStatusAsync(transferId, customerId, ct);
    return Results.Ok(result);
}).RequireAuthorization();

app.MapGet("/api/v1/transfers/{transferId:guid}", async (Guid transferId, ClaimsPrincipal user, ITransferService service, CancellationToken ct) =>
{
    var customerId = user.FindFirst("sub")?.Value ?? throw new DomainException("auth.subject_missing", "Authenticated subject is missing.");
    var transfer = await service.GetTransferDetailAsync(transferId, customerId, ct)
        ?? throw new DomainException("transfer.not_found", "Transfer not found.");
    return Results.Ok(transfer);
}).RequireAuthorization();

app.MapGet("/api/v1/admin/reconciliation", async (int? page, int? pageSize, IReconciliationService service, CancellationToken ct) =>
    Results.Ok(await service.ListReconciliationsAsync(page ?? 1, pageSize ?? 25, ct)))
.RequireAuthorization("AdminOrProductOwner");

app.MapGet("/api/v1/admin/reconciliation/{watDate}", async (DateOnly watDate, IReconciliationService service, CancellationToken ct) =>
{
    var report = await service.GetReconciliationAsync(watDate, ct);
    return report is null ? Results.NotFound(new { code = "reconciliation.not_found", message = "No reconciliation report for that date." }) : Results.Ok(report);
})
.RequireAuthorization("AdminOrProductOwner");

app.MapPost("/api/v1/admin/reconciliation/run", async (HttpContext http, IReconciliationService service, CancellationToken ct) =>
{
    var runBy = http.User.FindFirst("sub")?.Value ?? "ADMIN";
    var result = await service.RunReconciliationAsync(null, runBy, ct);
    return Results.Ok(result);
})
.RequireAuthorization("AdminOrProductOwner");

app.MapGet("/api/v1/admin/external-accounts", async (IExternalAccountService service, CancellationToken ct) =>
    Results.Ok(await service.GetAllAsync(ct)))
.RequireAuthorization("AdminOrProductOwner");

app.MapPost("/api/v1/admin/transfers/{transferId:guid}/repost", async (Guid transferId, ITransferService service, CancellationToken ct) =>
{
    var result = await service.RepostAsync(transferId, ct);
    return Results.Ok(new { result.TransferId, result.Status, message = result.Message });
})
.RequireAuthorization("AdminOrProductOwner");

app.MapPost("/api/v1/admin/settlement/credit", async (AdminCreditRequest req, HttpContext http, IWalletService service, CancellationToken ct) =>
{
    if (req.AmountKobo <= 0)
        return Results.BadRequest(new { code = "admin.invalid_request", message = "A positive AmountKobo is required." });
    var correlationId = Correlation(http);
    var actor = http.User.FindFirst("sub")?.Value ?? "ADMIN";
    var result = await service.CreditSettlementAsync(req.AmountKobo, actor, correlationId, ct);
    return Results.Ok(new { result.AccountNumber, result.Currency, result.BalanceKobo });
})
.RequireAuthorization("AdminOrProductOwner");

app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (DomainException ex)
    {
        var status = ex.Code.Contains("forbidden") ? 403
            : ex.Code == "idempotency.required" ? 422
            : ex.Code.Contains("not_found") ? 404
            : 400;
        ctx.Response.StatusCode = status;
        await Results.Problem(statusCode: status, title: ex.Message, type: $"https://novawallet/errors/{ex.Code}",
            extensions: new Dictionary<string, object?> { ["code"] = ex.Code }).ExecuteAsync(ctx);
    }
});

app.Run();

static string RequireIdempotencyKey(HttpContext ctx)
{
    var key = ctx.Request.Headers["Idempotency-Key"].ToString();
    return string.IsNullOrWhiteSpace(key) ? throw new DomainException("idempotency.required", "Idempotency-Key header is required.") : key;
}

static string Correlation(HttpContext ctx) =>
    ctx.Request.Headers.TryGetValue("X-Correlation-Id", out var value) && !string.IsNullOrWhiteSpace(value) ? value.ToString() : Guid.NewGuid().ToString("N");

namespace NovaWallet.WalletApi
{
    record CreditRequest(long AmountKobo);
    record SubmitKycRequest(KycDocumentType DocumentType, string DocumentNumber);
    record CreateWalletRequest(string? Currency, string? AccountName);
    record AdminCreditRequest(long AmountKobo);

    public sealed class FeePolicyOptions
    {
        public long OutboundFeeKobo { get; set; } = 50_000;
        public decimal VatRate { get; set; } = 0.075m;
    }
}
