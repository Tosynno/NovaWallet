using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using NovaWallet.Application;
using NovaWallet.Domain;
using NovaWallet.Infrastructure;
using NovaWallet.Infrastructure.Services;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlServer(config.GetConnectionString("LedgerDb")));
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, ValidateIssuerSigningKey = true,
        ValidIssuer = config["Jwt:Issuer"], ValidAudience = config["Jwt:Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key is required.")))
    };
});
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("AdminOrProductOwner", p => p.RequireRole(Roles.Admin, Roles.ProductOwner));
});

builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IChannelService, ChannelService>();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<IKycService, KycService>();

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

app.MapPost("/api/v1/auth/token", async (TokenRequest req, IAuthService service, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.AppKey) || string.IsNullOrWhiteSpace(req.AppSecret))
        return Results.BadRequest(new { code = "auth.missing_credentials", message = "AppKey and AppSecret are required." });
    var result = await service.IssueChannelTokenAsync(req.AppKey, req.AppSecret, ct);
    return result is null ? Results.Unauthorized() : Results.Ok(new { token = result.Token, expiresInSeconds = result.ExpiresInSeconds });
}).AllowAnonymous();

app.MapPost("/api/v1/auth/login", async (LoginRequest req, IAuthService service, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
        return Results.BadRequest(new { code = "auth.missing_credentials", message = "Email and Password are required." });
    var result = await service.LoginAsync(req.Email, req.Password, ct);
    return result is null ? Results.Unauthorized() : Results.Ok(new { token = result.Token, expiresInSeconds = result.ExpiresInSeconds, customerId = result.CustomerId });
}).AllowAnonymous();

app.MapPost("/api/v1/auth/register", async (RegisterRequest req, IAuthService service, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
        return Results.BadRequest(new { code = "auth.missing_credentials", message = "Email and Password are required." });
    var result = await service.RegisterAsync(req.Email, req.Password, req.FirstName ?? "", req.LastName ?? "", req.PhoneNumber, ct);
    if (!result.Success)
        return result.Error == "Email already registered."
            ? Results.Conflict(new { code = "user.duplicate", message = result.Error })
            : Results.Problem(statusCode: 500, title: result.Error);
    return Results.Ok(new { token = result.Token, expiresInSeconds = result.ExpiresInSeconds, customerId = result.CustomerId, walletId = result.WalletId, accountNumber = result.AccountNumber });
}).AllowAnonymous();

app.MapPost("/api/v1/channels", async (CreateChannelRequest req, IChannelService service, CancellationToken ct) =>
{
    try
    {
        var result = await service.CreateAsync(req.ChannelKey, req.ChannelName, ct);
        return Results.Created($"/api/v1/channels/{result.ChannelKey}", new { result.ChannelKey, result.ChannelName, result.Status, AppKey = result.AppKey, AppSecret = result.AppSecret, message = "Save the AppSecret securely. It will not be shown again." });
    }
    catch (DomainException ex) when (ex.Code == "channel.duplicate")
    {
        return Results.Conflict(new { code = ex.Code, message = ex.Message });
    }
    catch (DomainException ex)
    {
        return Results.BadRequest(new { code = ex.Code, message = ex.Message });
    }
}).RequireAuthorization("AdminOrProductOwner");

app.MapGet("/api/v1/channels", async (IChannelService service, CancellationToken ct) =>
    Results.Ok(await service.ListAsync(ct))).RequireAuthorization("AdminOrProductOwner");

app.MapPut("/api/v1/channels/{channelKey}/keys", async (string channelKey, IChannelService service, CancellationToken ct) =>
{
    var result = await service.RotateKeysAsync(channelKey, ct);
    return result is null ? Results.NotFound(new { code = "channel.not_found", message = "Channel not found." }) : Results.Ok(new { result.ChannelKey, AppKey = result.AppKey, AppSecret = result.AppSecret, message = "Save the AppSecret securely. It will not be shown again." });
}).RequireAuthorization("AdminOrProductOwner");

