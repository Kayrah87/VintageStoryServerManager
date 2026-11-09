using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MudBlazor.Services;
using VintageStoryServerManager.Core.Backups;
using VintageStoryServerManager.Core.Configuration;
using VintageStoryServerManager.Core.Security;
using VintageStoryServerManager.Infrastructure;
using VintageStoryServerManager.Infrastructure.Logs;
using VintageStoryServerManager.Web.Components;
using VintageStoryServerManager.Web.Components.Account;
using VintageStoryServerManager.Web.Data;

var builder = WebApplication.CreateBuilder(args);

// Everything persistent lives under the mounted data volume (default /data).
builder.Services.AddInfrastructure(builder.Configuration);
var dataPaths = builder.Configuration.GetSection(DataPathOptions.SectionName).Get<DataPathOptions>() ?? new DataPathOptions();
dataPaths.EnsureDirectories();

// Persist auth cookie keys in the volume so logins survive container restarts and moves.
builder.Services.AddDataProtection()
    .SetApplicationName("VintageStoryServerManager")
    .PersistKeysToFileSystem(new DirectoryInfo(dataPaths.DataProtectionKeysDirectory));

// TLS is expected to be terminated by a reverse proxy in front of the container.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services.AddHealthChecks();

// Give the game server time to save and stop cleanly when the container shuts down.
var serverOptions = builder.Configuration.GetSection(ServerOptions.SectionName).Get<ServerOptions>() ?? new ServerOptions();
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(serverOptions.StopTimeoutSeconds + 15));

builder.Services.AddMudServices();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? $"DataSource={dataPaths.DatabasePath};Cache=Shared";
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlite(connectionString));
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        // Self-hosted with no mail server: accounts are usernames managed by an admin.
        options.SignIn.RequireConfirmedAccount = false;
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();

builder.Services.Configure<IdentityOptions>(options =>
{
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
});

// Accounts are created by admins only; the first admin comes from the Admin__* settings.
builder.Services.Configure<AdminSeedOptions>(builder.Configuration.GetSection(AdminSeedOptions.SectionName));
builder.Services.AddScoped<IdentitySeeder>();
builder.Services.AddSingleton<UserAdministrationService>();

var app = builder.Build();

// Fail fast on a bad data-path configuration, then bring the database schema up to date.
_ = app.Services.GetRequiredService<IOptions<DataPathOptions>>().Value;
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.Migrate();
    await scope.ServiceProvider.GetRequiredService<IdentitySeeder>().SeedAsync();
}

app.UseForwardedHeaders();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

// Explicit (rather than implicit, at the start of the pipeline) so that the re-executed
// /not-found request above also passes through authorisation.
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/healthz");

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Add additional endpoints required by the Identity /Account Razor components.
app.MapAdditionalIdentityEndpoints();

app.MapGet("/api/backups/{fileName}", (string fileName, IBackupService backups) =>
        backups.GetPath(fileName) is { } path
            ? Results.File(path, "application/zip", fileName, enableRangeProcessing: true)
            : Results.NotFound())
    .RequireAuthorization(policy => policy.RequireRole(Roles.Admin));

app.MapGet("/api/logs/{**name}", (string name, LogService logs) =>
        logs.GetPath(name) is { } path
            ? Results.Stream(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete),
                "text/plain", Path.GetFileName(path))
            : Results.NotFound())
    .RequireAuthorization(policy => policy.RequireRole(Roles.Admin, Roles.Operator));

await app.RunAsync();
