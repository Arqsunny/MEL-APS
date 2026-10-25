namespace MasterEquipmentApi.Aps;

public class ApsOptions
{
    public const string Section = "Aps";

    /// <summary>"Mock" (no APS calls; local development and demos only) or "Live".</summary>
    public string Mode { get; set; } = "Mock";
    public bool IsLive => Mode.Equals("Live", StringComparison.OrdinalIgnoreCase);

    public string ClientId { get; set; } = string.Empty;
    /// <summary>Secret: environment variable Aps__ClientSecret or user-secrets; never commit it.</summary>
    public string ClientSecret { get; set; } = string.Empty;
    /// <summary>Registered 3-legged callback, e.g. https://mel.example.com/api/aps/callback</summary>
    public string CallbackUrl { get; set; } = string.Empty;

    // Endpoints are configurable so tests can point at a fake APS and EMEA deployments can switch region.
    public string AuthBaseUrl { get; set; } = "https://developer.api.autodesk.com";
    public string DesignAutomationBaseUrl { get; set; } = "https://developer.api.autodesk.com/da/us-east/v3";
    public string OssBaseUrl { get; set; } = "https://developer.api.autodesk.com/oss/v2";
    /// <summary>Data Management / Projects API host (hubs, projects, folders, items). Pagination links must stay on this host.</summary>
    public string DataManagementBaseUrl { get; set; } = "https://developer.api.autodesk.com";
    /// <summary>OIDC user-info endpoint used only to show which account is connected. (The old /userprofile/v1/users/@me now returns 410.)</summary>
    /// <summary>Time budget for one model scan; when it runs out the scan returns partial results (marked truncated).</summary>
    public int BrowseBudgetSeconds { get; set; } = 90;
    public string UserInfoUrl { get; set; } = "https://api.userprofile.autodesk.com/userinfo";

    /// <summary>
    /// Revit releases we run engines for. A model must be opened by the engine of the SAME release: an older engine cannot open it,
    /// and a newer one would upgrade the shared cloud model for everyone. Each release has its own bundle and activity.
    /// </summary>
    public int[] RevitYears { get; set; } = { 2025, 2026 };
    /// <summary>Base ids; the release year is appended (MelSyncApp2025, MelSyncActivity2026, ...).</summary>
    public string AppBundleId { get; set; } = "MelSyncApp";
    public string ActivityId { get; set; } = "MelSyncActivity";
    public string Alias { get; set; } = "prod";

    public string EngineFor(int year) => $"Autodesk.Revit+{year}";
    public string AppBundleIdFor(int year) => $"{AppBundleId}{year}";
    public string ActivityIdFor(int year) => $"{ActivityId}{year}";
    public bool Supports(int? year) => year is int y && RevitYears.Contains(y);

    /// <summary>OSS bucket for work item inputs/results (globally unique, lowercase, 3-128 chars).</summary>
    public string Bucket { get; set; } = string.Empty;

    /// <summary>How often the worker looks at queued/running jobs.</summary>
    public int PollSeconds { get; set; } = 5;
    /// <summary>A job that is not finished after this long is failed (items return to Approved; writes are idempotent).</summary>
    public int JobTimeoutMinutes { get; set; } = 75;

    // Scopes. Revit cloud models need a user context (3-legged) with code:all.
    public const string ScopeDesignAutomation = "code:all";
    public const string ScopeOss = "data:read data:write data:create bucket:create bucket:read";
    public const string ScopeUser = "code:all data:read data:write data:create viewables:read user-profile:read";
}

public class ApsException : Exception
{
    public int? Status { get; }
    public ApsException(string message, int? status = null) : base(message) { Status = status; }
}

/// <summary>Raised when the service account must be (re)connected by a BIM Lead.</summary>
public class ApsNotConnectedException : ApsException
{
    public ApsNotConnectedException(string message) : base(message) { }
}
