using System.Text.Json;
using Microsoft.Extensions.Options;
using MelSync;
using MasterEquipmentApi.Aps;
using MasterEquipmentApi.Models;

namespace MasterEquipmentApi.Sync;

/// <summary>Runs the MelSync activity on APS Design Automation for Revit against a Revit cloud model.</summary>
public class DesignAutomationSyncRunner : ISyncRunner
{
    private readonly ApsOptions _o;
    private readonly ApsAuth _auth;
    private readonly OssClient _oss;
    private readonly DesignAutomationClient _da;
    private readonly ILogger<DesignAutomationSyncRunner> _log;

    public DesignAutomationSyncRunner(IOptions<ApsOptions> o, ApsAuth auth, OssClient oss, DesignAutomationClient da, ILogger<DesignAutomationSyncRunner> log)
    { _o = o.Value; _auth = auth; _oss = oss; _da = da; _log = log; }

    public string Name => "Live";

    private record State(string InputKey, string ResultKey, string UploadKey);

    public async Task<string?> PreflightAsync(RevitModel? model, CancellationToken ct)
    {
        if (model == null || !model.IsLinked)
            return "This model is not linked to a Revit cloud model yet. A BIM Lead must set its region, project GUID and model GUID (PUT /api/models/{id}/cloud-link).";
        if (await _auth.GetConnectionAsync(ct) == null)
            return "No APS account is connected. A BIM Lead must connect one (POST /api/aps/connect).";
        // Same-release rule: only the engine of the model's own Revit release may open it (see MelSync.EngineGuard).
        if (model.RevitVersion is null)
            return "The Revit version of this model is not recorded. Use Link model to link it again so the right engine is chosen (the wrong release could upgrade the shared model).";
        if (!_o.Supports(model.RevitVersion))
            return $"This model was saved in Revit {model.RevitVersion}; engines are configured for Revit {string.Join(", ", _o.RevitYears)}. A different release cannot open it safely (an older one cannot, a newer one would upgrade the shared model).";
        return null;
    }

    public async Task<SubmitOutcome> SubmitAsync(SyncJob job, SyncPayload payload, CancellationToken ct)
    {
        var inputKey = $"inputs/{job.Id}.json";
        var resultKey = $"results/{job.Id}.json";

        await _oss.EnsureBucketAsync(ct);
        var inputUrl = await _oss.PutJsonAsync(inputKey, SyncJson.Serialize(payload), ct);
        var resultUpload = await _oss.GetSignedUploadAsync(resultKey, ct);

        // Revit cloud models need a user context: the 3-legged token travels as a special work item argument.
        var userToken = await _auth.GetServiceUserTokenAsync(ct);
        var nickname = await _da.GetNicknameAsync(ct);

        // The engine must be the same release as the model; the bundle re-checks this before opening anything.
        if (payload.RevitVersion is not int year || !_o.Supports(year))
            throw new ApsException($"No engine is configured for Revit {payload.RevitVersion?.ToString() ?? "(unknown)"}.");

        var workItemId = await _da.SubmitWorkItemAsync(_da.FullId(nickname, _o.ActivityIdFor(year), _o.Alias), new Dictionary<string, object>
        {
            ["inputJson"] = new { url = inputUrl },
            ["result"] = new { verb = "put", url = resultUpload.Url },
            ["adsk3LeggedToken"] = userToken
        }, ct);

        return new SubmitOutcome(workItemId, JsonSerializer.Serialize(new State(inputKey, resultKey, resultUpload.UploadKey)));
    }

    public async Task<RunnerPoll> PollAsync(SyncJob job, CancellationToken ct)
    {
        var wi = await _da.GetWorkItemAsync(job.WorkItemId!, ct);
        if (wi.IsRunning) return new RunnerPoll(RunnerState.Running, wi.Status, ReportUrl: wi.ReportUrl);

        if (!wi.IsSuccess)
            return new RunnerPoll(RunnerState.Failed, wi.Status, $"Design Automation work item ended with status '{wi.Status}'.", wi.ReportUrl);

        var state = JsonSerializer.Deserialize<State>(job.RunnerState ?? "{}");
        if (state == null || string.IsNullOrEmpty(state.ResultKey))
            return new RunnerPoll(RunnerState.Failed, wi.Status, "Job has no stored result location.", wi.ReportUrl);

        // The activity wrote the result straight to a signed S3 URL; finalize so OSS registers the object.
        try { await _oss.CompleteUploadAsync(state.ResultKey, state.UploadKey, ct); }
        catch (ApsException ex) { _log.LogWarning("Finalizing result upload for job {Job} returned: {Msg}", job.Id, ex.Message); }

        var text = await _oss.TryGetTextAsync(state.ResultKey, ct);
        if (text == null)
            return new RunnerPoll(RunnerState.Failed, wi.Status, "The work item succeeded but produced no result file.", wi.ReportUrl);

        try
        {
            return new RunnerPoll(RunnerState.Done, wi.Status, ReportUrl: wi.ReportUrl, Result: SyncJson.Deserialize<SyncResult>(text));
        }
        catch (JsonException ex)
        {
            return new RunnerPoll(RunnerState.Failed, wi.Status, $"The result file is not valid: {ex.Message}", wi.ReportUrl);
        }
    }
}