app.MapPut("/api/v1/channels/{channelKey}/status", async (string channelKey, UpdateChannelStatusRequest req, IChannelService service, CancellationToken ct) =>
{
    try
    {
        var result = await service.UpdateStatusAsync(channelKey, req.Status, ct);
        return result is null ? Results.NotFound(new { code = "channel.not_found", message = "Channel not found." }) : Results.Ok(new { result.ChannelKey, result.Status });
    }
    catch (DomainException ex)
    {
        return Results.BadRequest(new { code = ex.Code, message = ex.Message });
    }
}).RequireAuthorization("AdminOrProductOwner");

app.MapPost("/api/v1/users", async (CreateUserRequest req, IUserService service, CancellationToken ct) =>
{
    try
    {
        var result = await service.CreateAsync(req.CustomerId, req.Email, req.FirstName, req.LastName, req.PhoneNumber, ct);
        return Results.Created($"/api/v1/users/{result.Id}", new { result.Id, result.CustomerId, result.Email, result.KycStatus });
    }
    catch (DomainException ex) when (ex.Code == "user.duplicate")
    {
        return Results.Conflict(new { code = ex.Code, message = ex.Message });
    }
    catch (DomainException ex)
    {
        return Results.BadRequest(new { code = ex.Code, message = ex.Message });
    }
}).RequireAuthorization();

app.MapGet("/api/v1/users/{userId:long}", async (long userId, IUserService service, CancellationToken ct) =>
{
    var user = await service.GetByIdAsync(userId, ct);
    return user is null ? Results.NotFound(new { code = "user.not_found", message = "User not found." }) : Results.Ok(user);
}).RequireAuthorization();

app.MapGet("/api/v1/users/by-customer/{customerId}", async (string customerId, IUserService service, CancellationToken ct) =>
{
    var user = await service.GetByCustomerAsync(customerId, ct);
    return user is null ? Results.NotFound(new { code = "user.not_found", message = "User not found." }) : Results.Ok(user);
}).RequireAuthorization();

app.MapPost("/api/v1/users/{userId:long}/kyc", async (long userId, SubmitKycRequest req, IKycService service, CancellationToken ct) =>
{
    var result = await service.SubmitAsync(userId, req.DocumentType, req.DocumentNumber, ct);
    return result is null ? Results.NotFound(new { code = "user.not_found", message = "User not found." }) : Results.Created($"/api/v1/users/{userId}/kyc/{result.Id}", new { result.Id, result.DocumentType, result.DocumentNumber, result.Status });
}).RequireAuthorization();

app.MapGet("/api/v1/users/{userId:long}/kyc", async (long userId, IKycService service, CancellationToken ct) =>
    Results.Ok(await service.ListAsync(userId, ct))).RequireAuthorization();

app.MapPut("/api/v1/users/{userId:long}/kyc/{docId:long}/approve", async (long userId, long docId, IKycService service, CancellationToken ct) =>
{
    var result = await service.ApproveAsync(userId, docId, ct);
    return result is null ? Results.NotFound(new { code = "kyc.not_found", message = "User or KYC document not found." }) : Results.Ok(new { result.DocId, result.DocStatus, result.UserKycStatus });
}).RequireAuthorization("AdminOrProductOwner");

app.MapPut("/api/v1/users/{userId:long}/kyc/{docId:long}/reject", async (long userId, long docId, RejectKycRequest req, IKycService service, CancellationToken ct) =>
{
    var result = await service.RejectAsync(userId, docId, req.Notes, ct);
    return result is null ? Results.NotFound(new { code = "kyc.not_found", message = "User or KYC document not found." }) : Results.Ok(new { result.DocId, result.DocStatus, result.ReviewNotes, result.UserKycStatus });
}).RequireAuthorization("AdminOrProductOwner");

app.Run();

record TokenRequest(string AppKey, string AppSecret);
record LoginRequest(string Email, string Password);
record RegisterRequest(string Email, string Password, string? FirstName, string? LastName, string? PhoneNumber);
record CreateChannelRequest(string ChannelKey, string ChannelName);
record UpdateChannelStatusRequest(ChannelStatus Status);
record CreateUserRequest(string CustomerId, string Email, string? FirstName, string? LastName, string? PhoneNumber);
record SubmitKycRequest(KycDocumentType DocumentType, string DocumentNumber);
record RejectKycRequest(string? Notes);
