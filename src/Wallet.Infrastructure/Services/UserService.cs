using Microsoft.EntityFrameworkCore;
using NovaWallet.Application;
using NovaWallet.Domain;
using NovaWallet.Infrastructure;

namespace NovaWallet.Infrastructure.Services;

public sealed class UserService(AppDbContext db) : IUserService
{
    public async Task<UserResult?> GetByIdAsync(long userId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, ct);
        return user is null ? null : ToResult(user);
    }

    public async Task<UserResult?> GetByCustomerAsync(string customerId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.CustomerId == customerId, ct);
        return user is null ? null : ToResult(user);
    }

    public async Task<UserCreatedResult> CreateAsync(string customerId, string email, string? firstName, string? lastName, string? phoneNumber, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(customerId) || string.IsNullOrWhiteSpace(email))
            throw new DomainException("user.invalid", "CustomerId and Email are required.");

        if (await db.Users.AnyAsync(x => x.CustomerId == customerId || x.Email == email, ct))
            throw new DomainException("user.duplicate", "CustomerId or Email already exists.");

        var now = DateTimeOffset.UtcNow;
        var user = User.Create(customerId, email, firstName ?? "", lastName ?? "", UserRole.Customer, now);
        if (!string.IsNullOrWhiteSpace(phoneNumber)) user.SetPhoneNumber(phoneNumber, now);

        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return new UserCreatedResult(user.Id, user.CustomerId, user.Email, user.KycStatus);
    }

    private static UserResult ToResult(User user) =>
        new(user.Id, user.CustomerId, user.Email, user.PhoneNumber, user.FirstName, user.LastName, user.KycStatus, user.CreatedAt);
}
