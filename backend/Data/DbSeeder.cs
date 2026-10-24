using Microsoft.EntityFrameworkCore;
using MasterEquipmentApi.Models;

namespace MasterEquipmentApi.Data;

// Demo data for local development only (Database:SeedDemoData). Idempotent: skips when equipment exists.
public static class DbSeeder
{
    public const string DemoModelGuid = "demo-model";

    public static async Task SeedAsync(MelDbContext db)
    {
        if (!await db.Models.AnyAsync(m => m.ModelGuid == DemoModelGuid))
            db.Models.Add(new RevitModel { ModelGuid = DemoModelGuid, DisplayName = "Building B - Central Plant" });
        if (!await db.Equipment.AnyAsync()) db.Equipment.AddRange(Build());
        await db.SaveChangesAsync();
    }

    private static Dictionary<Guid, string> V(string guid, string tag, double lbs, double cfm, double kw, string v, string po, int weeks, string proc) => new()
    {
        [ParameterCatalog.ByName("MEL_Equipment_GUID").Guid] = guid,
        [ParameterCatalog.ByName("MEL_Tag_Number").Guid] = tag,
        [ParameterCatalog.ByName("MEL_Operating_Weight").Guid] = (lbs / 2.2046226218).ToString("0.####"),
        [ParameterCatalog.ByName("MEL_Design_Airflow").Guid] = (cfm / 2.11888).ToString("0.####"),
        [ParameterCatalog.ByName("MEL_Electrical_Load").Guid] = kw.ToString("0.##"),
        [ParameterCatalog.ByName("MEL_Voltage").Guid] = v,
        [ParameterCatalog.ByName("MEL_Phases").Guid] = "3",
        [ParameterCatalog.ByName("MEL_PO_Number").Guid] = po,
        [ParameterCatalog.ByName("MEL_Lead_Time_Weeks").Guid] = weeks.ToString(),
        [ParameterCatalog.ByName("MEL_Procurement_Status").Guid] = proc,
    };

    private static EquipmentItem E(string uid, string tag, string type, string disc, Dictionary<Guid, string> vals) => new()
    {
        ModelGuid = DemoModelGuid,
        UniqueId = uid,
        EquipmentGuid = vals[ParameterCatalog.ByName("MEL_Equipment_GUID").Guid],
        Tag = tag, FamilyType = type, Discipline = disc, Values = vals
    };

    private static List<EquipmentItem> Build() => new()
    {
        E("9f84b2c1-482a-412e-a4b8-f14d021c4321-0003e8a1", "AHU-01", "AHU_Custom_Rooftop : Size 120", "Mechanical",
          V("a1f0c2d4-0001-4a00-8000-000000000001", "AHU-01", 8450, 12500, 45, "480", "PO-2026-9481", 22, "Submittal Approved")),
        E("e14a88bc-19fe-49aa-8411-9a7b032d8471-0003e8b2", "CH-01", "Chiller_WaterCooled : 500-Ton", "Mechanical",
          V("a1f0c2d4-0002-4a00-8000-000000000002", "CH-01", 18200, 0, 350, "480", "PO-2026-9488", 36, "PO Issued")),
        E("7c20ad41-bb01-49fa-c438-bb4510fae120-0003e8c3", "P-01", "Pump_BaseMounted : Series 100", "Mechanical",
          V("a1f0c2d4-0003-4a00-8000-000000000003", "P-01", 650, 0, 11, "208", "PO-2026-8801", 8, "On Site")),
        E("45d12ef3-7182-4aa8-9331-dd34e12c5549-0003e8d4", "CT-01", "CoolingTower_Crossflow : CT-2C", "Mechanical",
          V("a1f0c2d4-0004-4a00-8000-000000000004", "CT-01", 12500, 45000, 75, "480", "PO-2026-9102", 28, "Spec Issued")),
    };
}
