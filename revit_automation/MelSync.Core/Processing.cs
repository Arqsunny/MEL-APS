using System.Globalization;

namespace MelSync;

public enum ParamStorage { None, String, Integer, Double, ElementId }

public readonly record struct MeasuredResult(bool Ok, string Code, string? Message, string? ReadBackSi);

/// <summary>A parameter on one element, as seen by the processor. The Revit adapter implements this over the Revit API.</summary>
public interface IParameterHandle
{
    ParamStorage Storage { get; }
    bool IsReadOnly { get; }
    void SetText(string value);
    void SetInteger(int value);
    /// <summary>
    /// Converts <paramref name="siValue"/> (expressed in <paramref name="siUnit"/>) to the model's internal units and writes it.
    /// Must return UnitMismatch if the parameter's spec cannot be expressed in that unit. Returns the value read back, in SI.
    /// </summary>
    MeasuredResult SetMeasured(double siValue, string siUnit);
}

public interface IElementHandle
{
    /// <summary>True when another user has the element checked out (workshared models); the write would fail or conflict.</summary>
    bool IsOwnedByOtherUser(out string? owner);
    IParameterHandle? GetParameter(Guid parameterGuid);
}

public interface IModelAccess
{
    /// <summary>Resolve by Revit UniqueId; fall back to MEL_Equipment_GUID when the UniqueId no longer exists.</summary>
    IElementHandle? FindElement(string uniqueId, string? equipmentGuid);
}

/// <summary>SI units the catalog may use and the adapter must be able to convert.</summary>
public static class UnitCatalog
{
    private static readonly HashSet<string> Supported = new(StringComparer.Ordinal) { "kg", "kW", "L/s", "V" };
    public static bool IsSupported(string? unit) => unit != null && Supported.Contains(unit);
}

public static class SyncProcessor
{
    /// <summary>
    /// Applies every update independently: one bad element never blocks the others, and exactly one result is returned
    /// per update, in input order. The caller owns the transaction, saving and synchronizing.
    /// </summary>
    public static List<ItemResult> Apply(SyncPayload payload, IModelAccess model)
    {
        var results = new List<ItemResult>(payload.Updates.Count);
        foreach (var u in payload.Updates)
        {
            ItemResult r;
            try { r = ApplyOne(u, model); }
            catch (Exception ex)
            {
                r = Fail(u, Codes.Error, $"{ex.GetType().Name}: {ex.Message}");
            }
            results.Add(r);
        }
        return results;
    }

    private static ItemResult ApplyOne(UpdateItem u, IModelAccess model)
    {
        if (!Guid.TryParse(u.ParameterGuid, out var pguid))
            return Fail(u, Codes.InvalidValue, "ParameterGuid is not a valid GUID.");

        var element = model.FindElement(u.UniqueId, u.EquipmentGuid);
        if (element == null)
            return Fail(u, Codes.ElementNotFound, "No element with this UniqueId or MEL_Equipment_GUID in the model.");

        if (element.IsOwnedByOtherUser(out var owner))
            return Skip(u, Codes.OwnedByOtherUser, $"Element is checked out by {owner ?? "another user"}; retry later.");

        var p = element.GetParameter(pguid);
        if (p == null || p.Storage == ParamStorage.None)
            return Fail(u, Codes.ParameterNotFound, $"{u.ParameterName} is not bound to this element's category/family.");
        if (p.IsReadOnly)
            return Fail(u, Codes.ReadOnly, $"{u.ParameterName} is read-only on this element.");

        switch (u.DataType)
        {
            case "TEXT":
                if (p.Storage != ParamStorage.String)
                    return Fail(u, Codes.TypeMismatch, $"{u.ParameterName} is {p.Storage} in the model but TEXT in the catalog.");
                p.SetText(u.Value);
                return Applied(u, u.Value);

            case "INTEGER":
                if (p.Storage != ParamStorage.Integer)
                    return Fail(u, Codes.TypeMismatch, $"{u.ParameterName} is {p.Storage} in the model but INTEGER in the catalog.");
                if (!int.TryParse(u.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                    return Fail(u, Codes.InvalidValue, $"'{u.Value}' is not a whole number.");
                p.SetInteger(i);
                return Applied(u, i.ToString(CultureInfo.InvariantCulture));

            default: // measured quantity (MASS, AIRFLOW, ELECTRICAL_POWER, ...)
                if (p.Storage != ParamStorage.Double)
                    return Fail(u, Codes.TypeMismatch, $"{u.ParameterName} is {p.Storage} in the model but {u.DataType} in the catalog.");
                if (!UnitCatalog.IsSupported(u.SiUnit))
                    return Fail(u, Codes.UnsupportedUnit, $"Unit '{u.SiUnit}' is not supported by the bundle.");
                if (!double.TryParse(u.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d))
                    return Fail(u, Codes.InvalidValue, $"'{u.Value}' is not a finite number.");
                var m = p.SetMeasured(d, u.SiUnit!);
                return m.Ok
                    ? Applied(u, m.ReadBackSi ?? u.Value)
                    : Fail(u, m.Code, m.Message);
        }
    }

    private static ItemResult Applied(UpdateItem u, string applied) =>
        new() { StagingId = u.StagingId, Outcome = Outcome.Applied, Code = Codes.Ok, AppliedValue = applied };

    private static ItemResult Fail(UpdateItem u, string code, string? message) =>
        new() { StagingId = u.StagingId, Outcome = Outcome.Failed, Code = code, Message = message };

    private static ItemResult Skip(UpdateItem u, string code, string? message) =>
        new() { StagingId = u.StagingId, Outcome = Outcome.Skipped, Code = code, Message = message };
}
