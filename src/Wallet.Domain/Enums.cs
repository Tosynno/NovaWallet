namespace NovaWallet.Domain;

public enum AccountType { Customer, Settlement, Vat, Income }
public enum ExternalAccountType { LedgerHolding, SettlementHolding, IncomeHolding, VatHolding }
public enum TransferType { Internal, Outbound, Inbound }
public enum TransferStatus { Submitted, Settled, Failed, Unknown }
public enum SettlementStatus { Pending, Processing, Succeeded, Failed, DeadLetter }
public enum LedgerDirection { Debit, Credit }
public enum ReconciliationStatus { Pending, InProgress, Balanced, Imbalanced }
public enum ChannelStatus { Active, Suspended, Revoked }
public enum KycStatus { Pending, Verified, Rejected }
public enum KycDocumentType { NIN, BVN, DriversLicense, InternationalPassport, UtilityBill }
public enum UserRole { Customer, Admin, ProductOwner }

public static class SystemAccountKeys
{
    public const string Settlement = "SETTLEMENT";
    public const string Vat = "VAT";
    public const string Income = "INCOME";
}

public static class ExternalAccountKeys
{
    public const string LedgerHolding = "LEDGER_HOLDING";
    public const string SettlementHolding = "SETTLEMENT_HOLDING";
    public const string IncomeHolding = "INCOME_HOLDING";
    public const string VatHolding = "VAT_HOLDING";
}

public static class Roles
{
    public const string Admin = "admin";
    public const string ProductOwner = "product-owner";
}
