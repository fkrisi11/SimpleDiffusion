using MudBlazor.Services;
using SimpleDiffusion.Components;
using SimpleDiffusion.Infrastructure;
using System.Diagnostics;
using System.Runtime.InteropServices;

// Create the data/ folder (and migrate any legacy root-level files) before anything reads it.
SimpleDiffusion.Infrastructure.AppPaths.EnsureCreated();

var builder = WebApplication.CreateBuilder(args);
// User settings live in data/settings.json so a backup of the data folder captures them.
builder.Configuration.AddJsonFile(SimpleDiffusion.Infrastructure.AppPaths.SettingsFile, optional: true, reloadOnChange: true);

// libvips keeps an in-memory cache of recent operations (default ~100MB / 1000 ops). Our gallery
// downscales are one-off and already cached on disk, so that cache just retains memory after a
// conversion without ever being reused. Turn it off so memory is released promptly.
NetVips.Cache.MaxMem = 0;
NetVips.Cache.Max = 0;
NetVips.Cache.MaxFiles = 0;

// Result-store size cap, overridable from settings.json (MaxResultCacheMB). Below 256 MB is ignored
// as a likely mistake.
if (int.TryParse(builder.Configuration["MaxResultCacheMB"], out var cacheMb) && cacheMb >= 256)
    SimpleDiffusion.Infrastructure.GalleryServer.MaxResultBytes = cacheMb * 1024L * 1024L;

// Tidy the derived-tier cache at boot: drop the ephemeral in-memory-image tiers (dead after a
// restart) and bound the rest to its size cap.
SimpleDiffusion.Infrastructure.GalleryServer.CleanTierCacheOnStartup();
// Last session's on-disk results are gone — clear them too.
SimpleDiffusion.Infrastructure.GalleryServer.ClearResultsOnStartup();

builder.WebHost.ConfigureKestrel(serverOptions =>
{
    // HTTP on all interfaces (localhost + LAN). Port is configurable via settings.json ("ServerPort");
    // an absent/invalid/out-of-range value falls back to the default 5248.
    var port = int.TryParse(builder.Configuration["ServerPort"], out var p) && p is > 0 and <= 65535
        ? p : 5248;
    serverOptions.ListenAnyIP(port);
    // HTTPS disabled for LAN-only use. Re-enable the line below if you ever need TLS.
    // serverOptions.ListenAnyIP(7166, listenOptions => listenOptions.UseHttps());
});

builder.Services.AddScoped(sp =>
{
    var client = new HttpClient(new HttpClientHandler
    {
        // DANGEROUS: Only do this in Development!
        ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
    })
    {
        // Well above HttpClient's 100s default so long-running SD operations (generation, upscale,
        // hires) proxied through this client aren't dropped mid-flight.
        Timeout = TimeSpan.FromHours(1)
    };

    var nav = sp.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
    client.BaseAddress = new Uri(nav.BaseUri);
    return client;
});

// Add MudBlazor services
builder.Services.AddMudServices();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Detailed error logging
builder.Services.AddServerSideBlazor()
.AddCircuitOptions(options =>
{
    options.DetailedErrors = true;
})
// Inpaint-sketch and outpaint return a full composited image from JS → .NET over the circuit. The
// default SignalR receive cap is 32 KB, which silently fails those interop calls (a photographic PNG
// is multiple MB). Raise it so large interop returns get through.
.AddHubOptions(options =>
{
    options.MaximumReceiveMessageSize = 512 * 1024 * 1024; // 512 MB — large composited/outpaint images
});

builder.Services.AddSingleton<TagService>();

// Per-device UI preferences (Civitai API key + NSFW handling), stored in the browser.
builder.Services.AddScoped<SimpleDiffusion.Components.Services.UiPreferences>();

// Cross-tab navigation requests (e.g. "show this model in the LoRA browser").
builder.Services.AddScoped<SimpleDiffusion.Components.Services.CrossTab>();

// ControlNet preprocessor/model discovery (caches the type map per circuit).
builder.Services.AddScoped<SimpleDiffusion.Components.Services.ControlNetService>();

// Saved tag groups, shared by the txt2img + img2img Prompt Tools panels.
builder.Services.AddScoped<SimpleDiffusion.Components.Services.TagGroupService>();

// Per-LoRA favourites + usage counts for the LoRA browser.
builder.Services.AddScoped<SimpleDiffusion.Components.Services.LoraStatsService>();

// Wildcard files for dynamic prompts (__name__ -> wildcards/name.txt).
builder.Services.AddSingleton<SimpleDiffusion.Components.Services.WildcardService>();

// Civitai browser: API client + download manager are app-wide singletons so
// downloads survive navigation and are shared across connected clients.
builder.Services.AddSingleton<SimpleDiffusion.Components.Civitai.CivitaiService>();
builder.Services.AddSingleton<SimpleDiffusion.Components.Civitai.CivitaiDownloadManager>();

// Per-connection working state so the home-page fields (prompt, settings, preset, active tab)
// survive navigating to the Settings page and back.
builder.Services.AddScoped<SimpleDiffusion.Components.Services.WorkspaceState>();

// Per-device generation history (localStorage-backed), so a refresh/crash doesn't lose what was made.
builder.Services.AddScoped<SimpleDiffusion.Components.Services.HistoryService>();

// One shared Stable Diffusion server process for the whole app (singleton = shared across all
// connected users, not one per connection).
builder.Services.AddSingleton<SimpleDiffusion.Components.Services.SdProcessManager>();

Console.Title = "Simple Diffusion";

var app = builder.Build();

app.MapLoraPreview();
app.MapLoraDetails();
app.MapCivitaiImageProxy();
app.MapCivitaiOpenFolder();
app.MapFolderBrowser();
app.MapGallery();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

// We don't need HTTPS for LAN — serve plain HTTP so browsers don't get
// redirected to the (self-signed) HTTPS port and complain about the cert.
// app.UseHttpsRedirection();


app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
