using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using MasterEquipmentApi.Aps;
using MasterEquipmentApi.Auth;
using MasterEquipmentApi.Data;
using MasterEquipmentApi.Sync;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

// Connection string: ConnectionStrings__Mel env var in production; appsettings.Development.json for local docker-compose.
builder.Services.AddDbContext<MelDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Mel")
        ?? throw new InvalidOperationException("ConnectionStrings:Mel is not configured. For local development run tools/init-dev-secrets.ps1 (stores it in user secrets); in production set ConnectionStrings__Mel.")));

builder.AddMelAuth();

// ---- APS / Design Automation -------------------------------------------------------------------------------------
var aps = builder.Configuration.GetSection(ApsOptions.Section).Get<ApsOptions>() ?? new ApsOptions();
if (aps.IsLive)
{
    // Fail at startup, not on the first sync, if the deployment cannot actually talk to APS.
    foreach (var (name, value) in new[] { ("ClientId", aps.ClientId), ("ClientSecret", aps.ClientSecret), ("Bucket", aps.Bucket) })
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Aps:{name} is required when Aps:Mode is Live.");
}
else if (!builder.Environment.IsDevelopment())
{
    // The mock reports success for work it never did. It must not be reachable in a real deployment.
    throw new InvalidOperationException("Aps:Mode=Mock is only allowed in the Development environment. Set Aps:Mode=Live.");
}

builder.Services.Configure<ApsOptions>(builder.Configuration.GetSection(ApsOptions.Section));
builder.Services.AddHttpClient();
builder.Services.AddDataProtection();   // protects the stored APS refresh token; persist the key ring in production (docs/APS.md)
builder.Services.AddSingleton<ApsAuth>();
builder.Services.AddSingleton<OssClient>();
builder.Services.AddSingleton<DataManagementClient>();
builder.Services.AddSingleton<ViewerSessionService>();
builder.Services.AddSingleton<DesignAutomationClient>();
builder.Services.AddSingleton<ApsSetupService>();
if (aps.IsLive) builder.Services.AddSingleton<ISyncRunner, DesignAutomationSyncRunner>();
else builder.Services.AddSingleton<ISyncRunner, MockSyncRunner>();
builder.Services.AddScoped<SyncResultApplier>();
builder.Services.AddHostedService<SyncWorker>();

var app = builder.Build();

// `dotnet run --project backend -- aps-setup [bundle.zip]`: publish the AppBundle + Activity, then exit (no web server, no database).
if (args.Contains("aps-setup"))
{
    if (!aps.IsLive) { Console.Error.WriteLine("Set Aps:Mode=Live (and credentials) to run aps-setup."); return 2; }
    var dir = args.SkipWhile(a => a != "aps-setup").Skip(1).FirstOrDefault() ?? Path.Combine(app.Environment.ContentRootPath, "..", "artifacts");
    return await app.Services.GetRequiredService<ApsSetupService>().RunAsync(Path.GetFullPath(dir), CancellationToken.None);
}

// Database:AutoMigrate applies pending migrations on startup (convenient locally; run migrations in the deploy pipeline instead for production).
if (app.Configuration.GetValue<bool>("Database:AutoMigrate"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<MelDbContext>();
    await db.Database.MigrateAsync();
    if (app.Configuration.GetValue<bool>("Database:SeedDemoData")) await DbSeeder.SeedAsync(db);
}

// Static files first: the SPA shell and its assets are public; everything under /api requires a signed-in user
// (authorization fallback policy). The shell contains no data.
// In development, run `npm run dev` in frontend/ (proxies /api here) instead of serving frontend/dist.
var distPath = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "frontend", "dist"));
var spa = Directory.Exists(distPath) ? new PhysicalFileProvider(distPath) : null;
if (spa != null)
{
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = spa });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = spa });
}

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
if (spa != null)
    app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = spa }).AllowAnonymous();

app.Run();
return 0;
