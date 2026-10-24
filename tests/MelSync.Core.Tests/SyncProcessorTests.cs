using MelSync;
using Xunit;

namespace MelSync.Core.Tests;

public class SyncProcessorTests
{
    // ---- fakes -------------------------------------------------------------------------------------------------
    private sealed class FakeParam : IParameterHandle
    {
        public ParamStorage Storage { get; init; } = ParamStorage.String;
        public bool IsReadOnly { get; init; }
        public string? Text; public int? Int; public double? Measured; public string? Unit;
        public Func<double, string, MeasuredResult>? MeasuredBehavior;
        public Exception? ThrowOnSet;
        public void SetText(string v) { if (ThrowOnSet != null) throw ThrowOnSet; Text = v; }
        public void SetInteger(int v) { if (ThrowOnSet != null) throw ThrowOnSet; Int = v; }
        public MeasuredResult SetMeasured(double si, string unit)
        {
            if (ThrowOnSet != null) throw ThrowOnSet;
            Measured = si; Unit = unit;
            return MeasuredBehavior?.Invoke(si, unit) ?? new MeasuredResult(true, Codes.Ok, null, si.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private sealed class FakeElement : IElementHandle
    {
        public string? Owner;
        public Dictionary<Guid, FakeParam> Params { get; } = new();
        public bool IsOwnedByOtherUser(out string? owner) { owner = Owner; return Owner != null; }
        public IParameterHandle? GetParameter(Guid g) => Params.GetValueOrDefault(g);
    }

    private sealed class FakeModel : IModelAccess
    {
        public Dictionary<string, FakeElement> ByUid { get; } = new();
        public Dictionary<string, FakeElement> ByGuid { get; } = new();
        public IElementHandle? FindElement(string uid, string? eg) =>
            ByUid.GetValueOrDefault(uid) ?? (eg != null ? ByGuid.GetValueOrDefault(eg) : null);
    }

    private static readonly Guid PText = Guid.NewGuid(), PInt = Guid.NewGuid(), PMass = Guid.NewGuid();

    private static UpdateItem U(string id, Guid p, string type, string value, string? unit = null, string uid = "u1", string? eg = null) =>
        new() { StagingId = id, UniqueId = uid, EquipmentGuid = eg, ParameterGuid = p.ToString(), ParameterName = "MEL_X", DataType = type, SiUnit = unit, Value = value };

    private static SyncPayload P(params UpdateItem[] items) => new() { BatchId = "b", Updates = items.ToList() };

    private static (FakeModel model, FakeElement el, FakeParam text, FakeParam integer, FakeParam mass) Setup()
    {
        var el = new FakeElement();
        var text = new FakeParam { Storage = ParamStorage.String };
        var integer = new FakeParam { Storage = ParamStorage.Integer };
        var mass = new FakeParam { Storage = ParamStorage.Double };
        el.Params[PText] = text; el.Params[PInt] = integer; el.Params[PMass] = mass;
        var m = new FakeModel(); m.ByUid["u1"] = el;
        return (m, el, text, integer, mass);
    }

    // ---- happy paths -------------------------------------------------------------------------------------------
    [Fact]
    public void Applies_text_integer_and_measured_values()
    {
        var (m, _, text, integer, mass) = Setup();
        var r = SyncProcessor.Apply(P(U("a", PText, "TEXT", "PO-1"), U("b", PInt, "INTEGER", "18"), U("c", PMass, "MASS", "3855.53", "kg")), m);

        Assert.All(r, x => Assert.Equal(Outcome.Applied, x.Outcome));
        Assert.Equal("PO-1", text.Text);
        Assert.Equal(18, integer.Int);
        Assert.Equal(3855.53, mass.Measured);
        Assert.Equal("kg", mass.Unit);
        Assert.Equal("3855.53", r[2].AppliedValue);
    }

    [Fact]
    public void Returns_exactly_one_result_per_update_in_input_order()
    {
        var (m, _, _, _, _) = Setup();
        var r = SyncProcessor.Apply(P(U("1", PText, "TEXT", "a"), U("2", PText, "TEXT", "b", uid: "missing"), U("3", PInt, "INTEGER", "5")), m);
        Assert.Equal(new[] { "1", "2", "3" }, r.Select(x => x.StagingId));
    }

    [Fact]
    public void Number_parsing_is_culture_invariant()
    {
        var old = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE"); // decimal comma
            var (m, _, _, _, mass) = Setup();
            var r = SyncProcessor.Apply(P(U("c", PMass, "MASS", "3855.53", "kg")), m);
            Assert.Equal(Outcome.Applied, r[0].Outcome);
            Assert.Equal(3855.53, mass.Measured);
        }
        finally { Thread.CurrentThread.CurrentCulture = old; }
    }

    // ---- element resolution ------------------------------------------------------------------------------------
    [Fact]
    public void Falls_back_to_equipment_guid_when_uniqueid_is_gone()
    {
        var (m, el, text, _, _) = Setup();
        m.ByUid.Clear(); m.ByGuid["EG-1"] = el;
        var r = SyncProcessor.Apply(P(U("a", PText, "TEXT", "x", uid: "stale-uid", eg: "EG-1")), m);
        Assert.Equal(Outcome.Applied, r[0].Outcome);
        Assert.Equal("x", text.Text);
    }

    [Fact]
    public void Missing_element_fails_with_ElementNotFound_and_does_not_block_others()
    {
        var (m, _, text, _, _) = Setup();
        var r = SyncProcessor.Apply(P(U("a", PText, "TEXT", "x", uid: "nope"), U("b", PText, "TEXT", "y")), m);
        Assert.Equal(Codes.ElementNotFound, r[0].Code);
        Assert.Equal(Outcome.Failed, r[0].Outcome);
        Assert.Equal(Outcome.Applied, r[1].Outcome);
        Assert.Equal("y", text.Text);
    }

    [Fact]
    public void Element_owned_by_other_user_is_skipped_not_written()
    {
        var (m, el, text, _, _) = Setup();
        el.Owner = "jane@firm.com";
        var r = SyncProcessor.Apply(P(U("a", PText, "TEXT", "x")), m);
        Assert.Equal(Outcome.Skipped, r[0].Outcome);
        Assert.Equal(Codes.OwnedByOtherUser, r[0].Code);
        Assert.Contains("jane@firm.com", r[0].Message);
        Assert.Null(text.Text);
    }

    // ---- parameter problems ------------------------------------------------------------------------------------
    [Fact]
    public void Unbound_parameter_fails_with_ParameterNotFound()
    {
        var (m, _, _, _, _) = Setup();
        var r = SyncProcessor.Apply(P(U("a", Guid.NewGuid(), "TEXT", "x")), m);
        Assert.Equal(Codes.ParameterNotFound, r[0].Code);
    }

    [Fact]
    public void Read_only_parameter_fails_with_ReadOnly()
    {
        var (m, el, _, _, _) = Setup();
        el.Params[PText] = new FakeParam { Storage = ParamStorage.String, IsReadOnly = true };
        var r = SyncProcessor.Apply(P(U("a", PText, "TEXT", "x")), m);
        Assert.Equal(Codes.ReadOnly, r[0].Code);
    }

    [Theory]
    [InlineData("TEXT", ParamStorage.Integer)]
    [InlineData("TEXT", ParamStorage.Double)]
    [InlineData("INTEGER", ParamStorage.String)]
    [InlineData("MASS", ParamStorage.String)]
    [InlineData("MASS", ParamStorage.Integer)]
    public void Catalog_type_must_match_model_storage(string dataType, ParamStorage actual)
    {
        var (m, el, _, _, _) = Setup();
        el.Params[PText] = new FakeParam { Storage = actual };
        var r = SyncProcessor.Apply(P(U("a", PText, dataType, "1", dataType == "MASS" ? "kg" : null)), m);
        Assert.Equal(Codes.TypeMismatch, r[0].Code);
        Assert.Equal(Outcome.Failed, r[0].Outcome);
    }

    // ---- value validation --------------------------------------------------------------------------------------
    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("1e999")]
    public void Non_finite_or_unparseable_numbers_are_rejected_without_writing(string bad)
    {
        var (m, _, _, _, mass) = Setup();
        var r = SyncProcessor.Apply(P(U("a", PMass, "MASS", bad, "kg")), m);
        Assert.Equal(Codes.InvalidValue, r[0].Code);
        Assert.Null(mass.Measured);
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("99999999999")]
    public void Integer_values_must_be_whole_numbers_in_range(string bad)
    {
        var (m, _, _, integer, _) = Setup();
        var r = SyncProcessor.Apply(P(U("a", PInt, "INTEGER", bad)), m);
        Assert.Equal(Codes.InvalidValue, r[0].Code);
        Assert.Null(integer.Int);
    }

