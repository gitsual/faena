using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Faena.Escena;

namespace Faena.Agente;

/// <summary>Un mensaje de la conversación: <c>user</c> o <c>assistant</c>, con una captura PNG opcional.</summary>
public sealed record ChatMessage(string Role, string Text, byte[]? ImagePng = null);

/// <summary>Un modelo de lenguaje con el que se conversa por turnos; el agente no sabe cuál hay detrás.</summary>
public interface IChatBackend
{
    Task<string> CompleteAsync(string system, IReadOnlyList<ChatMessage> messages, CancellationToken ct);
}

/// <summary>La única llamada de red: POST de un JSON y respuesta JSON. Se inyecta en los tests.</summary>
public delegate Task<JsonNode> PostJsonAsync(string url, JsonNode payload, IDictionary<string, string> headers, CancellationToken ct);

/// <summary>
/// Un modelo servido por Ollama en este mismo PC, por su endpoint de chat con <c>format: json</c>
/// para que conteste un objeto JSON. Un modelo de 30B en una GPU de sobremesa tarda unos
/// segundos por turno, de ahí el tope de tres minutos.
/// </summary>
public sealed class OllamaChatBackend : IChatBackend
{
    public string Model { get; }
    public string BaseUrl { get; }
    private readonly PostJsonAsync _post;

    public OllamaChatBackend(string model = Backends.DefaultOllamaModel, string baseUrl = Backends.DefaultOllamaUrl, PostJsonAsync? post = null)
    {
        Model = model;
        BaseUrl = baseUrl.TrimEnd('/');
        _post = post ?? Backends.HttpPostAsync;
    }

    public async Task<string> CompleteAsync(string system, IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        var lista = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = system } };
        foreach (var m in messages)
        {
            var mensaje = new JsonObject { ["role"] = m.Role, ["content"] = m.Text };
            if (m.ImagePng is not null) mensaje["images"] = new JsonArray(Convert.ToBase64String(m.ImagePng));
            lista.Add(mensaje);
        }
        var payload = new JsonObject
        {
            ["model"] = Model,
            ["messages"] = lista,
            ["stream"] = false,
            ["format"] = "json",
            ["options"] = new JsonObject { ["temperature"] = 0.1, ["num_ctx"] = 16384 },
            // Algunos modelos lo ignoran y razonan igual; el <think> se quita al leer la respuesta.
            ["think"] = false,
        };
        var reply = await _post($"{BaseUrl}/api/chat", payload, new Dictionary<string, string>(), ct).ConfigureAwait(false);
        if (reply is JsonObject o && o["error"] is JsonNode error)
            throw new InvalidOperationException($"Ollama: {error}");
        return reply?["message"]?["content"]?.GetValue<string>() ?? throw new InvalidOperationException("Ollama no ha devuelto message.content");
    }
}

/// <summary>La Messages API de Claude, con la captura de pantalla como imagen cuando la hay.</summary>
public sealed class ClaudeChatBackend : IChatBackend
{
    public string Model { get; }
    public string ApiKey { get; }
    private readonly PostJsonAsync _post;

    public ClaudeChatBackend(string apiKey, string model = Backends.DefaultClaudeModel, PostJsonAsync? post = null)
    {
        ApiKey = apiKey;
        Model = model;
        _post = post ?? Backends.HttpPostAsync;
    }

    public async Task<string> CompleteAsync(string system, IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        var lista = new JsonArray();
        foreach (var m in messages)
        {
            if (m.ImagePng is null)
            {
                lista.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Text });
                continue;
            }
            lista.Add(new JsonObject
            {
                ["role"] = m.Role,
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "image",
                        ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = "image/png", ["data"] = Convert.ToBase64String(m.ImagePng) },
                    },
                    new JsonObject { ["type"] = "text", ["text"] = m.Text },
                },
            });
        }
        var payload = new JsonObject
        {
            ["model"] = Model,
            ["max_tokens"] = 1024,
            ["system"] = system,
            ["messages"] = lista,
        };
        var reply = await _post(Backends.ClaudeUrl, payload,
            new Dictionary<string, string> { ["x-api-key"] = ApiKey, ["anthropic-version"] = "2023-06-01" }, ct).ConfigureAwait(false);
        if (reply is JsonObject o && o["error"] is JsonObject error)
            throw new InvalidOperationException($"Claude: {error["message"] ?? error}");
        var sb = new StringBuilder();
        if (reply?["content"] is JsonArray bloques)
            foreach (var bloque in bloques)
                if (bloque is JsonObject b && b["text"] is JsonNode texto)
                    sb.Append(texto.GetValue<string>());
        return sb.ToString();
    }
}

