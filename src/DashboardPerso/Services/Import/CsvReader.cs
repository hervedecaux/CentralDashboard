using System.Text;

namespace DashboardPerso.Services.Import;

/// <summary>
/// Lecteur CSV minimal : séparateur configurable, champs entre guillemets
/// pouvant contenir le séparateur, des sauts de ligne et des "" échappés.
/// </summary>
public static class CsvReader
{
    public static List<List<string>> Parse(string text, char separator = ';')
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == separator) { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\r') { /* ignoré : géré avec \n */ }
            else if (c == '\n') { row.Add(field.ToString()); field.Clear(); rows.Add(row); row = []; }
            else field.Append(c);
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }
}
