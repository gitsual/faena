using System.Text.RegularExpressions;

namespace Faena.Agente;

/// <summary>
/// Lo que no tiene vuelta atrás. En modo tarea, antes de cada acción se mira si el botón que se va
/// a pulsar se llama como algo irreversible (enviar, borrar, pagar, aceptar…) o si el propio
/// modelo lo ha marcado con <c>"irreversible": true</c>; las dos cosas, porque un modelo pequeño
/// se olvida a veces y un nombre como «Continuar» puede esconder un pago.
/// </summary>
public static class Guardian
{
    /// <summary>Cada categoría con las palabras que la delatan (palabras enteras del nombre).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Categorias = new Dictionary<string, string[]>
    {
        ["enviar"] = ["enviar", "envía", "envia", "envíalo", "mandar", "responder", "publicar", "send", "submit", "publish", "post"],
        ["borrar"] = ["borrar", "borra", "eliminar", "elimina", "suprimir", "vaciar", "delete", "remove", "erase", "purge"],
        ["pagar"] = ["pagar", "comprar", "tramitar", "realizar pedido", "finalizar compra", "pay", "buy", "checkout", "place order"],
        ["aceptar"] = ["aceptar", "confirmar", "aprobar", "firmar", "validar", "autorizar", "imputar", "transferir", "accept", "confirm", "approve", "sign", "authorize", "transfer"],
    };

    private static readonly (string Categoria, Regex Patron)[] Patrones = Categorias
        .Select(c => (c.Key, new Regex(@"(?<!\p{L})(" + string.Join("|", c.Value.Select(Regex.Escape)) + @")(?!\p{L})",RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
        .ToArray();

    /// <summary>La categoría que delata el nombre de un control, o null si parece inofensivo.</summary>
    public static string? Categoria(string nombre)
    {
        if (string.IsNullOrWhiteSpace(nombre) || Palabras.Cookies.IsMatch(nombre)) return null;
        foreach (var (categoria, patron) in Patrones)
            if (patron.IsMatch(nombre)) return categoria;
        return null;
    }

    /// <summary>Si la decisión del modelo sobre esta observación es irreversible, qué es; si no, null.</summary>
    public static Irreversible? Detectar(Observation obs, Decision d)
    {
        if (d.Accion is "terminar" or "fallo" or "esperar" or "señalar" or "ir") return null;
        var el = d.Ref is int n ? obs.Elemento(n) : null;
        string descripcion;
        string? categoria = null;
        switch (d.Accion)
        {
            case "clic" when el is not null:
                descripcion = $"Pulsar {el.Tipo}{(el.Nombre.Length != 0 ? $" «{el.Nombre}»" : "")}";
                categoria = Categoria(el.Nombre);
                break;
            case "elegir" when el is not null:
                descripcion = $"Elegir «{d.Valor}» en {el.Tipo}{(el.Nombre.Length != 0 ? $" «{el.Nombre}»" : "")}";
                break;
            case "tecla":
                descripcion = $"Pulsar la tecla {Palabras.Tecla(d.Valor)}";
                break;
            case "escribir" when el is not null:
                // Escribir no envía nada por sí solo; solo cuenta si el modelo dice que sí.
                descripcion = $"Escribir «{d.Valor}» en {el.Tipo}{(el.Nombre.Length != 0 ? $" «{el.Nombre}»" : "")}";
                break;
            default:
                return null;
        }
        if (categoria is null && !d.Irreversible) return null;
        return new Irreversible(categoria ?? "irreversible", descripcion, d.Pensamiento);
    }

    /// <summary>Si la persona ha autorizado esta categoría de antemano («*» = todo).</summary>
    public static bool Permitida(Irreversible accion, IReadOnlyCollection<string> permitidas) =>
        permitidas.Any(p => p.Trim() == "*" || string.Equals(p.Trim(), accion.Categoria, StringComparison.OrdinalIgnoreCase));
}
