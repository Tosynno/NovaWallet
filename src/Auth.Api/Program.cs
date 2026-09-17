using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using NovaWallet.Domain;
using NovaWallet.Infrastructure;

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

app.MapPost("/api/v1/auth/token", async (TokenRequest req, AppDbContext db, IConfiguration cfg, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.AppKey) || string.IsNullOrWhiteSpace(req.AppSecret))
        return Results.BadRequest(new { code = "auth.missing_credentials", message = "AppKey and AppSecret are required." });

    var channel = await db.Channels.SingleOrDefaultAsync(x => x.AppKey == req.AppKey, ct);
    if (channel is null || channel.Status != ChannelStatus.Active)
        return Results.Unauthorized();

    var secretHash = HashSecret(req.AppSecret);
    if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(channel.AppSecretHash), Encoding.UTF8.GetBytes(secretHash)))
        return Results.Unauthorized();

    var expiryMinutes = cfg.GetValue<int>("Jwt:ExpiryMinutes", 5);
    var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(cfg["Jwt:Key"]!));
    var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
    var claims = new List<Claim>
    {
        new("sub", channel.ChannelKey),
        new("channel", channel.ChannelName),
        new("channel_key", channel.ChannelKey)
    };
    var token = new JwtSecurityToken(
        issuer: cfg["Jwt:Issuer"], audience: cfg["Jwt:Audience"],
        claims: claims, expires: DateTime.UtcNow.AddMinutes(expiryMinutes), signingCredentials: creds);
    return Results.Ok(new { token = new JwtSecurityTokenHandler().WriteToken(token), expiresInSeconds = expiryMinutes * 60 });
}).AllowAnonymous();

app.MapPost("/api/v1/channels", async (CreateChannelRequest req, AppDbContext db, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.ChannelKey) || string.IsNullOrWhiteSpace(req.ChannelName))
        return Results.BadRequest(new { code = "channel.invalid", message = "ChannelKey and ChannelName are required." });

    if (await db.Channels.AnyAsync(x => x.ChannelKey == req.ChannelKey, ct))
        return Results.Conflict(new { code = "channel.duplicate", message = "ChannelKey already exists." });

    var appKey = GenerateAppKey();
    var appSecret = GenerateAppSecret();
    var channel = Channel.Create(req.ChannelKey, req.ChannelName, appKey, HashSecret(appSecret), DateTimeOffset.UtcNow);
    db.Channels.Add(channel);
    await db.SaveChangesAsync(ct);

    return Results.Created($"/api/v1/channels/{channel.ChannelKey}", new { channel.ChannelKey, channel.ChannelName, channel.Status, AppKey = appKey, AppSecret = appSecret, message = "Save the AppSecret securely. It will not be shown again." });
}).RequireAuthorization("AdminOrProductOwner");

app.MapGet("/api/v1/channels", async (AppDbContext db, CancellationToken ct) =>
{
    var channels = await db.Channels.AsNoTracking().Select(x => new { x.Id, x.ChannelKey, x.ChannelName, x.AppKey, x.Status, x.CreatedAt, x.UpdatedAt }).ToListAsync(ct);
    return Results.Ok(channels);
}).RequireAuthorization("AdminOrProductOwner");

app.MapPut("/api/v1/channels/{channelKey}/keys", async (string channelKey, AppDbContext db, CancellationToken ct) =>
{
    var channel = await db.Channels.SingleOrDefaultAsync(x => x.ChannelKey == channelKey, ct);
    if (channel is null) return Results.NotFound(new { code = "channel.not_found", message = "Channel not found." });

    var newAppKey = GenerateAppKey();
    var newAppSecret = GenerateAppSecret();
    channel.RotateKeys(newAppKey, HashSecret(newAppSecret), DateTimeOffset.UtcNow);
    await db.SaveChangesAsync(ct);

    return Results.Ok(new { channel.ChannelKey, AppKey = newAppKey, AppSecret = newAppSecret, message = "Save the AppSecret securely. It will not be shown again." });
}).RequireAuthorization("AdminOrProductOwner");

app.MapPut("/api/v1/channels/{channelKey}/status", async (string channelKey, UpdateChannelStatusRequest req, AppDbContext db, CancellationToken ct) =>
{
    var channel = await db.Channels.SingleOrDefaultAsync(x => x.ChannelKey == channelKey, ct);
    if (channel is null) return Results.NotFound(new { code = "channel.not_found", message = "Channel not found." });

    var now = DateTimeOffset.UtcNow;
    if (req.Status == ChannelStatus.Active) channel.Activate(now);
    else if (req.Status == ChannelStatus.Suspended) channel.Suspend(now);
    else if (req.Status == ChannelStatus.Revoked) channel.Revoke(now);

    await db.SaveChangesAsync(ct);
    return Results.Ok(new { channel.ChannelKey, channel.Status });
}).RequireAuthorization("AdminOrProductOwner");

