using System.Text.Json;

namespace MasterEquipmentApi.Models;

public record ParameterDefinition(
    string Name, string Category, string DataType, string? SiUnit,
    Guid Guid, string Description, bool UserModifiable, bool Stageable);

// Loaded from docs/mel-parameters.json. The MEP rows are imported from the shared-parameter TXT.
public static class ParameterCatalog
{
    private static readonly Lazy<IReadOnlyList<ParameterDefinition>> _all = new(Load);

    public static IReadOnlyList<ParameterDefinition> All => _all.Value;

    public static ParameterDefinition ByName(string name) => All.First(p => p.Name == name);

    public static ParameterDefinition? Find(Guid guid) => All.FirstOrDefault(p => p.Guid == guid);

    private static IReadOnlyList<ParameterDefinition> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "mel-parameters.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("parameters").EnumerateArray()
            .Select(e => new ParameterDefinition(
                e.GetProperty("name").GetString()!,
                e.GetProperty("category").GetString()!,
                e.GetProperty("dataType").GetString()!,
                e.GetProperty("siUnit").ValueKind == JsonValueKind.Null ? null : e.GetProperty("siUnit").GetString(),
                Guid.Parse(e.GetProperty("guid").GetString()!),
                e.GetProperty("description").GetString()!,
                e.GetProperty("userModifiable").GetBoolean(),
                e.GetProperty("stageable").GetBoolean()))
            .ToList();
    }
}
