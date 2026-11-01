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
        return reply?["message"]?["content"]?.GetValue<string>() ?? throw new InvalidOperationException(Lengua.T("Ollama no ha devuelto message.content", "Ollama returned no message.content"));
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

/// <summary>
/// Any server that speaks the OpenAI chat completions API (llama-server, vLLM, LiteLLM, LM Studio...),
/// asked for a JSON object. Screenshots go as <c>image_url</c> data URIs only when <c>vision</c> is on
/// (<c>FAENA_LLM_VISION=1</c>): a text-only model behind such a server would reject them.
/// </summary>
public sealed class OpenAiChatBackend : IChatBackend
{
    public string Model { get; }
    public string BaseUrl { get; }
    public string? ApiKey { get; }
    public bool Vision { get; }
    private readonly PostJsonAsync _post;
    // Once the server has said it does not know response_format, stop sending it.
    private bool _sinFormato;

    public OpenAiChatBackend(string baseUrl, string model = Backends.DefaultOpenAiModel, string? apiKey = null, bool vision = false, PostJsonAsync? post = null)
    {
        BaseUrl = baseUrl.Trim().TrimEnd('/');
        Model = model;
        ApiKey = string.IsNullOrEmpty(apiKey) ? null : apiKey;
        Vision = vision;
        _post = post ?? Backends.HttpPostAsync;
    }

    public async Task<string> CompleteAsync(string system, IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        var url = $"{BaseUrl}/chat/completions";
        var headers = new Dictionary<string, string>();
        if (ApiKey is not null) headers["Authorization"] = $"Bearer {ApiKey}";
        JsonNode reply;
        try
        {
            reply = await _post(url, Payload(system, messages, !_sinFormato), headers, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException e) when (!_sinFormato && RechazaFormato(e.Message))
        {
            _sinFormato = true;
            reply = await _post(url, Payload(system, messages, false), headers, ct).ConfigureAwait(false);
        }
        if (reply is JsonObject o && o["error"] is JsonNode error)
            throw new InvalidOperationException($"OpenAI: {(error is JsonObject eo ? eo["message"] ?? error : error)}");
        var content = reply?["choices"]?[0]?["message"]?["content"];
        if (content is null) throw new InvalidOperationException(Lengua.T("el servidor no ha devuelto choices[0].message.content", "the server returned no choices[0].message.content"));
        return Protocol.QuitarThink(content.GetValueKind() == System.Text.Json.JsonValueKind.String ? content.GetValue<string>() : content.ToJsonString());
    }

    /// <summary>A 400/422 whose body names response_format: the server does not support it.</summary>
    internal static bool RechazaFormato(string mensaje) =>
        mensaje.Contains("response_format", StringComparison.OrdinalIgnoreCase)
        && (mensaje.Contains(" 400", StringComparison.Ordinal) || mensaje.Contains(" 422", StringComparison.Ordinal));

    internal JsonObject Payload(string system, IReadOnlyList<ChatMessage> messages, bool formato)
    {
        var lista = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = system } };
        foreach (var m in messages)
        {
            if (m.ImagePng is null || !Vision)
            {
                lista.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Text });
                continue;
            }
            lista.Add(new JsonObject
            {
                ["role"] = m.Role,
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = m.Text },
                    new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + Convert.ToBase64String(m.ImagePng) },
                    },
                },
            });
        }
        var payload = new JsonObject
        {
            ["model"] = Model,
            ["messages"] = lista,
            ["temperature"] = 0.1,
            ["max_tokens"] = 1024,
        };
        if (formato) payload["response_format"] = new JsonObject { ["type"] = "json_object" };
        return payload;
    }
}

public static class Backends
{
    public const string DefaultOpenAiModel = "default";
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
            throw new InvalidOperationException(Lengua.T($"{url} ha contestado {(int)response.StatusCode}: {Observer.Recortar(cuerpo, 300)}", $"{url} answered {(int)response.StatusCode}: {Observer.Recortar(cuerpo, 300)}"));
        return JsonNode.Parse(cuerpo) ?? throw new InvalidOperationException(Lengua.T("respuesta JSON vacía", "empty JSON answer"));
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
                var key = Get("ANTHROPIC_API_KEY") ?? throw new ArgumentException(Lengua.T("el backend claude necesita ANTHROPIC_API_KEY en el entorno (o la clave en los ajustes)", "the claude backend needs ANTHROPIC_API_KEY in the environment (or the key in Settings)"));
                return new ClaudeChatBackend(key, model ?? DefaultClaudeModel);
            case "ollama":
                return new OllamaChatBackend(model ?? DefaultOllamaModel, UrlOllama(Get("OLLAMA_HOST")));
            case "openai":
                // Any OpenAI-compatible server: its base url (…/v1) is required, the key is optional.
                var baseUrl = Get("FAENA_LLM_URL") ?? throw new ArgumentException(Lengua.T("el backend openai necesita FAENA_LLM_URL en el entorno o el servidor en los ajustes (p. ej. http://llm:8080/v1)", "the openai backend needs FAENA_LLM_URL in the environment or the server in Settings (e.g. http://llm:8080/v1)"));
                return new OpenAiChatBackend(baseUrl, model ?? DefaultOpenAiModel, Get("FAENA_LLM_KEY"), Get("FAENA_LLM_VISION") == "1");
            default:
                throw new ArgumentException(Lengua.T($"backend desconocido «{chosen}»: usa ollama, claude u openai", $"unknown backend “{chosen}”: use ollama, claude or openai"));
        }
    }
}
