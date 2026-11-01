// The window's Settings: the language and the AI (backend, server, model, key), kept in
// ajustes.json (Paths.Ajustes) and read by every `faena` that starts, the window's jobs included.
//
// One rule for every setting: what is typed for one run wins over what is saved. A command-line
// flag (--lang, --backend, --model) first, then the environment (FAENA_LANG; FAENA_LLM,
// FAENA_LLM_MODEL, OLLAMA_HOST, ANTHROPIC_API_KEY, FAENA_LLM_URL, FAENA_LLM_KEY), then the saved
// settings, then the defaults (English; Ollama on this computer with qwen3-coder:30b). The saved
// model, server and key belong to the saved backend: a run that picks another backend does not
// inherit them.
using System.Text;
using System.Text.Json.Nodes;
using Faena.Agente;
using Faena.Escena;

namespace Faena;

internal static class Ajustes
{
    public const string KIdioma = "idioma", KBackend = "ia_backend", KModelo = "ia_modelo", KUrl = "ia_url", KClave = "ia_clave";
    public static readonly IReadOnlyList<string> BackendsValidos = ["", "ollama", "claude", "openai"];

    public static string Str(JsonObject? o, string key) =>
        o?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";

    /// <summary>The saved settings ({} when there are none or the file cannot be read).</summary>
    public static JsonObject Leer(string? ruta = null)
    {
        ruta ??= Paths.Ajustes;
        try { return File.Exists(ruta) ? Json.Parse(File.ReadAllText(ruta, Encoding.UTF8)) as JsonObject ?? new JsonObject() : new JsonObject(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return new JsonObject(); }
    }

    /// <summary>
    /// Merge what the window sends into the saved settings and write them. An empty key keeps the
    /// saved one (the window never gets it back); <c>ia_clave_borrar</c> removes it. Changing the
    /// backend forgets the model and the server of the old one unless new ones come along.
    /// </summary>
    public static JsonObject Guardar(JsonObject cambios, string? ruta = null)
    {
        ruta ??= Paths.Ajustes;
        var a = Leer(ruta);
        if (cambios.ContainsKey(KIdioma))
        {
            var idioma = Str(cambios, KIdioma);
            if (!Lengua.Valido(idioma)) throw new ArgumentException(Lengua.T($"idioma desconocido «{idioma}»: usa en o es", $"unknown language “{idioma}”: use en or es"));
            a[KIdioma] = Lengua.Normalizar(idioma);
        }
        if (cambios.ContainsKey(KBackend))
        {
            var b = Str(cambios, KBackend).ToLowerInvariant();
            if (b == "auto") b = "";
            if (!BackendsValidos.Contains(b)) throw new ArgumentException(Lengua.T($"backend desconocido «{b}»: usa ollama, claude u openai", $"unknown backend “{b}”: use ollama, claude or openai"));
            if (b != Str(a, KBackend))
            {
                a.Remove(KModelo);
                a.Remove(KUrl);
                a.Remove(KClave);
            }
            a[KBackend] = b;
        }
        foreach (var k in new[] { KModelo, KUrl })
            if (cambios.ContainsKey(k)) a[k] = Str(cambios, k);
        if (Str(cambios, KClave).Length != 0) a[KClave] = Str(cambios, KClave);
        if (cambios["ia_clave_borrar"] is JsonValue borrar && borrar.TryGetValue<bool>(out var si) && si) a.Remove(KClave);
        Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
        File.WriteAllText(ruta, Json.Dump(a, indent: true), new UTF8Encoding(false));
        return a;
    }

    /// <summary>The settings as the window may see them: the key replaced by whether there is one and its last characters.</summary>
    public static JsonObject Publicos(JsonObject ajustes)
    {
        var copia = ajustes.DeepClone().AsObject();
        var clave = Str(ajustes, KClave);
        copia[KClave] = "";
        copia["ia_clave_guardada"] = clave.Length != 0;
        copia["ia_clave_fin"] = clave.Length >= 8 ? clave[^4..] : "";
        return copia;
    }

    public static IDictionary<string, string> Entorno() =>
        Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "", StringComparer.OrdinalIgnoreCase);

    private static string? Get(IDictionary<string, string> env, string k) =>
        env.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    /// <summary>The language for this run: --lang, then FAENA_LANG, then the saved one, then English.</summary>
    public static string Idioma(string? flag, JsonObject ajustes, IDictionary<string, string>? env = null)
    {
        env ??= Entorno();
        if (Lengua.Valido(flag)) return Lengua.Normalizar(flag);
        if (Get(env, Lengua.Variable) is { } e && Lengua.Valido(e)) return Lengua.Normalizar(e);
        if (Lengua.Valido(Str(ajustes, KIdioma))) return Lengua.Normalizar(Str(ajustes, KIdioma));
        return "en";
    }

    /// <summary>The backend this run ends up with (before the defaults of Backends.Pick fill model and server).</summary>
    public static string BackendElegido(string? flag, JsonObject ajustes, IDictionary<string, string> env) =>
        (string.IsNullOrWhiteSpace(flag) ? null : flag.Trim().ToLowerInvariant())
        ?? Get(env, "FAENA_LLM") ?? Get(env, "TRAMOYA_LLM")
        ?? (Str(ajustes, KBackend) is { Length: > 0 } b ? b : null)
        ?? (Get(env, "ANTHROPIC_API_KEY") is not null ? "claude" : "ollama");

    /// <summary>
    /// The environment Backends.Pick reads, with the saved settings filled in where neither the
    /// flags nor the environment say anything. The saved model, server and key only go with the saved backend.
    /// </summary>
    public static Dictionary<string, string> EntornoIa(string? flagBackend, JsonObject ajustes, IDictionary<string, string>? env = null)
    {
        env ??= Entorno();
        var r = new Dictionary<string, string>(env, StringComparer.OrdinalIgnoreCase);
        var guardado = Str(ajustes, KBackend);
        if (guardado.Length == 0) return r;
        var elegido = BackendElegido(flagBackend, ajustes, env);
        if (string.IsNullOrWhiteSpace(flagBackend) && Get(env, "FAENA_LLM") is null && Get(env, "TRAMOYA_LLM") is null) r["FAENA_LLM"] = guardado;
        if (elegido != guardado) return r;
        void Poner(string k, string v) { if (v.Length != 0 && Get(env, k) is null) r[k] = v; }
        Poner("FAENA_LLM_MODEL", Str(ajustes, KModelo));
        switch (guardado)
        {
            case "ollama": Poner("OLLAMA_HOST", Str(ajustes, KUrl)); break;
            case "claude": Poner("ANTHROPIC_API_KEY", Str(ajustes, KClave)); break;
            case "openai":
                Poner("FAENA_LLM_URL", Str(ajustes, KUrl));
                Poner("FAENA_LLM_KEY", Str(ajustes, KClave));
                break;
        }
        return r;
    }

    /// <summary>The model for one run, weighing flags, environment and settings.</summary>
    public static IChatBackend Backend(string? flagBackend, string? flagModel, JsonObject ajustes, IDictionary<string, string>? env = null) =>
        Backends.Pick(flagBackend, flagModel, EntornoIa(flagBackend, ajustes, env));

    /// <summary>What the window says about the chosen model: backend, model and where (never the key).</summary>
    public static (string Backend, string Modelo, string Url) Describir(IChatBackend b) => b switch
    {
        OllamaChatBackend o => ("ollama", o.Model, o.BaseUrl),
        ClaudeChatBackend c => ("claude", c.Model, ""),
        OpenAiChatBackend a => ("openai", a.Model, a.BaseUrl),
        _ => ("?", "", ""),
    };
}
