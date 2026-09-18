using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace NovaWallet.Web.Services;

public sealed class ChannelTokenService(
    IHttpClientFactory httpClientFactory, EncryptionService crypto, IConfiguration config)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly EncryptionService _crypto = crypto;
    private readonly string? _appKey = config["Channel:AppKey"];
    private readonly string? _appSecret = config["Channel:AppSecret"];
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string? Token { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }

    public bool IsValid => !string.IsNullOrWhiteSpace(Token) && DateTimeOffset.UtcNow < ExpiresAt;

    public async Task<string?> EnsureTokenAsync()
    {
        if (IsValid) return Token;
        await _lock.WaitAsync();
        try
        {
            if (IsValid) return Token;
            await RefreshTokenAsync();
            return Token;
        }
        finally { _lock.Release(); }
    }

    private async Task RefreshTokenAsync()
    {
        var http = _httpClientFactory.CreateClient("ChannelApi");
        var payload = JsonSerializer.Serialize(new { AppKey = _appKey, AppSecret = _appSecret });
        var encrypted = _crypto.Encrypt(payload);
        var wrapper = JsonSerializer.Serialize(new { Data = encrypted });
        var content = new StringContent(wrapper, Encoding.UTF8, "application/json");
        var response = await http.PostAsync("/api/v1/auth/token", content);
        if (!response.IsSuccessStatusCode) return;
        var raw = await response.Content.ReadAsStringAsync();
        var respWrapper = JsonSerializer.Deserialize<EncryptedResponse>(raw, JsonOpts);
        string json = respWrapper?.Data is not null ? _crypto.Decrypt(respWrapper.Data) : raw;
        var result = JsonSerializer.Deserialize<TokenResponse>(json, JsonOpts);
        if (result is null || string.IsNullOrWhiteSpace(result.Token)) return;
        Token = result.Token;
        ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(result.ExpiresInSeconds - 30, 1));
    }

    public record TokenResponse(string Token, int ExpiresInSeconds);
    public sealed record EncryptedResponse(string? Data);
}