public static class Backends
{
    public const string DefaultOllamaModel = "qwen3-coder:30b";
    public const string DefaultOllamaUrl = "http://localhost:11434";
    public const string DefaultClaudeModel = "claude-sonnet-5-5";
    public const string ClaudeUrl = "https://api.anthropic.com/v1/messages";

    /// <summary>
    /// <c>OLLAMA_HOST</c> es la variable del propio servidor, así que suele venir sin esquema
    /// («127.0.0.1:11434») o como dirección de escucha («0.0.0.0:11434»), que no vale para
    /// conectarse: se convierte en la url local que sí vale.
    /// </summary>
    public static string UrlOllama(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return DefaultOllamaUrl;
        var url = host.Trim();
        if (!url.Contains("://", StringComparison.Ordinal)) url = "http://" + url;
        url = url.Replace("://0.0.0.0", "://127.0.0.1", StringComparison.Ordinal)
                 .Replace("://[::]", "://localhost", StringComparison.Ordinal)
                 .Replace("://::", "://localhost", StringComparison.Ordinal);
        return url.TrimEnd('/');
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(180) };

    /// <summary>POST de un JSON y respuesta JSON; un error HTTP lleva el cuerpo en el mensaje (Ollama explica ahí qué falta).</summary>
    public static async Task<JsonNode> HttpPostAsync(string url, JsonNode payload, IDictionary<string, string> headers, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload.ToJsonString(Json.Compact), Encoding.UTF8),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        foreach (var (name, value) in headers)
            if (!request.Headers.TryAddWithoutValidation(name, value))
                request.Content.Headers.TryAddWithoutValidation(name, value);
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var cuerpo = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{url} ha contestado {(int)response.StatusCode}: {Observer.Recortar(cuerpo, 300)}");
        return JsonNode.Parse(cuerpo) ?? throw new InvalidOperationException("respuesta JSON vacía");
    }

    /// <summary>
    /// Elige el modelo: el nombre explícito, si no <c>$FAENA_LLM</c> (o <c>$TRAMOYA_LLM</c>, la de
    /// tramoya), si no Claude cuando hay <c>$ANTHROPIC_API_KEY</c> y, si no, Ollama en local. El modelo:
    /// <paramref name="model"/>, si no <c>$FAENA_LLM_MODEL</c> (o <c>$TRAMOYA_LLM_MODEL</c>), si no el de cada casa.
    /// </summary>
    public static IChatBackend Pick(string? name, string? model, IDictionary<string, string>? env = null)
    {
        env ??= Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "");
        string? Get(string key) => env.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : null;
        var fallback = Get("ANTHROPIC_API_KEY") is not null ? "claude" : "ollama";
        var chosen = (string.IsNullOrEmpty(name) ? Get("FAENA_LLM") ?? Get("TRAMOYA_LLM") : name) ?? fallback;
        model = string.IsNullOrEmpty(model) ? Get("FAENA_LLM_MODEL") ?? Get("TRAMOYA_LLM_MODEL") : model;
        switch (chosen)
        {
            case "claude":
                var key = Get("ANTHROPIC_API_KEY") ?? throw new ArgumentException("el backend claude necesita ANTHROPIC_API_KEY en el entorno");
                return new ClaudeChatBackend(key, model ?? DefaultClaudeModel);
            case "ollama":
                return new OllamaChatBackend(model ?? DefaultOllamaModel, UrlOllama(Get("OLLAMA_HOST")));
            default:
                throw new ArgumentException($"backend desconocido «{chosen}»: usa ollama o claude");
        }
    }
}
