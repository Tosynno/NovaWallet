using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using NovaWallet.Application;
using NovaWallet.Domain;
using NovaWallet.Infrastructure;

namespace NovaWallet.Infrastructure.Services;

public sealed class AuthService(AppDbContext db, IConfiguration config) : IAuthService
{
    public async Task<ChannelTokenResult?> IssueChannelTokenAsync(string appKey, string appSecret, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(appKey) || string.IsNullOrWhiteSpace(appSecret))
            return null;

        var channel = await db.Channels.SingleOrDefaultAsync(x => x.AppKey == appKey, ct);
        if (channel is null || channel.Status != ChannelStatus.Active)
            return null;

        var secretHash = HashSecret(appSecret);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(channel.AppSecretHash), Encoding.UTF8.GetBytes(secretHash)))
            return null;

        var expiryMinutes = int.TryParse(config["Jwt:ExpiryMinutes"], out var em) ? em : 5;
        var token = IssueJwt(channel.ChannelKey, new[] { ("channel", channel.ChannelName), ("channel_key", channel.ChannelKey) }, expiryMinutes);
        return new ChannelTokenResult(token, expiryMinutes * 60);
    }

    public async Task<LoginResult?> LoginAsync(string email, string password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return null;

        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Email == email, ct);
        if (user is null || string.IsNullOrEmpty(user.PasswordHash))
            return null;

        var passwordHash = HashSecret(password);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(user.PasswordHash), Encoding.UTF8.GetBytes(passwordHash)))
            return null;

        var expiryMinutes = int.TryParse(config["Jwt:ExpiryMinutes"], out var lm) ? lm : 60;
        var token = IssueJwt(user.CustomerId, new[] { ("email", user.Email), ("role", user.Role.ToString().ToLowerInvariant()) }, expiryMinutes);
        return new LoginResult(token, expiryMinutes * 60, user.CustomerId);
    }

    public async Task<RegisterResult> RegisterAsync(string email, string password, string firstName, string lastName, string? phoneNumber, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return new RegisterResult(false, "Email and Password are required.", null, 0, null, null, null);

        if (await db.Users.AnyAsync(x => x.Email == email, ct))
            return new RegisterResult(false, "Email already registered.", null, 0, null, null, null);

        var customerId = Guid.NewGuid().ToString("N");
        var passwordHash = HashSecret(password);
        var now = DateTimeOffset.UtcNow;
        var expiryMinutes = int.TryParse(config["Jwt:ExpiryMinutes"], out var rm) ? rm : 60;
        Guid walletId = Guid.Empty;
        string accountNumber = "";

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                var user = User.Create(customerId, email, firstName, lastName, UserRole.Customer, now, passwordHash);
                if (!string.IsNullOrWhiteSpace(phoneNumber)) user.SetPhoneNumber(phoneNumber, now);
                db.Users.Add(user);

                accountNumber = AccountNumberGenerator.Generate();
                var wallet = Wallet.CreateCustomer(customerId, accountNumber, now);
                db.Wallets.Add(wallet);

                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                walletId = wallet.Id;
                break;
            }
            catch (DbUpdateException) when (attempt < 4)
            {
                await tx.RollbackAsync(ct);
                db.ChangeTracker.Clear();
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }

        if (walletId == Guid.Empty)
            return new RegisterResult(false, "Could not allocate a unique account number.", null, 0, null, null, null);

        var token = IssueJwt(customerId, new[] { ("email", email), ("role", "customer") }, expiryMinutes);
        return new RegisterResult(true, null, token, expiryMinutes * 60, customerId, walletId, accountNumber);
    }

    private string IssueJwt(string subject, (string type, string value)[] extraClaims, int expiryMinutes)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:Key"]!));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim> { new("sub", subject) };
        foreach (var (type, value) in extraClaims)
            claims.Add(new Claim(type, value));
        var token = new JwtSecurityToken(
            issuer: config["Jwt:Issuer"], audience: config["Jwt:Audience"],
            claims: claims, expires: DateTime.UtcNow.AddMinutes(expiryMinutes), signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string HashSecret(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}
