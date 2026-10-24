using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using DesignAutomationFramework;

namespace MelSync.Revit;

[Regeneration(RegenerationOption.Manual)]
[Transaction(TransactionMode.Manual)]
public class MelSyncApp : IExternalDBApplication
{
    // Activity parameters (localName). Keep in step with ApsSetupService in the backend.
    private const string InputFile = "input.json";
    private const string ResultFile = "result.json";
    // MEL_Equipment_GUID (docs/mel-parameters.json): the stable cross-model key used as a fallback lookup.
    private static readonly Guid EquipmentGuidParam = new("8f4a2110-14c1-4d92-a110-312984bc1001");

    public ExternalDBApplicationResult OnStartup(ControlledApplication application)
    {
        DesignAutomationBridge.DesignAutomationReadyEvent += OnReady;
        return ExternalDBApplicationResult.Succeeded;
    }

    public ExternalDBApplicationResult OnShutdown(ControlledApplication application) => ExternalDBApplicationResult.Succeeded;

    private static void OnReady(object? sender, DesignAutomationReadyEventArgs e)
    {
        // The work item "succeeds" as a process whenever a result file is produced; per-element success lives in the result.
        e.Succeeded = Run(e.DesignAutomationData);
    }

    private static bool Run(DesignAutomationData data)
    {
        var result = new SyncResult { RevitVersion = data.RevitApp?.VersionNumber };
        // Diagnostics go both to the result (visible in the app) and to the engine's stdout (visible in the Design Automation report).
        void Log(string m) { result.Log.Add(m); Console.WriteLine("[MelSync] " + m); }
        Document? doc = null;
        try
        {
            var payload = SyncJson.Deserialize<SyncPayload>(File.ReadAllText(InputFile));
            result.BatchId = payload.BatchId;
            Log($"Revit {data.RevitApp?.VersionNumber} (build {data.RevitApp?.VersionBuild}); payload: {payload.Updates.Count} update(s), region {payload.Region}.");
            Log(DescribeAccount(data));

            // Same-release rule: a different engine either cannot open the model or would upgrade the shared cloud model.
            int? engineYear = int.TryParse(data.RevitApp?.VersionNumber, out var ey) ? ey : null;
            if (EngineGuard.Check(engineYear, payload.RevitVersion) is { } refusal) throw new InvalidOperationException(refusal);
            Log($"Engine release {engineYear} matches the model's release {payload.RevitVersion}.");

            doc = Open(data, payload, Log);
            result.Opened = true;

            var model = new RevitModelAccess(doc, EquipmentGuidParam);
            using (var tx = new Transaction(doc, $"MEL parameter sync {payload.BatchId}"))
            {
                tx.Start();
                var failures = tx.GetFailureHandlingOptions();
                failures.SetFailuresPreprocessor(new WarningSwallower());
                tx.SetFailureHandlingOptions(failures);

                result.Results = SyncProcessor.Apply(payload, model);

                tx.Commit();
            }

            var anyApplied = result.Results.Any(r => r.Outcome == Outcome.Applied);
            result.Synced = !anyApplied || SaveToCloud(doc, payload.BatchId);
        }
        catch (Exception ex)
        {
            // Opened/Synced stay as far as they got; the backend turns this into a failed job and retryable items.
            result.Error = Describe(ex);
            Log("Failed: " + result.Error);
        }
        finally
        {
            try { doc?.Close(false); } catch { /* closing must not mask the real outcome */ }
            File.WriteAllText(ResultFile, SyncJson.Serialize(result));
        }
        return true;
    }

    /// <summary>Whether the engine session has an Autodesk account (i.e. adsk3LeggedToken was accepted as a user context). Masked: no full address.</summary>
    private static string DescribeAccount(DesignAutomationData data)
    {
        try
        {
            var u = data.RevitApp?.Username;
            if (string.IsNullOrWhiteSpace(u))
                return "Autodesk account in the engine session: NONE. The adsk3LeggedToken was not accepted as a user context; cloud models cannot be opened.";
            var at = u.IndexOf('@');
            return $"Autodesk account in the engine session: present ({u[0]}***{(at > 0 ? u[at..] : "")}).";
        }
        catch (Exception ex) { return $"Autodesk account in the engine session: could not be read ({ex.GetType().Name})."; }
    }

    private static string Describe(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e != null; e = e.InnerException) parts.Add($"{e.GetType().Name}: {e.Message}");
        return string.Join(" <- inner: ", parts);
    }

    private static Document Open(DesignAutomationData data, SyncPayload p, Action<string> log)
    {
        if (!Guid.TryParse(p.ProjectGuid, out var projectGuid) || !Guid.TryParse(p.ModelGuid, out var modelGuid))
            throw new ArgumentException("payload.projectGuid / modelGuid must be GUIDs.");

        // Literal strings: ModelPathUtils.CloudRegionUS/EMEA are marked obsolete in newer Revit APIs but are just these values.
        var region = p.Region.Equals("EMEA", StringComparison.OrdinalIgnoreCase) ? "EMEA" : "US";

        // Needs the adsk3LeggedToken work item argument (user context) with scope code:all.
        log($"Opening cloud model: region {region}, project {projectGuid}, model {modelGuid}.");
        var path = ModelPathUtils.ConvertCloudGUIDsToCloudPath(region, projectGuid, modelGuid);
        log($"Cloud path built (CloudPath={path.CloudPath}). Calling OpenDocumentFile…");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var doc = data.RevitApp.OpenDocumentFile(path, new OpenOptions());
            log($"Opened in {sw.Elapsed.TotalSeconds:0.0}s (workshared={doc.IsWorkshared}).");
            return doc;
        }
        catch
        {
            log($"OpenDocumentFile failed after {sw.Elapsed.TotalSeconds:0.0}s.");
            throw;
        }
    }

    private static bool SaveToCloud(Document doc, string batchId)
    {
        if (doc.IsWorkshared)
        {
            var swc = new SynchronizeWithCentralOptions { Comment = $"MEL automated parameter sync {batchId}", SaveLocalAfter = false };
            swc.SetRelinquishOptions(new RelinquishOptions(true));
            doc.SynchronizeWithCentral(new TransactWithCentralOptions(), swc);
        }
        else
        {
            doc.SaveCloudModel();
        }
        return true;
    }
}

internal sealed class WarningSwallower : IFailuresPreprocessor
{
    public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
    {
        foreach (var f in failuresAccessor.GetFailureMessages())
            if (f.GetSeverity() == FailureSeverity.Warning) failuresAccessor.DeleteWarning(f);
        return FailureProcessingResult.Continue;
    }
}
