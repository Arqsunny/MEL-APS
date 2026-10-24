using System.Globalization;
using Autodesk.Revit.DB;

namespace MelSync.Revit;

/// <summary>Adapts a Revit <see cref="Document"/> to the pure <see cref="IModelAccess"/> used by the processor.</summary>
internal sealed class RevitModelAccess : IModelAccess
{
    private readonly Document _doc;
    private readonly Guid _equipmentGuidParam;
    private Dictionary<string, Element>? _byEquipmentGuid;

    public RevitModelAccess(Document doc, Guid equipmentGuidParam)
    {
        _doc = doc;
        _equipmentGuidParam = equipmentGuidParam;
    }

    public IElementHandle? FindElement(string uniqueId, string? equipmentGuid)
    {
        var el = string.IsNullOrWhiteSpace(uniqueId) ? null : _doc.GetElement(uniqueId);
        if (el == null && !string.IsNullOrWhiteSpace(equipmentGuid))
        {
            // UniqueId changes if an element is deleted and recreated; MEL_Equipment_GUID is the stable key.
            _byEquipmentGuid ??= BuildGuidIndex();
            _byEquipmentGuid.TryGetValue(equipmentGuid!, out el);
        }
        return el == null ? null : new RevitElementHandle(_doc, el);
    }

    // Built lazily and only when a UniqueId lookup fails, because it scans every family instance.
    private Dictionary<string, Element> BuildGuidIndex()
    {
        var map = new Dictionary<string, Element>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in new FilteredElementCollector(_doc).OfClass(typeof(FamilyInstance)))
        {
            var v = e.get_Parameter(_equipmentGuidParam)?.AsString();
            if (!string.IsNullOrWhiteSpace(v)) map.TryAdd(v!, e);
        }
        return map;
    }
}

internal sealed class RevitElementHandle : IElementHandle
{
    private readonly Document _doc;
    private readonly Element _el;
    public RevitElementHandle(Document doc, Element el) { _doc = doc; _el = el; }

    public bool IsOwnedByOtherUser(out string? owner)
    {
        owner = null;
        if (!_doc.IsWorkshared) return false;
        var status = WorksharingUtils.GetCheckoutStatus(_doc, _el.Id, out var who);
        if (status != CheckoutStatus.OwnedByOtherUser) return false;
        owner = who;
        return true;
    }

    public IParameterHandle? GetParameter(Guid parameterGuid)
    {
        var p = _el.get_Parameter(parameterGuid);
        return p == null ? null : new RevitParameterHandle(p);
    }
}

internal sealed class RevitParameterHandle : IParameterHandle
{
    private readonly Parameter _p;
    public RevitParameterHandle(Parameter p) { _p = p; }

    public ParamStorage Storage => _p.StorageType switch
    {
        StorageType.String => ParamStorage.String,
        StorageType.Integer => ParamStorage.Integer,
        StorageType.Double => ParamStorage.Double,
        StorageType.ElementId => ParamStorage.ElementId,
        _ => ParamStorage.None
    };

    public bool IsReadOnly => _p.IsReadOnly;

    // Parameter.Set returns false (rather than throwing) when Revit rejects a value; never treat that as success.
    public void SetText(string value)
    {
        if (!_p.Set(value)) throw new InvalidOperationException("Revit rejected the text value.");
    }

    public void SetInteger(int value)
    {
        if (!_p.Set(value)) throw new InvalidOperationException("Revit rejected the integer value.");
    }

    public MeasuredResult SetMeasured(double siValue, string siUnit)
    {
        var unit = ToRevitUnit(siUnit);
        if (unit == null)
            return new MeasuredResult(false, Codes.UnsupportedUnit, $"No Revit unit mapped for '{siUnit}'.", null);

        // The parameter's own spec decides what it can hold: a Length/Number parameter must never receive kilograms.
        var spec = _p.Definition.GetDataType();
        if (!UnitUtils.IsMeasurableSpec(spec) || !UnitUtils.GetValidUnits(spec).Contains(unit))
            return new MeasuredResult(false, Codes.UnitMismatch,
                $"Parameter spec '{spec.TypeId}' cannot be expressed in {siUnit}. Check the shared parameter's data type.", null);

        // Revit stores measured values in internal units (feet, etc.), so convert from the SI value we hold.
        if (!_p.Set(UnitUtils.ConvertToInternalUnits(siValue, unit)))
            return new MeasuredResult(false, Codes.Error, "Revit rejected the numeric value.", null);

        var readBack = UnitUtils.ConvertFromInternalUnits(_p.AsDouble(), unit);
        return new MeasuredResult(true, Codes.Ok, null, readBack.ToString("0.####", CultureInfo.InvariantCulture));
    }

    // Must stay in step with MelSync.UnitCatalog.
    private static ForgeTypeId? ToRevitUnit(string siUnit) => siUnit switch
    {
        "kg" => UnitTypeId.Kilograms,
        "kW" => UnitTypeId.Kilowatts,
        "L/s" => UnitTypeId.LitersPerSecond,
        "V" => UnitTypeId.Volts,
        _ => null
    };
}
