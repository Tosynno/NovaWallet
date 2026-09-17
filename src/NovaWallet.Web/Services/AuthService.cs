using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace NovaWallet.Web.Services;

public sealed class AuthService
{
    private readonly HttpClient _http;
    private readonly EncryptionService _crypto;
    public string? Token { get; set; }
    public string? ChannelKey { get; set; }
    public string? ChannelName { get; set; }
    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(Token);

    public AuthService(HttpClient http, EncryptionService crypto)
    {
        _http = http;
        _crypto = crypto;
    }

    public void SetToken(string token, string channelKey, string channelName)
    {
        Token = token;
        ChannelKey = channelKey;
        ChannelName = channelName;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public void Logout()
    {
        Token = null;
        ChannelKey = null;
        ChannelName = null;
        _http.DefaultRequestHeaders.Authorization = null;
    }

    public async Task<bool> LoginAsync(string appKey, string appSecret)
    {
        var payload = JsonSerializer.Serialize(new { AppKey = appKey, AppSecret = appSecret });
        var encrypted = _crypto.Encrypt(payload);
        var content = new StringContent(JsonSerializer.Serialize(new { Data = encrypted }), Encoding.UTF8, "application/json");
        var response = await _http.PostAsync("/api/v1/auth/token", content);
        if (!response.IsSuccessStatusCode) return false;
        var raw = await response.Content.ReadAsStringAsync();
        var wrapper = JsonSerializer.Deserialize<EncryptedResponse>(raw);
        if (wrapper?.Data is null) return false;
        var decrypted = _crypto.Decrypt(wrapper.Data);
        var result = JsonSerializer.Deserialize<TokenResponse>(decrypted);
        if (result is null) return false;
        SetToken(result.Token, "", "");
        return true;
    }

    public record TokenResponse(string Token, int ExpiresInSeconds);
    public sealed record EncryptedResponse(string? Data);
}

public sealed class ApiClient
{
    public HttpClient Http { get; }
    private readonly EncryptionService _crypto;

    public ApiClient(HttpClient http, EncryptionService crypto)
    {
        Http = http;
        _crypto = crypto;
    }

    public async Task<T?> GetAsync<T>(string path)
    {
        var response = await Http.GetAsync(path);
        if (!response.IsSuccessStatusCode) return default;
        var raw = await response.Content.ReadAsStringAsync();
        var wrapper = System.Text.Json.JsonSerializer.Deserialize<EncryptedResponse>(raw);
        if (wrapper?.Data is null)
            return System.Text.Json.JsonSerializer.Deserialize<T>(raw);
        var decrypted = _crypto.Decrypt(wrapper.Data);
        return System.Text.Json.JsonSerializer.Deserialize<T>(decrypted);
    }

    public async Task<T?> PostAsync<T>(string path, object body)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(body);
        var encrypted = _crypto.Encrypt(payload);
        var wrapper = System.Text.Json.JsonSerializer.Serialize(new { Data = encrypted });
        var content = new StringContent(wrapper, Encoding.UTF8, "application/json");
        var response = await Http.PostAsync(path, content);
        if (!response.IsSuccessStatusCode) return default;
        var raw = await response.Content.ReadAsStringAsync();
        var respWrapper = System.Text.Json.JsonSerializer.Deserialize<EncryptedResponse>(raw);
        if (respWrapper?.Data is null)
            return System.Text.Json.JsonSerializer.Deserialize<T>(raw);
        var decrypted = _crypto.Decrypt(respWrapper.Data);
        return System.Text.Json.JsonSerializer.Deserialize<T>(decrypted);
    }

    public async Task<bool> PostNoResponseAsync(string path, object? body = null)
    {
        if (body is null)
        {
            var resp = await Http.PostAsync(path, null);
            return resp.IsSuccessStatusCode;
        }
        var payload = System.Text.Json.JsonSerializer.Serialize(body);
        var encrypted = _crypto.Encrypt(payload);
        var wrapper = System.Text.Json.JsonSerializer.Serialize(new { Data = encrypted });
        var content = new StringContent(wrapper, Encoding.UTF8, "application/json");
        var response = await Http.PostAsync(path, content);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> PutNoResponseAsync(string path, object? body = null)
    {
        if (body is null)
        {
            var resp = await Http.PutAsync(path, null);
            return resp.IsSuccessStatusCode;
        }
        var payload = System.Text.Json.JsonSerializer.Serialize(body);
        var encrypted = _crypto.Encrypt(payload);
        var wrapper = System.Text.Json.JsonSerializer.Serialize(new { Data = encrypted });
        var content = new StringContent(wrapper, Encoding.UTF8, "application/json");
        var response = await Http.PutAsync(path, content);
        return response.IsSuccessStatusCode;
    }

    public sealed record EncryptedResponse(string? Data);
}

public sealed class CustomAuthStateProvider(AuthService auth) : Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider
{
    public override Task<Microsoft.AspNetCore.Components.Authorization.AuthenticationState> GetAuthenticationStateAsync()
    {
        if (auth.IsAuthenticated)
        {
            var identity = new System.Security.Claims.ClaimsIdentity(new[]
            {
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, auth.ChannelKey ?? "user"),
            }, "jwt");
            var user = new System.Security.Claims.ClaimsPrincipal(identity);
            return Task.FromResult(new Microsoft.AspNetCore.Components.Authorization.AuthenticationState(user));
        }
        return Task.FromResult(new Microsoft.AspNetCore.Components.Authorization.AuthenticationState(new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity())));
    }

    public void NotifyChanged() => NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
}
