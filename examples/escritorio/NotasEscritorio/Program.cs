// Notas: una ventana mínima con la que el agente de escritorio de faena se prueba.
// Campos «Título» y «Texto», botón «Añadir», la lista de notas, un «Borrar todo» que pregunta
// (es lo irreversible de la demo) y una etiqueta con el número de notas.
using System.Windows.Forms;

ApplicationConfiguration.Initialize();
Application.Run(new Notas());

sealed class Notas : Form
{
    readonly TextBox titulo = new() { Name = "titulo", AccessibleName = "Título", Left = 80, Top = 12, Width = 280 };
    readonly TextBox texto = new() { Name = "texto", AccessibleName = "Texto", Left = 80, Top = 42, Width = 280 };
    readonly ListBox lista = new() { Name = "lista", AccessibleName = "Notas", Left = 12, Top = 110, Width = 348, Height = 160 };
    readonly Label cuenta = new() { Name = "cuenta", Left = 12, Top = 280, Width = 348, Text = "0 notas" };

    public Notas()
    {
        Text = "Notas";
        ClientSize = new Size(372, 340);
        var anadir = new Button { Name = "anadir", Text = "Añadir", Left = 12, Top = 74, Width = 100 };
        var borrar = new Button { Name = "borrar", Text = "Borrar todo", Left = 260, Top = 300, Width = 100 };
        anadir.Click += (_, _) =>
        {
            if (titulo.Text.Trim().Length == 0) return;
            lista.Items.Add(texto.Text.Trim().Length != 0 ? $"{titulo.Text.Trim()}: {texto.Text.Trim()}" : titulo.Text.Trim());
            titulo.Text = texto.Text = "";
            Contar();
        };
        borrar.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "¿Borrar todas las notas?", "Notas", MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                lista.Items.Clear();
                Contar();
            }
        };
        Controls.AddRange([
            new Label { Text = "Título", Left = 12, Top = 15, Width = 60 }, titulo,
            new Label { Text = "Texto", Left = 12, Top = 45, Width = 60 }, texto,
            anadir, lista, cuenta, borrar,
        ]);
    }

    void Contar() => cuenta.Text = lista.Items.Count == 1 ? "1 nota" : $"{lista.Items.Count} notas";
}
