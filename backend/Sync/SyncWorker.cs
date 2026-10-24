using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MelSync;
using MasterEquipmentApi.Aps;
using MasterEquipmentApi.Data;
using MasterEquipmentApi.Models;

namespace MasterEquipmentApi.Sync;

/// <summary>Submits queued jobs, polls running ones, and finalizes them. One failing job never blocks the others.</summary>
public class SyncWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ApsOptions _o;
    private readonly ILogger<SyncWorker> _log;

    public SyncWorker(IServiceScopeFactory scopes, IOptions<ApsOptions> o, ILogger<SyncWorker> log) { _scopes = scopes; _o = o.Value; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, _o.PollSeconds)));
        do
        {
            try { await TickAsync(stop); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogError(ex, "Sync worker tick failed."); }
        }
        while (await WaitAsync(timer, stop));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer t, CancellationToken ct)
    {
        try { return await t.WaitForNextTickAsync(ct); } catch (OperationCanceledException) { return false; }
    }

    public async Task TickAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MelDbContext>();
        var runner = scope.ServiceProvider.GetRequiredService<ISyncRunner>();
        var applier = scope.ServiceProvider.GetRequiredService<SyncResultApplier>();

        var jobs = await db.SyncJobs
            .Where(j => j.Status == SyncJobStatus.Queued || j.Status == SyncJobStatus.Submitted)
            .OrderBy(j => j.CreatedAt).Take(25).ToListAsync(ct);

        foreach (var job in jobs)
        {
            try { await AdvanceAsync(db, runner, applier, job, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Advancing sync job {Job} failed; it will be retried on the next tick.", job.Id);
                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task AdvanceAsync(MelDbContext db, ISyncRunner runner, SyncResultApplier applier, SyncJob job, CancellationToken ct)
    {
        if (job.Status == SyncJobStatus.Queued)
        {
            try
            {
                var payload = SyncJson.Deserialize<SyncPayload>(job.PayloadJson);
                var outcome = await runner.SubmitAsync(job, payload, ct);
                job.WorkItemId = outcome.WorkItemId;
                job.RunnerState = outcome.RunnerState;
                job.Status = SyncJobStatus.Submitted;
                job.SubmittedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Could not even start: nothing ran, so the changes go back to Approved for a retry.
                _log.LogWarning(ex, "Submitting job {Job} failed.", job.Id);
                await applier.FailAsync(db, job, $"Could not start the sync: {ex.Message}", ct);
            }
            return;
        }

        if (job.SubmittedAt is { } started && DateTime.UtcNow - started > TimeSpan.FromMinutes(_o.JobTimeoutMinutes))
        {
            await applier.FailAsync(db, job, $"The sync did not finish within {_o.JobTimeoutMinutes} minutes. Retrying is safe: writes are idempotent.", ct);
            return;
        }

        RunnerPoll poll;
        try { poll = await runner.PollAsync(job, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Treat poll errors as transient (network, 5xx); the job timeout bounds how long we keep trying.
            _log.LogWarning(ex, "Polling job {Job} failed; will retry.", job.Id);
            job.LastPolledAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return;
        }

        job.LastPolledAt = DateTime.UtcNow;
        job.DaStatus = poll.DaStatus;
        job.ReportUrl = poll.ReportUrl ?? job.ReportUrl;

        switch (poll.State)
        {
            case RunnerState.Running: await db.SaveChangesAsync(ct); break;
            case RunnerState.Done: await applier.ApplyResultAsync(db, job, poll.Result!, ct); break;
            default: await applier.FailAsync(db, job, poll.Message ?? "The sync failed.", ct); break;
        }
    }
}
