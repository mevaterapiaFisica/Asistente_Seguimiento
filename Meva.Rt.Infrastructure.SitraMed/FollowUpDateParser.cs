using System.Globalization;
using System.Text.RegularExpressions;

namespace Meva.Rt.Infrastructure.SitraMed;

/// <summary>
/// Extrae la fecha de inicio de etapa desde el HTML de una fila de seguimiento de SitraMed.
///
/// Estrategia: cada etapa mapea a un comentario de sección (<!-- fX -->) y al N-ésimo
/// &lt;td&gt; dentro de esa sección. Si la fecha no existe, se retrocede por el orden
/// natural de etapas hasta encontrar una.
/// </summary>
internal static class FollowUpDateParser
{
    // dd-MM-yyyy, dd/MM/yyyy, yyyy-MM-dd, yyyy/MM/dd
    private static readonly Regex DateInCellRegex = new(
        @"\b(\d{2}[-/]\d{2}[-/]\d{4}|\d{4}[-/]\d{2}[-/]\d{2})\b",
        RegexOptions.Compiled);

    // "DD/MM/YYYY HH:MMhs - TECNICA - Estado"
    private static readonly Regex TurnoEntryRegex = new(
        @"(\d{2}/\d{2}/\d{4})\s+\d{2}:\d{2}hs\s+-\s+\S+\s+-\s+(\w+)",
        RegexOptions.Compiled);

    private static readonly Regex StripTagsRegex = new(@"<[^>]+>", RegexOptions.Compiled);

