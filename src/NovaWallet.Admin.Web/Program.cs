using Microsoft.EntityFrameworkCore;
using NovaWallet.Admin.Web.Components;
using NovaWallet.Admin.Web.Services;
using NovaWallet.Application;
using NovaWallet.Application.Repositories;
using NovaWallet.Infrastructure;
using NovaWallet.Infrastructure.Repositories;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddAuthorization();
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
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IPaymentRail, MockNipPaymentRail>();

builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<CustomAuthStateProvider>();
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>(sp => sp.GetRequiredService<CustomAuthStateProvider>());

builder.Services.AddScoped<AdminDatabaseService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

using (var scope = app.Services.CreateScope())
{
    await DbInitializer.InitializeAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), CancellationToken.None);
}

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
