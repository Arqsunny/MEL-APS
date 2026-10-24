using System.Collections.Concurrent;
using MelSync;
using MasterEquipmentApi.Models;

namespace MasterEquipmentApi.Sync;

/// <summary>
/// Local development/demo runner; never used outside Development. Takes one extra poll to finish (so "syncing" is observable),
/// then applies every update, except values that start with "FAIL" (simulated failure) or "SKIP" (simulated checked-out element).
/// </summary>
public class MockSyncRunner : ISyncRunner
{
    private readonly ConcurrentDictionary<Guid, int> _polls = new();
    public string Name => "Mock";

    public Task<string?> PreflightAsync(RevitModel? model, CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<SubmitOutcome> SubmitAsync(SyncJob job, SyncPayload payload, CancellationToken ct) =>
        Task.FromResult(new SubmitOutcome("mock-" + job.Id.ToString("N")[..8], null));

    public Task<RunnerPoll> PollAsync(SyncJob job, CancellationToken ct)
    {
        if (_polls.AddOrUpdate(job.Id, 1, (_, n) => n + 1) < 2)
            return Task.FromResult(new RunnerPoll(RunnerState.Running, "inprogress"));

        _polls.TryRemove(job.Id, out _);
        var payload = SyncJson.Deserialize<SyncPayload>(job.PayloadJson);
        var result = new SyncResult { BatchId = payload.BatchId, Opened = true, Synced = true, RevitVersion = "mock" };
        foreach (var u in payload.Updates)
        {
            result.Results.Add(u.Value.StartsWith("FAIL", StringComparison.Ordinal)
                ? new ItemResult { StagingId = u.StagingId, Outcome = Outcome.Failed, Code = "MockFailure", Message = "Simulated failure (value starts with FAIL)." }
                : u.Value.StartsWith("SKIP", StringComparison.Ordinal)
                    ? new ItemResult { StagingId = u.StagingId, Outcome = Outcome.Skipped, Code = Codes.OwnedByOtherUser, Message = "Simulated: element checked out by another user." }
                    : new ItemResult { StagingId = u.StagingId, Outcome = Outcome.Applied, Code = Codes.Ok, AppliedValue = u.Value });
        }
        return Task.FromResult(new RunnerPoll(RunnerState.Done, "success", Result: result));
    }
}
