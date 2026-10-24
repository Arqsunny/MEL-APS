using Microsoft.Extensions.Options;

namespace MasterEquipmentApi.Aps;

/// <summary>
/// `dotnet run --project backend -- aps-setup [folder-with-bundle-zips]`: publishes one AppBundle + Activity per configured Revit
/// release (Aps:RevitYears), from MelSync.bundle.R{year}.zip files built by tools/build-bundle.ps1. Idempotent.
/// </summary>
public class ApsSetupService
{
    private readonly ApsOptions _o;
    private readonly DesignAutomationClient _da;
    private readonly OssClient _oss;

    public ApsSetupService(IOptions<ApsOptions> o, DesignAutomationClient da, OssClient oss) { _o = o.Value; _da = da; _oss = oss; }

    public async Task<int> RunAsync(string bundleDir, CancellationToken ct)
    {
        var plan = _o.RevitYears.Select(y => (Year: y, Zip: Path.Combine(bundleDir, $"MelSync.bundle.R{y}.zip"))).ToList();
        var missing = plan.Where(p => !File.Exists(p.Zip)).ToList();
        foreach (var m in missing) Console.Error.WriteLine($"Missing {m.Zip} (Revit {m.Year}). Build it with: powershell -File tools/build-bundle.ps1 -Years {m.Year}");
        if (missing.Count == plan.Count) return 2;

        var nickname = await _da.GetNicknameAsync(ct);
        Console.WriteLine($"APS nickname: {nickname}; alias '{_o.Alias}'");

        foreach (var (year, zip) in plan.Where(p => File.Exists(p.Zip)))
        {
            var bundleVersion = await _da.UpsertAppBundleAsync(year, zip, ct);
            Console.WriteLine($"Revit {year}: AppBundle {nickname}.{_o.AppBundleIdFor(year)}+{_o.Alias} -> v{bundleVersion}");
            var activityVersion = await _da.UpsertActivityAsync(nickname, year, ct);
            Console.WriteLine($"Revit {year}: Activity  {nickname}.{_o.ActivityIdFor(year)}+{_o.Alias} -> v{activityVersion} (engine {_o.EngineFor(year)})");
        }

        await _oss.EnsureBucketAsync(ct);
        Console.WriteLine($"OSS bucket '{_o.Bucket}' ready");
        if (missing.Count > 0) Console.WriteLine($"Note: {missing.Count} release(s) were skipped (no zip). Models saved in those releases cannot be synced.");
        Console.WriteLine("Done. Next: connect the APS account (Connect in the app) and link each model (Link model).");
        return 0;
    }
}
