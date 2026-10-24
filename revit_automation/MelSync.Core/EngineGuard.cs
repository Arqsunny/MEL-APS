namespace MelSync;

/// <summary>
/// A Revit engine may only open a model saved in the SAME release. An older engine cannot open a newer model, and a newer engine
/// would upgrade the shared cloud model for every user the moment it syncs. So anything other than an exact match is refused
/// before the model is touched.
/// </summary>
public static class EngineGuard
{
    /// <summary>Null when the engine may open the model; otherwise the reason it must not.</summary>
    public static string? Check(int? engineYear, int? modelYear)
    {
        if (modelYear is null)
            return "The model's Revit version is unknown. Link it again with the Link model button so the right engine is chosen; opening it with the wrong release could upgrade the shared model.";
        if (engineYear is null)
            return "The engine's Revit version could not be determined, so the model was not opened.";
        if (engineYear == modelYear) return null;
        return engineYear < modelYear
            ? $"The model was saved in Revit {modelYear} but this is a Revit {engineYear} engine, which cannot open a newer model."
            : $"The model was saved in Revit {modelYear} but this is a Revit {engineYear} engine; opening it would upgrade the shared cloud model for everyone, so it was not opened.";
    }
}
