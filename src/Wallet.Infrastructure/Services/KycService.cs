using Microsoft.EntityFrameworkCore;
using NovaWallet.Application;
using NovaWallet.Domain;
using NovaWallet.Infrastructure;

namespace NovaWallet.Infrastructure.Services;

public sealed class KycService(AppDbContext db) : IKycService
{
    public async Task<KycSubmittedResult?> SubmitAsync(long userId, KycDocumentType documentType, string documentNumber, CancellationToken ct)
    {
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == userId, ct);
        if (user is null) return null;

        var doc = KycDocument.Create(userId, documentType, documentNumber, DateTimeOffset.UtcNow);
        db.KycDocuments.Add(doc);
        user.SubmitKyc(DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(ct);
        return new KycSubmittedResult(doc.Id, doc.DocumentType, doc.DocumentNumber, doc.Status);
    }

    public async Task<IReadOnlyList<KycDocumentResult>> ListAsync(long userId, CancellationToken ct)
    {
        return await db.KycDocuments.AsNoTracking()
            .Where(x => x.UserId == userId)
            .Select(x => new KycDocumentResult(x.Id, x.DocumentType, x.DocumentNumber, x.Status, x.SubmittedAt, x.ReviewedAt, x.ReviewNotes))
            .ToListAsync(ct);
    }

    public async Task<KycReviewResult?> ApproveAsync(long userId, long docId, CancellationToken ct)
    {
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == userId, ct);
        if (user is null) return null;

        var doc = await db.KycDocuments.SingleOrDefaultAsync(x => x.Id == docId && x.UserId == userId, ct);
        if (doc is null) return null;

        var now = DateTimeOffset.UtcNow;
        doc.Approve(null, now);
        user.VerifyKyc(now);
        await db.SaveChangesAsync(ct);
        return new KycReviewResult(doc.Id, doc.Status, user.KycStatus, doc.ReviewNotes);
    }

    public async Task<KycReviewResult?> RejectAsync(long userId, long docId, string? notes, CancellationToken ct)
    {
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == userId, ct);
        if (user is null) return null;

        var doc = await db.KycDocuments.SingleOrDefaultAsync(x => x.Id == docId && x.UserId == userId, ct);
        if (doc is null) return null;

        var now = DateTimeOffset.UtcNow;
        doc.Reject(notes ?? "Rejected", now);
        user.RejectKyc(now);
        await db.SaveChangesAsync(ct);
        return new KycReviewResult(doc.Id, doc.Status, user.KycStatus, doc.ReviewNotes);
    }
}
