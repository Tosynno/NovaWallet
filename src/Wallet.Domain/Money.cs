namespace NovaWallet.Domain;

public readonly record struct Money(long Kobo)
{
    public static Money Zero => new(0);
    public bool IsPositive => Kobo > 0;
    public static Money Create(long kobo) => kobo < 0 ? throw new ArgumentOutOfRangeException(nameof(kobo)) : new(kobo);
    public static Money operator +(Money a, Money b) => new(checked(a.Kobo + b.Kobo));
    public static Money operator -(Money a, Money b)
    {
        var result = checked(a.Kobo - b.Kobo);
        if (result < 0) throw new InvalidOperationException("Money subtraction would be negative.");
        return new(result);
    }
}
