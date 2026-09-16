using System.Text.RegularExpressions;
using Meva.Rt.Core;

namespace Meva.Rt.Infrastructure.Mail;

public static partial class TbiMailParser
{
    // "TBI {HC} {Nombre}" — formato usual.
    [GeneratedRegex(@"^TBI\s+(?<id>\d[\d\-]*)\s+(?<name>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex SubjectIdFirstRegex();

    // "TBI {Nombre} HC: {HC}" — variante con HC/nombre invertidos.
    [GeneratedRegex(@"^TBI\s+(?<name>.+?)\s+HC:?\s*(?<id>\d[\d\-]*)$", RegexOptions.IgnoreCase)]
    private static partial Regex SubjectHcSuffixRegex();

    // Admite "Fecha de Tac" y "Turno de Tac", con texto de relleno entre la etiqueta y la fecha
    // (ej. "Fecha de Tac: hará hoy 21/5").
    [GeneratedRegex(@"(?:Fecha|Turno)\s+de\s+Tac:?[^\d\n]{0,20}(\d{1,2})/(\d{1,2})", RegexOptions.IgnoreCase)]
    private static partial Regex TomographyRegex();

    // Día(s) de inicio de tratamiento: "los días 14, 15 y 16/9" (varios) o "el 4/6" (uno solo).
    [GeneratedRegex(@"(?:los\s+d[ií]as\s+(?<days>[\d,\s y]+?)|\bel\s+(?<day>\d{1,2}))\s*/\s*(?<month>\d{1,2})", RegexOptions.IgnoreCase)]
    private static partial Regex StartDateRegex();

    [GeneratedRegex(@"en\s+Equipo\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex EquipoRegex();

    [GeneratedRegex(@"a\s+las\s+(\d{1,2})(?::(\d{2}))?\s*[Hh]s", RegexOptions.IgnoreCase)]
    private static partial Regex TimeSlotRegex();

    [GeneratedRegex(@"\d+")]
    private static partial Regex FirstNumberRegex();

    // ponytail: parseo best-effort — campos que no matchean quedan null, el admin los completa a mano en la UI
    public static TbiMailFetchResult? Parse(string subject, string body, DateTimeOffset receivedAt)
    {
        var trimmedSubject = subject.Trim();
        var subjectMatch = SubjectIdFirstRegex().Match(trimmedSubject);
        if (!subjectMatch.Success) subjectMatch = SubjectHcSuffixRegex().Match(trimmedSubject);
        if (!subjectMatch.Success) return null; // sin HC reconocible en el asunto — no se puede matchear contra un paciente

        var info = new TbiMailFetchResult
        {
            PatientId = subjectMatch.Groups["id"].Value.Trim(),
            PatientName = subjectMatch.Groups["name"].Value.Trim(),
            ReceivedAtUtc = receivedAt.UtcDateTime
        };

        var tacMatch = TomographyRegex().Match(body);
        if (tacMatch.Success &&
            int.TryParse(tacMatch.Groups[1].Value, out var tacDay) &&
            int.TryParse(tacMatch.Groups[2].Value, out var tacMonth))
        {
            info.TomographyDate = TryResolveDate(tacDay, tacMonth, receivedAt);
        }

        var dateMatch = StartDateRegex().Match(body);
        if (dateMatch.Success && int.TryParse(dateMatch.Groups["month"].Value, out var startMonth))
        {
            int daysCount;
            int? startDay;
            if (dateMatch.Groups["days"].Success)
            {
                var daysList = dateMatch.Groups["days"].Value;
                var firstDayMatch = FirstNumberRegex().Match(daysList);
                startDay = firstDayMatch.Success ? int.Parse(firstDayMatch.Value) : null;
                daysCount = FirstNumberRegex().Matches(daysList).Count;
            }
            else
            {
                startDay = int.Parse(dateMatch.Groups["day"].Value);
                daysCount = 1;
            }

            if (startDay is not null)
                info.TreatmentStartDate = TryResolveDate(startDay.Value, startMonth, receivedAt);

            var equipoMatch = EquipoRegex().Match(body);
            if (equipoMatch.Success)
                info.MachineDisplayName = $"MEVA-Central - Equipo {equipoMatch.Groups[1].Value}";

            var timeSlots = TimeSlotRegex().Matches(body);
            if (timeSlots.Count > 0)
            {
                var first = timeSlots[0];
                var minute = first.Groups[2].Success ? first.Groups[2].Value : "00";
                info.TreatmentStartTime = $"{int.Parse(first.Groups[1].Value):D2}:{minute}";
                info.TotalApplications = daysCount * timeSlots.Count;
            }
        }

        return info;
    }

    private static DateOnly? TryResolveDate(int day, int month, DateTimeOffset receivedAt)
    {
        if (month is < 1 or > 12) return null;
        var year = receivedAt.Year;
        var monthDiff = month - receivedAt.Month;
        if (monthDiff > 6) year--;
        else if (monthDiff < -6) year++;
        try { return new DateOnly(year, month, day); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
