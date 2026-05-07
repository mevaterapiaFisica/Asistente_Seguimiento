namespace Meva.Rt.Infrastructure.Aria;

/// <summary>
/// Provee la raíz del modelo ARIA con propiedad Patients (consumida por MetodosParaWebScrap.BuscarPaciente).
/// Implementación por defecto sin conexión; reemplazar en DI cuando exista sesión ARIA real.
/// </summary>
public interface IAriaPatientRootProvider
{
    object? TryGetPatientRoot();
}

public sealed class NullAriaPatientRootProvider : IAriaPatientRootProvider
{
    public object? TryGetPatientRoot() => null;
}