app.MapPost("/api/v1/users", async (CreateUserRequest req, AppDbContext db, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.CustomerId) || string.IsNullOrWhiteSpace(req.Email))
        return Results.BadRequest(new { code = "user.invalid", message = "CustomerId and Email are required." });

    if (await db.Users.AnyAsync(x => x.CustomerId == req.CustomerId || x.Email == req.Email, ct))
        return Results.Conflict(new { code = "user.duplicate", message = "CustomerId or Email already exists." });

    var user = User.Create(req.CustomerId, req.Email, req.FirstName ?? "", req.LastName ?? "", UserRole.Customer, DateTimeOffset.UtcNow);
    if (!string.IsNullOrWhiteSpace(req.PhoneNumber)) user.SetPhoneNumber(req.PhoneNumber, DateTimeOffset.UtcNow);

    db.Users.Add(user);
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/v1/users/{user.Id}", new { user.Id, user.CustomerId, user.Email, user.KycStatus });
}).RequireAuthorization();

app.MapGet("/api/v1/users/{userId:long}", async (long userId, AppDbContext db, CancellationToken ct) =>
{
    var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, ct);
    return user is null ? Results.NotFound(new { code = "user.not_found", message = "User not found." }) : Results.Ok(new { user.Id, user.CustomerId, user.Email, user.PhoneNumber, user.FirstName, user.LastName, user.KycStatus, user.CreatedAt });
}).RequireAuthorization();

app.MapGet("/api/v1/users/by-customer/{customerId}", async (string customerId, AppDbContext db, CancellationToken ct) =>
{
    var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.CustomerId == customerId, ct);
    return user is null ? Results.NotFound(new { code = "user.not_found", message = "User not found." }) : Results.Ok(new { user.Id, user.CustomerId, user.Email, user.PhoneNumber, user.FirstName, user.LastName, user.KycStatus, user.CreatedAt });
}).RequireAuthorization();

app.MapPost("/api/v1/users/{userId:long}/kyc", async (long userId, SubmitKycRequest req, AppDbContext db, CancellationToken ct) =>
{
    var user = await db.Users.SingleOrDefaultAsync(x => x.Id == userId, ct);
    if (user is null) return Results.NotFound(new { code = "user.not_found", message = "User not found." });

    var doc = KycDocument.Create(userId, req.DocumentType, req.DocumentNumber, DateTimeOffset.UtcNow);
    db.KycDocuments.Add(doc);
    user.SubmitKyc(DateTimeOffset.UtcNow);
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/v1/users/{userId}/kyc/{doc.Id}", new { doc.Id, doc.DocumentType, doc.DocumentNumber, doc.Status });
}).RequireAuthorization();

app.MapGet("/api/v1/users/{userId:long}/kyc", async (long userId, AppDbContext db, CancellationToken ct) =>
{
    var docs = await db.KycDocuments.AsNoTracking().Where(x => x.UserId == userId).Select(x => new { x.Id, x.DocumentType, x.DocumentNumber, x.Status, x.SubmittedAt, x.ReviewedAt, x.ReviewNotes }).ToListAsync(ct);
    return Results.Ok(docs);
}).RequireAuthorization();

app.MapPut("/api/v1/users/{userId:long}/kyc/{docId:long}/approve", async (long userId, long docId, AppDbContext db, CancellationToken ct) =>
{
    var user = await db.Users.SingleOrDefaultAsync(x => x.Id == userId, ct);
    if (user is null) return Results.NotFound(new { code = "user.not_found", message = "User not found." });

    var doc = await db.KycDocuments.SingleOrDefaultAsync(x => x.Id == docId && x.UserId == userId, ct);
    if (doc is null) return Results.NotFound(new { code = "kyc.not_found", message = "KYC document not found." });

    doc.Approve(null, DateTimeOffset.UtcNow);
    user.VerifyKyc(DateTimeOffset.UtcNow);
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { doc.Id, doc.Status, user.KycStatus });
}).RequireAuthorization("AdminOrProductOwner");

app.MapPut("/api/v1/users/{userId:long}/kyc/{docId:long}/reject", async (long userId, long docId, RejectKycRequest req, AppDbContext db, CancellationToken ct) =>
{
    var user = await db.Users.SingleOrDefaultAsync(x => x.Id == userId, ct);
    if (user is null) return Results.NotFound(new { code = "user.not_found", message = "User not found." });

    var doc = await db.KycDocuments.SingleOrDefaultAsync(x => x.Id == docId && x.UserId == userId, ct);
    if (doc is null) return Results.NotFound(new { code = "kyc.not_found", message = "KYC document not found." });

    doc.Reject(req.Notes ?? "Rejected", DateTimeOffset.UtcNow);
    user.RejectKyc(DateTimeOffset.UtcNow);
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { doc.Id, doc.Status, doc.ReviewNotes, user.KycStatus });
}).RequireAuthorization("AdminOrProductOwner");

app.Run();

static string HashSecret(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
static string GenerateAppKey() => "AK" + Guid.NewGuid().ToString("N")[..24].ToUpperInvariant();
static string GenerateAppSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

record TokenRequest(string AppKey, string AppSecret);
record CreateChannelRequest(string ChannelKey, string ChannelName);
record UpdateChannelStatusRequest(ChannelStatus Status);
record CreateUserRequest(string CustomerId, string Email, string? FirstName, string? LastName, string? PhoneNumber);
record SubmitKycRequest(KycDocumentType DocumentType, string DocumentNumber);
record RejectKycRequest(string? Notes);
