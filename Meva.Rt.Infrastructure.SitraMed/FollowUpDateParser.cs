using System.Globalization;
using System.Text.RegularExpressions;

namespace Meva.Rt.Infrastructure.SitraMed;

/// <summary>
/// Extrae la fecha de inicio de etapa desde el HTML de una fila de seguimiento de SitraMed.
/// La tabla muestra todas las etapas en columnas. Los comentarios HTML delimitan cada sección
/// (p. ej. &lt;!-- f5 Marcación --&gt;). La fecha de inicio de la etapa actual = primera fecha
/// en la sección de la etapa PREVIA (cuando esa etapa se completó).
/// </summary>
internal static class FollowUpDateParser
{
    private static readonly Regex PureDateTdRegex = new(
        @"<td[^>]*>\s*(\d{2}[-/]\d{2}[-/]\d{4})\s*</td>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NextFCommentRegex = new(
        @"<!--\s*f\d",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] DateFormats =
        ["dd-MM-yyyy", "dd/MM/yyyy", "yyyy-MM-dd", "yyyy/MM/dd"];

    /// <summary>
    /// Devuelve la fecha en que el paciente entró a <paramref name="stageCode"/>
    /// buscando el primer &lt;td&gt; con fecha pura en la sección HTML de la etapa anterior.
    /// </summary>
    public static DateOnly? ExtractStageEntryDate(string rowHtml, string stageCode)
    {
        var sectionMarker = GetPrecedingSectionMarker(stageCode);
        if (sectionMarker == null) return null;

        var markerIdx = rowHtml.IndexOf(sectionMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIdx < 0) return null;

        // Acotar la sección hasta el siguiente comentario de etapa
        var afterMarker = markerIdx + sectionMarker.Length;
        var nextComment = NextFCommentRegex.Match(rowHtml, afterMarker);
        var sectionEnd = nextComment.Success ? nextComment.Index : rowHtml.Length;
        var section = rowHtml.Substring(markerIdx, sectionEnd - markerIdx);

        // Primera <td> que contiene SOLO una fecha (no texto adicional)
        var m = PureDateTdRegex.Match(section);
        return m.Success ? ParseDateOnly(m.Groups[1].Value) : null;
    }

    /// <summary>
    /// Retorna el marcador HTML del comentario de la sección cuyo FIN coincide con el
    /// INICIO de <paramref name="stageCode"/>.
    /// </summary>
    private static string? GetPrecedingSectionMarker(string stageCode) =>
        stageCode.ToUpperInvariant() switch
        {
            // F3 Ingreso: la fecha es "F. Ingreso" en la propia sección f3
            "F3" => "<!-- f3",
            // F4 TurnoTomosim: empieza cuando termina F3 (F. Ingreso)
            "F4" => "<!-- f3",
            // F5 Marcación: empieza cuando termina F4 (F. TAC)
            "F5" => "<!-- f4",
            // F6A Planificación: empieza cuando termina F5 (F. Delimitado)
            "F6A" => "<!-- f5",
            // F6B+ (Asignación Físico en adelante): empieza cuando termina F6A (F. Asignación Resp.)
            // La primera fecha pura en la sección <!-- f6 es exactamente esa fecha.
            "F6B" or "F6C" or "F6F" or "F6G" => "<!-- f6",
            // F7x: empieza cuando termina F6 (Aprobación / F. Fin Etapa)
            "F7A" or "F7C" => "<!-- f6",
            _ => null
        };

    public static DateOnly? ParseDateOnly(string value)
    {
        if (DateOnly.TryParseExact(value.Trim(), DateFormats,
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return parsed;
        return null;
    }
}
