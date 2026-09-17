using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace NovaWallet.Admin.Web.Services;

public sealed class AuthService
{
    private readonly HttpClient _http;
    public string? Token { get; set; }
    public string? ChannelKey { get; set; }
    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(Token);

    public AuthService(HttpClient http) => _http = http;

    public void SetToken(string token, string channelKey)
    {
        Token = token;
        ChannelKey = channelKey;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public void Logout()
    {
        Token = null;
        ChannelKey = null;
        _http.DefaultRequestHeaders.Authorization = null;
    }

    public async Task<bool> LoginAsync(string appKey, string appSecret)
    {
        var response = await _http.PostAsJsonAsync("/api/v1/auth/token", new { AppKey = appKey, AppSecret = appSecret });
        if (!response.IsSuccessStatusCode) return false;
        var result = await response.Content.ReadFromJsonAsync<TokenResponse>();
        if (result is null) return false;
        SetToken(result.Token, "");
        return true;
    }

    public record TokenResponse(string Token, int ExpiresInSeconds);
}

public sealed class ApiClient(HttpClient http) { public HttpClient Http { get; } = http; }

public sealed class CustomAuthStateProvider(AuthService auth) : Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider
{
    public override Task<Microsoft.AspNetCore.Components.Authorization.AuthenticationState> GetAuthenticationStateAsync()
    {
        if (auth.IsAuthenticated)
        {
            var identity = new System.Security.Claims.ClaimsIdentity(new[]
            {
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, auth.ChannelKey ?? "admin"),
            }, "jwt");
            return Task.FromResult(new Microsoft.AspNetCore.Components.Authorization.AuthenticationState(new System.Security.Claims.ClaimsPrincipal(identity)));
        }
        return Task.FromResult(new Microsoft.AspNetCore.Components.Authorization.AuthenticationState(new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity())));
    }

    public void NotifyChanged() => NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
}
