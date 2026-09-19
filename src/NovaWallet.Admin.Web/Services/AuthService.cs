using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;

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

public sealed class CustomAuthStateProvider(IHttpContextAccessor httpContextAccessor, AuthService auth) : AuthenticationStateProvider
{
    public override Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var user = httpContextAccessor.HttpContext?.User;
        if (user is not null && user.Identity?.IsAuthenticated == true)
        {
            if (!auth.IsAuthenticated)
            {
                var name = user.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value ?? "admin";
                auth.SetAuthenticated(name);
            }
            return Task.FromResult(new AuthenticationState(user));
        }

        if (auth.IsAuthenticated)
        {
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, auth.Username ?? "admin"),
                new Claim(ClaimTypes.Role, "admin"),
            }, "local");
            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
        }

        return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    public void NotifyChanged() => NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
}
