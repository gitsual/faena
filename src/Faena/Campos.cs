using System.Text.Json.Nodes;

namespace Faena;

/// <summary>Reading the JSON that comes from the window, a report or a tramoya project, the way JavaScript reads it.</summary>
public static class Campos
{
    public static string Str(JsonObject p, string key) => p[key]?.ToString() ?? "";

    /// <summary>JavaScript's truthiness: an empty string, array or object, zero and null are false.</summary>
    public static bool Bool(JsonObject p, string key) => p[key] switch
    {
        null => false,
        JsonArray a => a.Count != 0,
        JsonObject o => o.Count != 0,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<string>(out var s) => s.Length != 0,
        JsonValue v when v.TryGetValue<double>(out var d) => d != 0,
        _ => true
    };
}