    private static readonly Regex NextSectionRegex = new(
        @"<!--\s*f\d",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ModalDivOpenRegex = new(
        @"<div\b[^>]*\bclass\s*=\s*""[^""]*modal[^""]*""[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DivTagRegex = new(
        @"<(/?)div\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Elimina bloques &lt;div class="...modal..."&gt;...&lt;/div&gt; (con balanceo de anidamiento).
    /// Necesario porque SitraMed embebe modales de historial dentro de celdas de la tabla de
    /// seguimiento; esos modales contienen sus propios &lt;td&gt;, lo que desalinea el conteo
    /// naive de columnas de <see cref="GetNthTdContent"/> (mismo patrón de bug que rompía la
    /// extracción de técnica en la celda "Comunicaciones Internas", ver PlaywrightSitraMedClient).
    /// </summary>
    private static string StripModalBlocks(string html)
    {
        var sb = new System.Text.StringBuilder();
        var pos = 0;
        while (true)
        {
            var openMatch = ModalDivOpenRegex.Match(html, pos);
            if (!openMatch.Success)
            {
                sb.Append(html, pos, html.Length - pos);
                break;
            }
            sb.Append(html, pos, openMatch.Index - pos);

            var depth = 1;
            var scan = openMatch.Index + openMatch.Length;
            while (depth > 0)
            {
                var tag = DivTagRegex.Match(html, scan);
                if (!tag.Success)
                {
                    scan = html.Length;
                    break;
                }
                depth += tag.Groups[1].Value == "/" ? -1 : 1;
                scan = tag.Index + tag.Length;
            }
            pos = scan;
        }
        return sb.ToString();
    }

    private static readonly string[] DateFormats =
        ["dd-MM-yyyy", "dd/MM/yyyy", "yyyy-MM-dd", "yyyy/MM/dd"];

    // Orden natural de etapas (usado para fallback)
    private static readonly string[] StageOrder =
    [
        "F1", "F2A", "F2B", "F3", "F4", "F4B", "F5",
        "F6A", "F6B", "F6C", "F6D", "F6E", "F6F", "F6G",
        "F7A", "F7B", "F7C", "F8", "F9", "F10", "F11"
    ];

    /// <summary>
    /// Devuelve la fecha en que el paciente entró a <paramref name="stageCode"/>.
    /// Si no existe fecha para esa etapa, retrocede por el orden natural hasta encontrar una.
    /// </summary>
    public static DateOnly? ExtractStageEntryDate(string rowHtml, string stageCode)
    {
        var code = stageCode.ToUpperInvariant();
        var idx = Array.IndexOf(StageOrder, code);
        if (idx < 0) return null;

        for (var i = idx; i >= 0; i--)
        {
            var date = ExtractDateForStage(rowHtml, StageOrder[i]);
            if (date.HasValue) return date;
        }
        return null;
    }

    private static DateOnly? ExtractDateForStage(string rowHtml, string stageCode)
    {
        var info = GetColumnInfo(stageCode);
        if (info.SectionMarker == null) return null;

        var markerIdx = rowHtml.IndexOf(info.SectionMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIdx < 0) return null;

        var afterMarker = markerIdx + info.SectionMarker.Length;
        var nextSection = NextSectionRegex.Match(rowHtml, afterMarker);
        var sectionEnd = nextSection.Success ? nextSection.Index : rowHtml.Length;
        var sectionHtml = StripModalBlocks(rowHtml.Substring(afterMarker, sectionEnd - afterMarker));

        var cellContent = GetNthTdContent(sectionHtml, info.TdIndex);
        if (cellContent == null) return null;

        return info.TurnoStatus != null
            ? ExtractTurnoDate(cellContent, info.TurnoStatus)
            : ExtractFirstDate(cellContent);
    }

    /// <summary>
    /// Extrae el contenido interno del N-ésimo &lt;td&gt; en el HTML dado (1-based).
    /// No maneja tablas anidadas; funciona correctamente mientras las celdas
    /// anteriores al target no contengan &lt;td&gt; anidados.
    /// </summary>
    private static string? GetNthTdContent(string html, int n)
    {
        var count = 0;
        var pos = 0;
        while (pos < html.Length)
        {
            var tdStart = html.IndexOf("<td", pos, StringComparison.OrdinalIgnoreCase);
            if (tdStart < 0) return null;

            var tagEnd = html.IndexOf('>', tdStart);
            if (tagEnd < 0) return null;

            count++;
            if (count == n)
            {
                var contentStart = tagEnd + 1;
                var tdClose = html.IndexOf("</td>", contentStart, StringComparison.OrdinalIgnoreCase);
                return tdClose < 0 ? null : html.Substring(contentStart, tdClose - contentStart);
            }

            pos = tagEnd + 1;
        }
        return null;
    }

    private static DateOnly? ExtractFirstDate(string cellContent)
    {
        var m = DateInCellRegex.Match(cellContent);
        return m.Success ? ParseDateOnly(m.Groups[1].Value) : null;
    }

    /// <summary>
    /// Para la columna "Turnos Asignados": extrae el turno más reciente con el estado dado
    /// (Pendiente para F4B, Atendido para F5).
    /// </summary>
    private static DateOnly? ExtractTurnoDate(string cellContent, string status)
    {
        // Limpiar tags HTML antes de matchear — SitraMed puede envolver partes del texto
        // en <span> u otros elementos, rompiendo el patrón de texto plano.
        var text = StripTagsRegex.Replace(cellContent, " ");
        text = System.Net.WebUtility.HtmlDecode(text);

        DateOnly? latest = null;
        foreach (Match m in TurnoEntryRegex.Matches(text))
        {
            if (!m.Groups[2].Value.Equals(status, StringComparison.OrdinalIgnoreCase))
                continue;
            var date = ParseDateOnly(m.Groups[1].Value);
            if (date.HasValue && (!latest.HasValue || date.Value > latest.Value))
                latest = date;
        }
        return latest;
    }

    /// <summary>
    /// Extrae la fecha del turno de tomografía de simulación (entrada "Atendido" más reciente
    /// en la columna "Turnos Asignados"). Válido para cualquier etapa — el dato existe en el
    /// HTML aunque el paciente ya haya avanzado más allá de F5.
    /// </summary>
    public static DateOnly? ExtractTomographyDate(string rowHtml)
        => ExtractDateForStage(rowHtml, "F5");

    /// <summary>
    /// Extrae la fecha de postergación del botón "Hasta: DD/MM/YYYY" en la columna
    /// "Pospuesto por paciente" (1ª &lt;td&gt; de sección &lt;!-- f4). Aplica a pacientes F4.
    /// </summary>
    public static DateOnly? ExtractPostponedUntil(string rowHtml)
    {
        var cellText = ExtractCellText(rowHtml, "<!-- f4", 1);
        if (string.IsNullOrWhiteSpace(cellText)) return null;
        var m = DateInCellRegex.Match(cellText);
        return m.Success ? ParseDateOnly(m.Groups[1].Value) : null;
    }

    /// <summary>
    /// Extrae el médico responsable: 4º &lt;td&gt; en la sección &lt;!-- f1 --&gt;
    /// (columna "Usuario" de la fila de definición de conducta).
    /// </summary>
    public static string? ExtractResponsibleDoctor(string rowHtml)
        => ExtractCellText(rowHtml, "<!-- f1", 4);

    /// <summary>
    /// Extrae la fecha de posible inicio de la sección "Conductas expectantes"
    /// (1ª &lt;td&gt;). SitraMed usa indistintamente "Expectant" y "Expectanct" (typo)
    /// según la página, por eso el marcador corta antes de esa letra ambigua.
    /// </summary>
    public static DateOnly? ExtractExpectantStartDate(string rowHtml)
    {
        var cellText = ExtractCellText(rowHtml, "<!-- Expectan", 1);
        if (string.IsNullOrWhiteSpace(cellText)) return null;
        var m = DateInCellRegex.Match(cellText);
        return m.Success ? ParseDateOnly(m.Groups[1].Value) : null;
    }

    /// <summary>
    /// Extrae las observaciones de la sección "Conductas expectantes" (2ª &lt;td&gt;).
    /// </summary>
    public static string? ExtractExpectantObservations(string rowHtml)
        => ExtractCellText(rowHtml, "<!-- Expectan", 2);

    /// <summary>
    /// Extrae el usuario de la sección "Conductas expectantes" (3ª &lt;td&gt;).
    /// </summary>
    public static string? ExtractExpectantUser(string rowHtml)
        => ExtractCellText(rowHtml, "<!-- Expectan", 3);

    private static string? ExtractCellText(string rowHtml, string sectionMarker, int tdIndex)
    {
        var markerIdx = rowHtml.IndexOf(sectionMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIdx < 0) return null;

        var afterMarker = markerIdx + sectionMarker.Length;
        var nextSection = NextSectionRegex.Match(rowHtml, afterMarker);
        var sectionEnd = nextSection.Success ? nextSection.Index : rowHtml.Length;
        var sectionHtml = StripModalBlocks(rowHtml.Substring(afterMarker, sectionEnd - afterMarker));

        var cellContent = GetNthTdContent(sectionHtml, tdIndex);
        if (string.IsNullOrWhiteSpace(cellContent)) return null;

        // Strip HTML tags and decode basic entities
        var text = System.Text.RegularExpressions.Regex.Replace(cellContent, "<[^>]+>", "").Trim();
        text = System.Net.WebUtility.HtmlDecode(text);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    public static DateOnly? ParseDateOnly(string value)
    {
        if (DateOnly.TryParseExact(value.Trim(), DateFormats,
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return parsed;
        return null;
    }

    // Mapeo etapa → (comentario de sección HTML, índice de <td> 1-based, estado turno)
    // Columnas determinadas del <thead> de la tabla de seguimiento:
    //   f0: F.PrimeraConsulta(1) Nombre(2) Institucion(3) MédicoHC(4) ComunIntern(5)
    //   f1: F.Solicitud(1) Usuario(2) F.DefConduct(3) Usuario(4)
    //   f2: F.Pedido(1) F.Recepción(2) F.Autorizac(3) F.Pospuesto(4) Acciones(5)
    //   f3: Nro.HC(1) F.Ingreso(2) Tratam-Zona(3)
    //   f4: Pospuesto(1) F.TAC(2) Contraste(3) Físico(4) Médico(5) Técnico(6)
    //       MarcóISO(7) CentroAderivar(8) TurnosAsignados(9) Acciones(10)
    //   f5: Patologia(1) Delimitado(2) F.Delimitado(3) Acciones(4)
    //   f6: Etapa(1) F.FinEtapa(2) ReplanifResp(3) F.AsignaciónResp(4) FísicoP(5)
    //       F.Realización(6) AprobaciónMédico(7) SistemaModulación(8)
    //       QAPaciente(9) Replanificación(10) AprobaciónFísico(11) Acciones(12)
    //   f7: Físico(1) F.Cálculo(2) NoCorresp(3) Acciones(4)
    //   f8: Físico(1) F.Chequeo(2) RespProtecciones(3) F.Protecciones(4)
    //   f12: F.Turno(1) Equipo(2) Acciones(3)
    //   f13: MédicoCorrección(1) FechaCorrección(2) MédicoAprueba(3) FechaOK(4)
    private record ColumnInfo(string? SectionMarker, int TdIndex, string? TurnoStatus);

    private static ColumnInfo GetColumnInfo(string stageCode) =>
        stageCode switch
        {
            "F1"  => new("<!-- f0",  1, null),
            "F2A" => new("<!-- f1",  3, null),
            "F2B" => new("<!-- f2",  1, null),
            "F3"  => new("<!-- f2",  2, null),
            "F4"  => new("<!-- f3",  2, null),
            "F4B" => new("<!-- f4",  9, "Pendiente"),
            "F5"  => new("<!-- f4",  9, "Atendido"),
            "F6A" => new("<!-- f5",  3, null),
            "F6B" => new("<!-- f6",  4, null),
            "F6C" => new("<!-- f6",  6, null),
            "F6D" => new("<!-- f6",  7, null),
            "F6E" => new("<!-- f6",  7, null),
            "F6F" => new("<!-- f6",  8, null),
            "F6G" => new("<!-- f6",  9, null),
            "F7A" => new("<!-- f7",  2, null),
            "F7B" => new("<!-- f6", 11, null),
            "F7C" => new("<!-- f6", 11, null),
            "F8"  => new("<!-- f8",  2, null),
            "F9"  => new("<!-- f8",  2, null),
            "F10" => new("<!-- f12", 1, null),
            "F11" => new("<!-- f13", 4, null),
            _     => new(null, 0, null)
        };
}