public sealed class AuthService(IHttpClientFactory httpClientFactory, EncryptionService crypto, ChannelTokenService channel)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan RefreshThreshold = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public string? Token { get; private set; }
    public DateTimeOffset TokenExpiry { get; private set; }
    public string? CustomerId { get; private set; }
    public string? WalletId { get; private set; }
    public string? AccountNumber { get; private set; }
    public DateTimeOffset LastActivity { get; private set; } = DateTimeOffset.UtcNow;
    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(Token) && DateTimeOffset.UtcNow < TokenExpiry;
    public bool IsIdle => DateTimeOffset.UtcNow - LastActivity > IdleTimeout;

    private void SetUserToken(string token, int expiresInSeconds, string? customerId, string? walletId, string? accountNumber)
    {
        Token = token;
        TokenExpiry = DateTimeOffset.UtcNow.AddSeconds(Math.Max(expiresInSeconds - 30, 1));
        CustomerId = customerId;
        WalletId = walletId;
        AccountNumber = accountNumber;
    }

    public void Logout()
    {
        Token = null;
        TokenExpiry = DateTimeOffset.MinValue;
        CustomerId = null;
        WalletId = null;
        AccountNumber = null;
    }

    public async Task EnsureUserTokenAsync()
    {
        if (!IsAuthenticated) return;

        if (IsIdle)
        {
            Logout();
            return;
        }

        LastActivity = DateTimeOffset.UtcNow;

        var timeUntilExpiry = TokenExpiry - DateTimeOffset.UtcNow;
        if (timeUntilExpiry <= TimeSpan.Zero)
        {
            Logout();
            return;
        }

        if (timeUntilExpiry <= RefreshThreshold)
            await RefreshUserTokenAsync();
    }

    private async Task RefreshUserTokenAsync()
    {
        await _refreshLock.WaitAsync();
        try
        {
            if (TokenExpiry - DateTimeOffset.UtcNow > RefreshThreshold) return;

            var http = httpClientFactory.CreateClient("ChannelApi");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

            var response = await http.PostAsync("/api/v1/auth/refresh", null);
            if (!response.IsSuccessStatusCode) return;

            var raw = await response.Content.ReadAsStringAsync();
            var wrapper = JsonSerializer.Deserialize<EncryptedResponse>(raw, JsonOpts);
            string json = wrapper?.Data is not null ? crypto.Decrypt(wrapper.Data) : raw;
            var result = JsonSerializer.Deserialize<RefreshResponse>(json, JsonOpts);
            if (result is null || string.IsNullOrWhiteSpace(result.Token)) return;
            Token = result.Token;
            TokenExpiry = DateTimeOffset.UtcNow.AddSeconds(Math.Max(result.ExpiresInSeconds - 30, 1));
        }
        finally { _refreshLock.Release(); }
    }

    public async Task<bool> LoginAsync(string email, string password)
    {
        var channelToken = await channel.EnsureTokenAsync();
        if (string.IsNullOrWhiteSpace(channelToken)) return false;

        var http = httpClientFactory.CreateClient("ChannelApi");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", channelToken);

        var payload = JsonSerializer.Serialize(new { Email = email, Password = password });
        var encrypted = crypto.Encrypt(payload);
        var wrapper = JsonSerializer.Serialize(new { Data = encrypted });
        var content = new StringContent(wrapper, Encoding.UTF8, "application/json");

        var response = await http.PostAsync("/api/v1/auth/login", content);
        if (!response.IsSuccessStatusCode) return false;

        var result = await DeserializeResponseAsync<LoginResponse>(response);
        if (result is null || string.IsNullOrWhiteSpace(result.Token)) return false;
        SetUserToken(result.Token, result.ExpiresInSeconds, result.CustomerId, result.WalletId, result.AccountNumber);
        return true;
    }

    public async Task<(bool Success, string? Error)> RegisterAsync(string email, string password, string firstName, string lastName, string? phoneNumber)
    {
        var channelToken = await channel.EnsureTokenAsync();
        if (string.IsNullOrWhiteSpace(channelToken)) return (false, "Channel authentication failed.");

        var http = httpClientFactory.CreateClient("ChannelApi");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", channelToken);

        var payload = JsonSerializer.Serialize(new { Email = email, Password = password, FirstName = firstName, LastName = lastName, PhoneNumber = phoneNumber });
        var encrypted = crypto.Encrypt(payload);
        var wrapper = JsonSerializer.Serialize(new { Data = encrypted });
        var content = new StringContent(wrapper, Encoding.UTF8, "application/json");

        var response = await http.PostAsync("/api/v1/auth/register", content);
        if (!response.IsSuccessStatusCode)
        {
            var errRaw = await response.Content.ReadAsStringAsync();
            try
            {
                var errWrapper = JsonSerializer.Deserialize<EncryptedResponse>(errRaw, JsonOpts);
                if (errWrapper?.Data is not null)
                    errRaw = crypto.Decrypt(errWrapper.Data);
                var err = JsonSerializer.Deserialize<ErrorResponse>(errRaw, JsonOpts);
                return (false, err?.Message ?? $"Registration failed ({response.StatusCode}).");
            }
            catch { return (false, $"Registration failed ({response.StatusCode})."); }
        }

        var result = await DeserializeResponseAsync<LoginResponse>(response);
        if (result is null || string.IsNullOrWhiteSpace(result.Token)) return (false, "Registration failed.");
        SetUserToken(result.Token, result.ExpiresInSeconds, result.CustomerId, result.WalletId, result.AccountNumber);
        return (true, null);
    }

    private async Task<T?> DeserializeResponseAsync<T>(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        var wrapper = JsonSerializer.Deserialize<EncryptedResponse>(raw, JsonOpts);
        string json = wrapper?.Data is not null ? crypto.Decrypt(wrapper.Data) : raw;
        return JsonSerializer.Deserialize<T>(json, JsonOpts);
    }

    public record LoginResponse(string Token, int ExpiresInSeconds, string? CustomerId, string? WalletId, string? AccountNumber);
    public record RefreshResponse(string Token, int ExpiresInSeconds);
    public record ErrorResponse(string Code, string Message);
    public sealed record EncryptedResponse(string? Data);
}

