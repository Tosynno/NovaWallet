using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NovaWallet.Application;
using NovaWallet.Domain;
using NovaWallet.Infrastructure;

namespace NovaWallet.Infrastructure.Services;

public sealed class ChannelService(AppDbContext db) : IChannelService
{
    public async Task<ChannelCreatedResult> CreateAsync(string channelKey, string channelName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(channelKey) || string.IsNullOrWhiteSpace(channelName))
            throw new DomainException("channel.invalid", "ChannelKey and ChannelName are required.");

        if (await db.Channels.AnyAsync(x => x.ChannelKey == channelKey, ct))
            throw new DomainException("channel.duplicate", "ChannelKey already exists.");

        var appKey = GenerateAppKey();
        var appSecret = GenerateAppSecret();
        var channel = Channel.Create(channelKey, channelName, appKey, HashSecret(appSecret), DateTimeOffset.UtcNow);
        db.Channels.Add(channel);
        await db.SaveChangesAsync(ct);

        return new ChannelCreatedResult(channel.ChannelKey, channel.ChannelName, channel.Status, appKey, appSecret);
    }

    public async Task<IReadOnlyList<ChannelListItem>> ListAsync(CancellationToken ct)
    {
        return await db.Channels.AsNoTracking()
            .Select(x => new ChannelListItem(x.Id, x.ChannelKey, x.ChannelName, x.AppKey, x.Status, x.CreatedAt, x.UpdatedAt))
            .ToListAsync(ct);
    }

    public async Task<ChannelKeysRotatedResult?> RotateKeysAsync(string channelKey, CancellationToken ct)
    {
        var channel = await db.Channels.SingleOrDefaultAsync(x => x.ChannelKey == channelKey, ct);
        if (channel is null) return null;

        var newAppKey = GenerateAppKey();
        var newAppSecret = GenerateAppSecret();
        channel.RotateKeys(newAppKey, HashSecret(newAppSecret), DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(ct);

        return new ChannelKeysRotatedResult(channel.ChannelKey, newAppKey, newAppSecret);
    }

    public async Task<ChannelStatusResult?> UpdateStatusAsync(string channelKey, ChannelStatus status, CancellationToken ct)
    {
        var channel = await db.Channels.SingleOrDefaultAsync(x => x.ChannelKey == channelKey, ct);
        if (channel is null) return null;

        var now = DateTimeOffset.UtcNow;
        if (status == ChannelStatus.Active) channel.Activate(now);
        else if (status == ChannelStatus.Suspended) channel.Suspend(now);
        else if (status == ChannelStatus.Revoked) channel.Revoke(now);
        else throw new DomainException("channel.invalid_status", "Invalid channel status.");

        await db.SaveChangesAsync(ct);
        return new ChannelStatusResult(channel.ChannelKey, channel.Status);
    }

    private static string GenerateAppKey() => "AK" + Guid.NewGuid().ToString("N")[..24].ToUpperInvariant();
    private static string GenerateAppSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static string HashSecret(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}
