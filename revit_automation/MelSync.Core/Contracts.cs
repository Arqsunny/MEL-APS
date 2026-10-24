using System.Text.Json;
using System.Text.Json.Serialization;

namespace MelSync;

/// <summary>Input to the Design Automation activity (the "inputJson" argument).</summary>
public sealed class SyncPayload
{
    public int Version { get; set; } = 1;
    public string BatchId { get; set; } = string.Empty;
    /// <summary>Cloud model location (Revit cloud model API): "US" or "EMEA".</summary>
    public string Region { get; set; } = "US";
    public string ProjectGuid { get; set; } = string.Empty;
    public string ModelGuid { get; set; } = string.Empty;
    /// <summary>Revit release the model was last saved in (e.g. 2025). The engine must be the same release; see <see cref="EngineGuard"/>.</summary>
    public int? RevitVersion { get; set; }
    public List<UpdateItem> Updates { get; set; } = new();
}

public sealed class UpdateItem
{
    /// <summary>Staging record id; echoed back in the result so outcomes map 1:1.</summary>
    public string StagingId { get; set; } = string.Empty;
    public string UniqueId { get; set; } = string.Empty;
    /// <summary>MEL_Equipment_GUID: fallback key if the UniqueId no longer resolves.</summary>
    public string? EquipmentGuid { get; set; }
    public string ParameterGuid { get; set; } = string.Empty;
    public string ParameterName { get; set; } = string.Empty;
    /// <summary>Catalog data type: TEXT, INTEGER, MASS, ELECTRICAL_POWER, AIRFLOW, ELECTRICAL_POTENTIAL ...</summary>
    public string DataType { get; set; } = "TEXT";
    /// <summary>SI unit the value is expressed in (null for TEXT / unitless INTEGER).</summary>
    public string? SiUnit { get; set; }
    /// <summary>Value as text, invariant culture, in the SI unit above.</summary>
    public string Value { get; set; } = string.Empty;
}

public enum Outcome { Applied, Skipped, Failed }

/// <summary>Machine-readable reasons; the UI and audit log show these.</summary>
public static class Codes
{
    public const string Ok = "OK";
    public const string ElementNotFound = "ElementNotFound";
    public const string ParameterNotFound = "ParameterNotFound";
    public const string ReadOnly = "ReadOnly";
    public const string TypeMismatch = "TypeMismatch";
    public const string UnitMismatch = "UnitMismatch";
    public const string UnsupportedUnit = "UnsupportedUnit";
    public const string InvalidValue = "InvalidValue";
    public const string OwnedByOtherUser = "OwnedByOtherUser";
    public const string NoResult = "NoResult";
    public const string Error = "Error";
}

public sealed class ItemResult
{
    public string StagingId { get; set; } = string.Empty;
    public Outcome Outcome { get; set; }
    public string Code { get; set; } = Codes.Ok;
    public string? Message { get; set; }
    /// <summary>Value read back from the model after the write (SI for measured parameters).</summary>
    public string? AppliedValue { get; set; }
}

/// <summary>Output of the activity (the "result" argument). Always written, even when opening the model failed.</summary>
public sealed class SyncResult
{
    public int Version { get; set; } = 1;
    public string BatchId { get; set; } = string.Empty;
    public bool Opened { get; set; }
    /// <summary>True only if the changes were saved/synchronized to the cloud model. Applied items are not durable otherwise.</summary>
    public bool Synced { get; set; }
    public string? Error { get; set; }
    public string? RevitVersion { get; set; }
    /// <summary>Plain-text diagnostics from inside the engine (what was attempted, which account the session had). Never contains secrets.</summary>
    public List<string> Log { get; set; } = new();
    public List<ItemResult> Results { get; set; } = new();
}

public static class SyncJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException("Empty JSON document.");
}
