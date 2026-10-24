using MelSync;
using Xunit;

namespace MelSync.Core.Tests;

public class EngineGuardTests
{
    [Theory]
    [InlineData(2025, 2025)]
    [InlineData(2026, 2026)]
    public void Same_release_is_allowed(int engine, int model) => Assert.Null(EngineGuard.Check(engine, model));

    [Fact]
    public void Older_engine_cannot_open_a_newer_model()
    {
        var why = EngineGuard.Check(2025, 2026);
        Assert.NotNull(why);
        Assert.Contains("cannot open a newer model", why);
    }

    [Fact]
    public void Newer_engine_is_refused_because_it_would_upgrade_the_shared_model()
    {
        var why = EngineGuard.Check(2026, 2024);
        Assert.NotNull(why);
        Assert.Contains("upgrade the shared cloud model", why);
    }

    [Fact]
    public void Unknown_model_version_is_refused_rather_than_guessed()
    {
        Assert.Contains("unknown", EngineGuard.Check(2025, null), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unknown_engine_version_is_refused()
    {
        Assert.NotNull(EngineGuard.Check(null, 2025));
    }

    [Fact]
    public void Payload_carries_the_revit_version_through_json()
    {
        var p = new SyncPayload { BatchId = "b", RevitVersion = 2026 };
        var back = SyncJson.Deserialize<SyncPayload>(SyncJson.Serialize(p));
        Assert.Equal(2026, back.RevitVersion);
        Assert.Null(SyncJson.Deserialize<SyncPayload>("{\"batchId\":\"x\"}").RevitVersion);   // older payloads still parse
    }
}
