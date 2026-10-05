var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddAdminConfiguration().AddLocalBlazor().AddAdminApiClient("OSSM:ControlClient").AddAdminListener();
builder.WebHost.UseStaticWebAssets();

await using var app = builder.Build();
app.UseLocalAccessPolicy(app.Services.GetRequiredService<IOptions<AdminOptions>>().Value.ListenPort);
app.UseStaticFiles();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapDefaultEndpoints();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode(options => options.ContentSecurityFrameAncestorsPolicy = "'none'");
await app.RunAsync();
