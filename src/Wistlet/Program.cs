using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Wistlet.Data;
using Wistlet.Infrastructure;
using Wistlet.Models;
using Wistlet.Services;

var builder = WebApplication.CreateBuilder(args);

var configuredDataPath = builder.Configuration["Wistlet:DataPath"];
var dataPath = Path.GetFullPath(string.IsNullOrWhiteSpace(configuredDataPath) ? Path.Combine(builder.Environment.ContentRootPath, "data") : configuredDataPath);
builder.Configuration["Wistlet:DataPath"] = dataPath;
Directory.CreateDirectory(dataPath);

builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? $"Data Source={Path.Combine(dataPath, "wistlet.db")}"));

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataPath, "keys")))
    .SetApplicationName("Wistlet");

builder.Services.AddScoped<CookieEvents>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "wistlet.auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.LoginPath = "/account/login";
        o.LogoutPath = "/account/logout";
        o.AccessDeniedPath = "/account/denied";
        o.SlidingExpiration = true;
        o.EventsType = typeof(CookieEvents);
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.Staff, p => p.RequireRole(nameof(UserRole.Moderator), nameof(UserRole.Admin)))
    .AddPolicy(Policies.Admin, p => p.RequireRole(nameof(UserRole.Admin)));

builder.Services.AddRazorPages(o =>
{
    o.Conventions.ConfigureFilter(new Microsoft.AspNetCore.Mvc.ServiceFilterAttribute(typeof(GuestAccessFilter)));
    o.Conventions.AuthorizeFolder("/Admin", Policies.Staff);
    o.Conventions.AuthorizePage("/Admin/Settings", Policies.Admin);
    o.Conventions.AuthorizePage("/Admin/Audit", Policies.Admin);
}).AddMvcOptions(o => o.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true);

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Only state-changing requests count against the limits.
    o.AddPolicy("auth", ctx => HttpMethods.IsGet(ctx.Request.Method)
        ? RateLimitPartition.GetNoLimiter("get")
        : RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "?",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("write", ctx => HttpMethods.IsGet(ctx.Request.Method)
        ? RateLimitPartition.GetNoLimiter("get")
        : RateLimitPartition.GetFixedWindowLimiter(ctx.User.GetUserId()?.ToString() ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "?",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) }));
});

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    if (builder.Configuration.GetValue("Wistlet:TrustAllProxies", false))
    {
        // The container is only reachable through the reverse proxy, which sits
        // outside Docker's network, so we accept forwarded headers from anywhere.
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
    }
});

builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = 55 * 1024 * 1024);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 55 * 1024 * 1024);

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<SiteSettingsService>();
builder.Services.AddSingleton<UploadService>();
builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<PostService>();
builder.Services.AddScoped<MessagingService>();
builder.Services.AddScoped<ViewerContext>();
builder.Services.AddScoped<DbInitializer>();
builder.Services.AddScoped<GuestAccessFilter>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<DbInitializer>().InitializeAsync();
}
await app.Services.GetRequiredService<SiteSettingsService>().LoadAsync();

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
}
app.UseStatusCodePagesWithReExecute("/error", "?code={0}");
app.UseSecurityHeaders();

var uploads = app.Services.GetRequiredService<UploadService>();
Directory.CreateDirectory(uploads.Root);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(uploads.Root),
    RequestPath = UploadService.UrlPrefix,
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "public, max-age=604800, immutable",
});

app.UseAuthentication();
app.UseMaintenanceMode();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthorization();

app.MapGet("/healthz", () => Results.Text("ok"));
app.MapGet("/avatar/{name}.svg", (string name, HttpContext ctx) =>
{
    ctx.Response.Headers.CacheControl = "public, max-age=86400";
    return Results.Content(DefaultAvatar.Render(name), "image/svg+xml");
});

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();

app.Run();

public partial class Program;
