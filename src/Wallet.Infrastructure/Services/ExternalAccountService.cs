using NovaWallet.Application;
using NovaWallet.Application.Repositories;
using NovaWallet.Domain;

namespace NovaWallet.Infrastructure.Services;

public sealed class ExternalAccountService(IExternalAccountRepository repo) : IExternalAccountService
{
    public async Task<IReadOnlyList<ExternalAccountResult>> GetAllAsync(CancellationToken ct)
    {
        var accounts = await repo.GetAllAsync(ct);
        return accounts.Select(x => new ExternalAccountResult(x.AccountKey, x.Type, x.BankName, x.BankCode, x.AccountNumber, x.AccountName, x.BalanceKobo)).ToList();
    }
}