    [Fact]
    public void Unknown_unit_is_rejected_before_any_write()
    {
        var (m, _, _, _, mass) = Setup();
        var r = SyncProcessor.Apply(P(U("a", PMass, "MASS", "5", "stone"), U("b", PMass, "MASS", "5", null)), m);
        Assert.All(r, x => Assert.Equal(Codes.UnsupportedUnit, x.Code));
        Assert.Null(mass.Measured);
    }

    [Fact]
    public void Adapter_reported_unit_mismatch_is_surfaced()
    {
        var (m, el, _, _, _) = Setup();
        el.Params[PMass] = new FakeParam
        {
            Storage = ParamStorage.Double,
            MeasuredBehavior = (_, _) => new MeasuredResult(false, Codes.UnitMismatch, "Parameter is Length, not Mass.", null)
        };
        var r = SyncProcessor.Apply(P(U("a", PMass, "MASS", "5", "kg")), m);
        Assert.Equal(Codes.UnitMismatch, r[0].Code);
        Assert.Equal("Parameter is Length, not Mass.", r[0].Message);
    }

    // ---- robustness --------------------------------------------------------------------------------------------
    [Fact]
    public void Exception_from_Revit_is_contained_to_that_update()
    {
        var (m, el, _, _, _) = Setup();
        el.Params[PText] = new FakeParam { Storage = ParamStorage.String, ThrowOnSet = new InvalidOperationException("boom") };
        var r = SyncProcessor.Apply(P(U("a", PText, "TEXT", "x"), U("b", PInt, "INTEGER", "7")), m);
        Assert.Equal(Codes.Error, r[0].Code);
        Assert.Contains("boom", r[0].Message);
        Assert.Equal(Outcome.Applied, r[1].Outcome);
    }