public sealed class ApiClient(HttpClient http, EncryptionService crypto, ChannelTokenService channel, AuthService auth)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions CamelOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public HttpClient Http { get; } = http;
    private readonly EncryptionService _crypto = crypto;
    private readonly ChannelTokenService _channel = channel;
    private readonly AuthService _auth = auth;

    private async Task EnsureAuthHeaderAsync()
    {
        await _channel.EnsureTokenAsync();
        await _auth.EnsureUserTokenAsync();
        var token = _auth.Token;
        if (!string.IsNullOrWhiteSpace(token))
            Http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }

    public async Task<T?> GetAsync<T>(string path)
    {
        await EnsureAuthHeaderAsync();
        var response = await Http.GetAsync(path);
        if (!response.IsSuccessStatusCode) return default;
        return await DecryptResponseAsync<T>(response);
    }

    public async Task<T?> PostAsync<T>(string path, object body, string? idempotencyKey = null)
    {
        await EnsureAuthHeaderAsync();
        var payload = JsonSerializer.Serialize(body, CamelOpts);
        var encrypted = _crypto.Encrypt(payload);
        var wrapper = JsonSerializer.Serialize(new { Data = encrypted }, CamelOpts);
        var content = new StringContent(wrapper, Encoding.UTF8, "application/json");
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
            content.Headers.Add("Idempotency-Key", idempotencyKey);
        var response = await Http.PostAsync(path, content);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await ReadErrorResponseAsync(response);
            throw new HttpRequestException($"API {(int)response.StatusCode}: {errorBody}");
        }
        return await DecryptResponseAsync<T>(response);
    }

    public async Task<bool> PostNoResponseAsync(string path, object? body = null)
    {
        await EnsureAuthHeaderAsync();
        if (body is null)
        {
            var resp = await Http.PostAsync(path, null);
            return resp.IsSuccessStatusCode;
        }
        var payload = JsonSerializer.Serialize(body);
        var encrypted = _crypto.Encrypt(payload);
        var wrapper = JsonSerializer.Serialize(new { Data = encrypted });
        var content = new StringContent(wrapper, Encoding.UTF8, "application/json");
        var response = await Http.PostAsync(path, content);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> PutNoResponseAsync(string path, object? body = null)
    {
        await EnsureAuthHeaderAsync();
        if (body is null)
        {
            var resp = await Http.PutAsync(path, null);
            return resp.IsSuccessStatusCode;
        }
        var payload = JsonSerializer.Serialize(body);
        var encrypted = _crypto.Encrypt(payload);
        var wrapper = JsonSerializer.Serialize(new { Data = encrypted });
        var content = new StringContent(wrapper, Encoding.UTF8, "application/json");
        var response = await Http.PutAsync(path, content);
        return response.IsSuccessStatusCode;
    }

    private async Task<T?> DecryptResponseAsync<T>(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        var wrapper = JsonSerializer.Deserialize<EncryptedResponse>(raw, JsonOpts);
        if (wrapper?.Data is null)
            return JsonSerializer.Deserialize<T>(raw, JsonOpts);
        var decrypted = _crypto.Decrypt(wrapper.Data);
        return JsonSerializer.Deserialize<T>(decrypted, JsonOpts);
    }

    private async Task<string> ReadErrorResponseAsync(HttpResponseMessage response)
    {
        try
        {
            var raw = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(raw)) return $"HTTP {(int)response.StatusCode}";
            var wrapper = JsonSerializer.Deserialize<EncryptedResponse>(raw, JsonOpts);
            if (wrapper?.Data is not null)
                return _crypto.Decrypt(wrapper.Data);
            return raw;
        }
        catch
        {
            return $"HTTP {(int)response.StatusCode}";
        }
    }

    public sealed record EncryptedResponse(string? Data);
}

public sealed class CustomAuthStateProvider(AuthService auth) : Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider
{
    public override Task<Microsoft.AspNetCore.Components.Authorization.AuthenticationState> GetAuthenticationStateAsync()
    {
        if (auth.IsAuthenticated)
        {
            var identity = new System.Security.Claims.ClaimsIdentity([
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, auth.CustomerId ?? "user"),
            ], "jwt");
            var user = new System.Security.Claims.ClaimsPrincipal(identity);
            return Task.FromResult(new Microsoft.AspNetCore.Components.Authorization.AuthenticationState(user));
        }
        return Task.FromResult(new Microsoft.AspNetCore.Components.Authorization.AuthenticationState(new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity())));
    }

    public void NotifyChanged() => NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
}
