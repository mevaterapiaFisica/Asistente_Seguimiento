using System.Text.RegularExpressions;
using Meva.Rt.Core;

namespace Meva.Rt.Infrastructure.Mail;

public static partial class TbiMailParser
{
    [GeneratedRegex(@"^TBI\s+([\d\-]+)\s+(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex SubjectRegex();

    [GeneratedRegex(@"Fecha\s+de\s+Tac:?\s*(\d{1,2})/(\d{1,2})", RegexOptions.IgnoreCase)]
    private static partial Regex TomographyRegex();

    [GeneratedRegex(@"los\s+d[ií]as\s+([\d,\s y]+?)/(\d{1,2})\s+a\s+las\s+(\d{1,2})\s*[Hh]s.*?Equipo\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TreatmentRegex();

    [GeneratedRegex(@"\d+")]
    private static partial Regex FirstNumberRegex();

    // ponytail: parseo best-effort — campos que no matchean quedan null, el admin los completa a mano en la UI
    public static TbiMailInfo? Parse(string subject, string body, DateTimeOffset receivedAt)
    {
        var subjectMatch = SubjectRegex().Match(subject.Trim());
        if (!subjectMatch.Success) return null;

        var info = new TbiMailInfo
        {
            PatientId = subjectMatch.Groups[1].Value.Trim(),
            PatientName = subjectMatch.Groups[2].Value.Trim(),
            ReceivedAtUtc = receivedAt.UtcDateTime
        };

        var tacMatch = TomographyRegex().Match(body);
        if (tacMatch.Success &&
            int.TryParse(tacMatch.Groups[1].Value, out var tacDay) &&
            int.TryParse(tacMatch.Groups[2].Value, out var tacMonth))
        {
            info.TomographyDate = TryResolveDate(tacDay, tacMonth, receivedAt);
        }

        var treatmentMatch = TreatmentRegex().Match(body);
        if (treatmentMatch.Success)
        {
            var daysList = treatmentMatch.Groups[1].Value;
            var firstDayMatch = FirstNumberRegex().Match(daysList);
            if (firstDayMatch.Success &&
                int.TryParse(firstDayMatch.Value, out var startDay) &&
                int.TryParse(treatmentMatch.Groups[2].Value, out var startMonth))
            {
                info.TreatmentStartDate = TryResolveDate(startDay, startMonth, receivedAt);
            }

            info.MachineDisplayName = $"MEVA-Central - Equipo {treatmentMatch.Groups[4].Value}";
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