    [Fact]
    public void Bad_parameter_guid_is_a_per_item_failure()
    {
        var (m, _, _, _, _) = Setup();
        var item = U("a", PText, "TEXT", "x"); item.ParameterGuid = "not-a-guid";
        var r = SyncProcessor.Apply(P(item), m);
        Assert.Equal(Codes.InvalidValue, r[0].Code);
    }

    [Fact]
    public void Empty_payload_yields_no_results()
    {
        var (m, _, _, _, _) = Setup();
        Assert.Empty(SyncProcessor.Apply(P(), m));
    }

    // ---- contracts ---------------------------------------------------------------------------------------------
    [Fact]
    public void Contracts_round_trip_as_camelCase_json_with_string_enums()
    {
        var res = new SyncResult
        {
            BatchId = "b", Opened = true, Synced = true,
            Results = { new ItemResult { StagingId = "s", Outcome = Outcome.Skipped, Code = Codes.OwnedByOtherUser } }
        };
        var json = SyncJson.Serialize(res);
        Assert.Contains("\"batchId\":\"b\"", json);
        Assert.Contains("\"outcome\":\"Skipped\"", json);
        var back = SyncJson.Deserialize<SyncResult>(json);
        Assert.Equal(Outcome.Skipped, back.Results[0].Outcome);

        var payload = SyncJson.Deserialize<SyncPayload>("{\"batchId\":\"x\",\"updates\":[{\"stagingId\":\"1\",\"uniqueId\":\"u\",\"parameterGuid\":\"g\",\"value\":\"v\"}]}");
        Assert.Equal("x", payload.BatchId);
        Assert.Equal("TEXT", payload.Updates[0].DataType);
    }
}
