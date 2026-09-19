using NovaWallet.Web.Components;
using NovaWallet.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddAuthorization();
builder.Services.AddAuthorizationCore();
builder.Services.Configure<Microsoft.AspNetCore.SignalR.HubOptions>(options =>
{
    options.KeepAliveInterval = TimeSpan.FromSeconds(10);
    options.ClientTimeoutInterval = TimeSpan.FromMinutes(1);
});
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddSingleton<EncryptionService>();
builder.Services.AddSingleton<ChannelTokenService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<CustomAuthStateProvider>();
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>(sp => sp.GetRequiredService<CustomAuthStateProvider>());

builder.Services.AddHttpClient("ChannelApi", c => c.BaseAddress = new Uri(builder.Configuration["ApiBaseUrl"] ?? "http://localhost:51079"));
builder.Services.AddHttpClient<ApiClient>(c => c.BaseAddress = new Uri(builder.Configuration["ApiBaseUrl"] ?? "http://localhost:51079"));

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
