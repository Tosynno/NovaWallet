using NovaWallet.Application;

namespace NovaWallet.Infrastructure;

public sealed class MockNipPaymentRail : IPaymentRail
{
    public Task<PaymentResult> SendAsync(PaymentInstruction instruction, CancellationToken ct)
    {
        var externalReference = $"NIP-{instruction.TransferId:N}";
        return Task.FromResult(new PaymentResult(PaymentRailStatus.Sent, externalReference, null));
    }

    public Task<PaymentStatusResult> GetStatusAsync(string externalReference, CancellationToken ct)
    {
        if (externalReference.StartsWith("NIP-"))
            return Task.FromResult(new PaymentStatusResult(PaymentRailStatus.Sent, externalReference, null));
        return Task.FromResult(new PaymentStatusResult(PaymentRailStatus.Unknown, null, "Reference not found."));
    }
}
