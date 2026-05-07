Meva.Rt

Solucion nueva para:
- agenda real de equipos
- seguimiento de pacientes por etapa
- integracion con ARIA

Proyectos:
- Meva.Rt.Core
- Meva.Rt.Application
- Meva.Rt.Infrastructure.SitraMed
- Meva.Rt.Infrastructure.Aria
- Meva.Rt.Infrastructure.Storage
- Meva.Rt.Web

Estado actual:
- esqueleto inicial compilable
- web minima con datos demo
- adapter inicial para ARIA
- estructura preparada para migrar Selenium a Playwright

Siguiente implementacion:
1. Reemplazar stubs de SitraMed por extractor real
2. Resolver scraping de seguimiento por centro + microestado
3. Conectar ARIA real por PatientId
4. Persistir snapshots reales en JSON
