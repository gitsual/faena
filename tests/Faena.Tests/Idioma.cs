using Faena.Agente;

namespace Faena.Tests;

/// <summary>
/// The language a test class runs in. Every class sets its own in its constructor (the tests run
/// one class at a time, see AssemblyInfo), so none depends on the order: the older classes check
/// the Spanish texts, the newer ones the English ones. The Settings file points at a folder that
/// does not exist, so the real one of this computer never leaks in.
/// </summary>
public sealed class Idioma : IDisposable
{
    public Idioma(string lang)
    {
        Environment.SetEnvironmentVariable(Lengua.Variable, lang);
        Environment.SetEnvironmentVariable("FAENA_AJUSTES", Path.Combine(Path.GetTempPath(), "faena-sin-ajustes-" + Guid.NewGuid().ToString("N"), "ajustes.json"));
        Lengua.Actual = lang;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Lengua.Variable, null);
        Environment.SetEnvironmentVariable("FAENA_AJUSTES", null);
        Lengua.Actual = "en";
    }
}
