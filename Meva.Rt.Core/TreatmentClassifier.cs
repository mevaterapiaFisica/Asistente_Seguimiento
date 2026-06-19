namespace Meva.Rt.Core;

public static class TreatmentClassifier
{
    public static string Classify(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return "Indefinido";
        var t = System.Text.RegularExpressions.Regex.Replace(rawText, @"[\s ]+", " ").Trim();

        if (t.Contains("baño de electrones", StringComparison.OrdinalIgnoreCase)) return "TSET";
        if (t.Contains("TBI", StringComparison.OrdinalIgnoreCase)
            || t.Contains("irradiacion corporal total", StringComparison.OrdinalIgnoreCase)) return "TBI";
        if (t.Contains("SBRT", StringComparison.OrdinalIgnoreCase)
            || t.Contains("radiocirugía extracraneal", StringComparison.OrdinalIgnoreCase)
            || t.Contains("radiocirugia extracraneal", StringComparison.OrdinalIgnoreCase)) return "SBRT";
        if (t.Contains("radiocirugía", StringComparison.OrdinalIgnoreCase)
            || t.Contains("radiocirugia", StringComparison.OrdinalIgnoreCase)
            || t.Contains("radiosurgery", StringComparison.OrdinalIgnoreCase)
            || t.Contains("RxCx", StringComparison.OrdinalIgnoreCase)
            || t.Equals("RC", StringComparison.OrdinalIgnoreCase)) return "RC";
        if (t.Contains("IGRT", StringComparison.OrdinalIgnoreCase)) return "IGRT";
        if (t.Contains("VMAT", StringComparison.OrdinalIgnoreCase)
            || t.Contains("arco", StringComparison.OrdinalIgnoreCase)) return "VMAT";
        if (t.Contains("IMRT", StringComparison.OrdinalIgnoreCase)
            || t.Contains("Intensidad Modulada", StringComparison.OrdinalIgnoreCase)) return "IMRT";
        if (t.Contains("BQT", StringComparison.OrdinalIgnoreCase)
            || t.Contains("braqui", StringComparison.OrdinalIgnoreCase)) return "BQT";
        if (t.Contains("IORT", StringComparison.OrdinalIgnoreCase)
            || t.Contains("intraoperatoria", StringComparison.OrdinalIgnoreCase)) return "IORT";
        return "3DC";
    }

    /// <summary>
    /// Combina la técnica de SitraMed con la modalidad e energía de ARIA en un label único.
    /// Llamar después de propagar IrradiationModality y ExactBeamEnergy desde ARIA.
    /// BeamType se usa como fallback de energía para "Electrones" y "SRS" (valores legacy).
    /// </summary>
    public static string? BuildLabel(
        string? treatmentTechnique,
        string? irradiationModality,
        string? exactBeamEnergy,
        string? beamTypeFallback = null)
    {
        var tech     = Normalize(treatmentTechnique);
        var modality = Normalize(irradiationModality);

        // ExactBeamEnergy takes priority; use BeamType fallback only for Electrones/SRS
        var energy = Normalize(exactBeamEnergy);
        if (energy == null && beamTypeFallback == "Electrones") energy = "Electrones";
        if (energy == null && beamTypeFallback == "SRS")        energy = "SRS";

        // ── Técnicas especiales ────────────────────────────────────────────────
        if (tech == "TSET") return "TSET";
        if (tech == "TBI")  return "TBI";

        if (tech == "SBRT")
        {
            if (energy == "SRS")    return "SBRT - haz SRS";
            if (modality == "VMAT") return "SBRT - VMAT";
            return "SBRT";
        }

        if (tech == "RC")
        {
            if (energy == "SRS")    return "RC - haz SRS";
            if (modality == "VMAT") return "RC - VMAT";
            return "RC";
        }

        if (tech == "IGRT")
        {
            if (modality == "IMRT") return "IGRT - estático";
            if (modality == "VMAT") return "IGRT - VMAT";
            return "IGRT";
        }

        // ── ARIA refina IMRT / 3DC de SitraMed ────────────────────────────────
        if (tech is "IMRT" or "3DC")
        {
            if (modality == "VMAT") return "VMAT";
            if (modality == "IMRT") return "IMRT - estático";
        }

        // ── Electrones ────────────────────────────────────────────────────────
        if (energy == "Electrones" && (tech == "3DC" || modality == "3DC"))
            return "3DC e-";

        // ── Alta energía con 3DC ──────────────────────────────────────────────
        if (energy is "10X" or "15X" or "18X" && (tech == "3DC" || modality == "3DC"))
            return $"3DC {energy}";

        // ── 3DC estándar (haz 6X, sin dato ARIA o ARIA confirma 3DC) ─────────
        if (tech == "3DC")
            return "3DC - 6X";

        // ── Fallback: SitraMed como label (VMAT, IMRT sin ARIA, BQT, IORT…) ──
        return tech;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) || value == "Indefinido" ? null : value;
}
