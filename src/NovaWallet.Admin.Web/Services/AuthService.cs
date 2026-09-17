using System.Security.Cryptography;
using System.Text;

namespace NovaWallet.Admin.Web.Services;

public sealed class AuthService
{
    private readonly AdminCredentialOptions _creds;
    public string? Username { get; private set; }
    public string? DisplayName { get; private set; }
    public bool IsAuthenticated { get; private set; }

    public AuthService(IConfiguration config)
    {
        _creds = config.GetSection("AdminCredentials").Get<AdminCredentialOptions>() ?? new AdminCredentialOptions();
    }

    public void SetAuthenticated(string username)
    {
        Username = username;
        DisplayName = username;
        IsAuthenticated = true;
    }

    public void Logout()
    {
        Username = null;
        DisplayName = null;
        IsAuthenticated = false;
    }

    public bool Validate(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return false;
        var userMatch = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(username.ToLowerInvariant()),
            Encoding.UTF8.GetBytes(_creds.Username.ToLowerInvariant()));
        var passMatch = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(password),
            Encoding.UTF8.GetBytes(_creds.Password));
        return userMatch && passMatch;
    }
}

public sealed class AdminCredentialOptions
{
    public string Username { get; set; } = "admin";
    public string Password { get; set; } = "";
}

public sealed class CustomAuthStateProvider(AuthService auth) : Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider
{
    public override Task<Microsoft.AspNetCore.Components.Authorization.AuthenticationState> GetAuthenticationStateAsync()
    {
        if (auth.IsAuthenticated)
        {
            var identity = new System.Security.Claims.ClaimsIdentity(new[]
            {
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, auth.Username ?? "admin"),
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "admin"),
            }, "local");
            return Task.FromResult(new Microsoft.AspNetCore.Components.Authorization.AuthenticationState(new System.Security.Claims.ClaimsPrincipal(identity)));
        }
        return Task.FromResult(new Microsoft.AspNetCore.Components.Authorization.AuthenticationState(new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity())));
    }

    public void NotifyChanged() => NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
}
