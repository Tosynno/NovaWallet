using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using NovaWallet.Admin.Web.Components;
using NovaWallet.Admin.Web.Services;
using NovaWallet.Application;
using NovaWallet.Application.Repositories;
using NovaWallet.Infrastructure;
using NovaWallet.Infrastructure.Repositories;
using System.Security.Claims;
using IClock = NovaWallet.Application.IClock;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAuthentication("AdminCookie").AddCookie("AdminCookie", o =>
{
    o.LoginPath = "/login";
    o.AccessDeniedPath = "/login";
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
    o.Cookie.Name = "NovaWallet.Admin";
    o.Cookie.HttpOnly = true;
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddAuthorizationCore();
builder.Services.AddCascadingAuthenticationState();
builder.Services.Configure<Microsoft.AspNetCore.SignalR.HubOptions>(options =>
{
    options.KeepAliveInterval = TimeSpan.FromSeconds(10);
    options.ClientTimeoutInterval = TimeSpan.FromMinutes(1);
});

builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlServer(builder.Configuration.GetConnectionString("LedgerDb")));
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped<IWalletRepository, WalletRepository>();
builder.Services.AddScoped<ITransferRepository, TransferRepository>();
builder.Services.AddScoped<ILedgerEntryRepository, LedgerEntryRepository>();
builder.Services.AddScoped<IAuditLogRepository, AuditLogRepository>();
builder.Services.AddScoped<IOutboxRepository, OutboxRepository>();
builder.Services.AddScoped<ISettlementJobRepository, SettlementJobRepository>();
builder.Services.AddScoped<IExternalAccountRepository, ExternalAccountRepository>();
builder.Services.AddScoped<IReconciliationRepository, ReconciliationRepository>();
builder.Services.AddScoped<IDailyOutboundCounterRepository, DailyOutboundCounterRepository>();
builder.Services.AddScoped<IReconciliationService, ReconciliationService>();
builder.Services.AddSingleton<IClock, NovaWallet.Application.SystemClock>();
builder.Services.AddSingleton<IPaymentRail, MockNipPaymentRail>();

builder.Services.AddScoped<AuthService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CustomAuthStateProvider>();
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>(sp => sp.GetRequiredService<CustomAuthStateProvider>());

builder.Services.AddScoped<AdminDatabaseService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

using (var scope = app.Services.CreateScope())
{
    await DbInitializer.InitializeAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), CancellationToken.None);
}

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapPost("/auth/login", async (HttpContext http, AuthService auth) =>
{
    var form = await http.Request.ReadFormAsync();
    var username = form["username"].ToString();
    var password = form["password"].ToString();
    var returnUrl = form["returnUrl"].ToString();

    if (auth.Validate(username, password))
    {
        auth.SetAuthenticated(username);
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, username),
            new Claim(ClaimTypes.Role, "admin"),
        };
        var identity = new ClaimsIdentity(claims, "AdminCookie");
        var principal = new ClaimsPrincipal(identity);
        await http.SignInAsync("AdminCookie", principal);
        http.Response.Redirect(string.IsNullOrWhiteSpace(returnUrl) ? "/dashboard" : returnUrl);
    }
    else
    {
        http.Response.Redirect("/login?error=1");
    }
}).AllowAnonymous();

app.MapPost("/auth/logout", async (HttpContext http, AuthService auth) =>
{
    auth.Logout();
    await http.SignOutAsync("AdminCookie");
    http.Response.Redirect("/login");
}).AllowAnonymous();

app.Run();
