using Meva.Rt.Core;

namespace Meva.Rt.Web;

public static class AppConfiguration
{
    public static RtSystemConfiguration BuildDefault()
    {
        return new RtSystemConfiguration
        {
            Centers =
            {
                new RtCenter { Id = "central",   Name = "MEVA-Central" },
                new RtCenter { Id = "cetro",     Name = "CETRO" },
                new RtCenter { Id = "quilmes",   Name = "QUILMES" },
                new RtCenter { Id = "sanjusto",  Name = "SAN JUSTO" },
                new RtCenter { Id = "rtmedrano",  Name = "RT MEDRANO" },
                new RtCenter { Id = "viamonte",   Name = "MEVA-Viamonte", AriaEnabled = false }
            },
            Machines =
            {
                // MEVA-Central
                new RtMachine { CenterName = "MEVA-Central", SitraName = "Equipo 1",           AriaName = "Equipo1",       DisplayName = "MEVA-Central - Equipo 1" },
                new RtMachine { CenterName = "MEVA-Central", SitraName = "Equipo 2",           AriaName = "Equipo 2 6EX",  DisplayName = "MEVA-Central - Equipo 2" },
                new RtMachine { CenterName = "MEVA-Central", SitraName = "Equipo 3",           AriaName = "Equipo3",       DisplayName = "MEVA-Central - Equipo 3" },
                new RtMachine { CenterName = "MEVA-Central", SitraName = "Equipo 4",           AriaName = "D-2300CD",      DisplayName = "MEVA-Central - Equipo 4" },
                // CETRO
                new RtMachine { CenterName = "CETRO",        SitraName = "Cetro",              AriaName = "PBA_6EX_730",   DisplayName = "CETRO - Cetro" },
                // QUILMES
                new RtMachine { CenterName = "QUILMES",      SitraName = "Quilmes - Equipo 1", AriaName = "QBA_600CD_523", DisplayName = "QUILMES - Equipo 1" },
                new RtMachine { CenterName = "QUILMES",      SitraName = "Quilmes - Equipo 2", AriaName = "EQ2_iX_827",    DisplayName = "QUILMES - Equipo 2" },
                // SAN JUSTO
                new RtMachine { CenterName = "SAN JUSTO",    SitraName = "San Justo - Equipo 2", AriaName = "Varian 21 EX", DisplayName = "SAN JUSTO - Equipo 2" },
                // RT MEDRANO
                new RtMachine { CenterName = "RT MEDRANO",    SitraName = "RT Medrano",              AriaName = "CL21EX", DisplayName = "RT MEDRANO - RT Medrano" },
                // MEVA-Viamonte
                new RtMachine { CenterName = "MEVA-Viamonte", SitraName = "Viamonte - Equipo 1",     AriaName = "",       DisplayName = "MEVA-Viamonte - Equipo 1" },
                new RtMachine { CenterName = "MEVA-Viamonte", SitraName = "Viamonte - Equipo 2",     AriaName = "",       DisplayName = "MEVA-Viamonte - Equipo 2" }
            },
            Stages =
            {
                new ProcessStageDefinition { Code = "F1",  SitraMicroStatus = "planification_request",      DisplayName = "Solicitud de Planificacion",    GroupName = "Solicitud",         ExpectedDays = 2, SortOrder =  1 },
                new ProcessStageDefinition { Code = "F2A", SitraMicroStatus = "auth_order_ingress",          DisplayName = "Orden de Autorizacion Pedida",  GroupName = "Autorizacion",      ExpectedDays = 3, SortOrder =  5 },
                new ProcessStageDefinition { Code = "F2B", SitraMicroStatus = "auth_order_receive",          DisplayName = "Orden de Autorizacion Recibida", GroupName = "Autorizacion",     ExpectedDays = 2, SortOrder =  6 },
                new ProcessStageDefinition { Code = "F3",  SitraMicroStatus = "ingress",                    DisplayName = "Ingreso",                      GroupName = "Ingreso",           ExpectedDays = 2, SortOrder = 10 },
                new ProcessStageDefinition { Code = "F4",  SitraMicroStatus = "no_tomograph_appointment",   DisplayName = "Turno de Tomosimulacion",       GroupName = "Tomosimulacion",    ExpectedDays = 3, SortOrder = 20 },
                new ProcessStageDefinition { Code = "F4B", SitraMicroStatus = "tomograph",                   DisplayName = "Tomosimulacion",               GroupName = "Tomosimulacion",    ExpectedDays = 1, SortOrder = 25 },
                new ProcessStageDefinition { Code = "F5",  SitraMicroStatus = "marking",                    DisplayName = "Marcacion",                    GroupName = "Marcacion",         ExpectedDays = 2, SortOrder = 30 },
                new ProcessStageDefinition { Code = "F6A", SitraMicroStatus = "planification_assign",       DisplayName = "Asignacion Planificacion",      GroupName = "Planificacion",     ExpectedDays = 2, SortOrder = 40 },
                new ProcessStageDefinition { Code = "F6B", SitraMicroStatus = "planification_waiting",      DisplayName = "Esperando Planificacion",       GroupName = "Planificacion",     ExpectedDays = 2, SortOrder = 41 },
                new ProcessStageDefinition { Code = "F6C", SitraMicroStatus = "planification_medic_ok",     DisplayName = "Aprobacion Medica",             GroupName = "Aprobacion Medica", ExpectedDays = 2, SortOrder = 50 },
                new ProcessStageDefinition { Code = "F6D", SitraMicroStatus = "modulation",                  DisplayName = "Sistema Modulacion",           GroupName = "Modulacion",        ExpectedDays = 3, SortOrder = 55 },
                new ProcessStageDefinition { Code = "F6E", SitraMicroStatus = "waiting_mod_reception",       DisplayName = "Recepcion Modulacion",          GroupName = "Modulacion",        ExpectedDays = 2, SortOrder = 56 },
                new ProcessStageDefinition { Code = "F6F", SitraMicroStatus = "qa",                         DisplayName = "Control de Calidad",            GroupName = "Control Calidad",   ExpectedDays = 1, SortOrder = 60 },
                new ProcessStageDefinition { Code = "F6G", SitraMicroStatus = "independent_calculation",    DisplayName = "Calculo Independiente",         GroupName = "Aprobacion Fisica", ExpectedDays = 1, SortOrder = 70 },
                new ProcessStageDefinition { Code = "F7A", SitraMicroStatus = "physicist_approval",         DisplayName = "Aprobacion Fisico",             GroupName = "Aprobacion Fisica", ExpectedDays = 1, SortOrder = 71 },
                new ProcessStageDefinition { Code = "F7B", SitraMicroStatus = "proteccion_confection",       DisplayName = "Confeccion de Plomos",          GroupName = "Plomos",            ExpectedDays = 1, SortOrder = 75 },
                new ProcessStageDefinition { Code = "F7C", SitraMicroStatus = "general_check",              DisplayName = "Chequeo General",               GroupName = "Chequeo General",   ExpectedDays = 1, SortOrder = 80 },
                new ProcessStageDefinition { Code = "F8",  SitraMicroStatus = "localization_plate",          DisplayName = "Placa Localizadora",            GroupName = "Placa Localizadora", ExpectedDays = 1, SortOrder = 90 },
                new ProcessStageDefinition { Code = "F9",  SitraMicroStatus = "verification_plate_pending",  DisplayName = "Placa Verificadora",            GroupName = "Placa Verificadora", ExpectedDays = 2, SortOrder = 100 },
                new ProcessStageDefinition { Code = "F10", SitraMicroStatus = "verification_plate_ok_medic", DisplayName = "Ok Medico",                    GroupName = "Placa Verificadora", ExpectedDays = 1, SortOrder = 110 },
                new ProcessStageDefinition { Code = "F11", SitraMicroStatus = "machine_appointment",         DisplayName = "Turno Equipo",                  GroupName = "Turno Equipo",      ExpectedDays = 2, SortOrder = 120 }
            },
            MachineCapacities =
            {
                new MachineCapacitySetting { CenterName = "MEVA-Central", MachineName = "MEVA-Central - Equipo 1",    WorkingHours = 12, StandardSlotMinutes = 15, ReservedSpecialHours = 1 },
                new MachineCapacitySetting { CenterName = "MEVA-Central", MachineName = "MEVA-Central - Equipo 2",    WorkingHours = 12, StandardSlotMinutes = 15, ReservedSpecialHours = 1 },
                new MachineCapacitySetting { CenterName = "MEVA-Central", MachineName = "MEVA-Central - Equipo 3",    WorkingHours = 12, StandardSlotMinutes = 15, ReservedSpecialHours = 0 },
                new MachineCapacitySetting { CenterName = "MEVA-Central", MachineName = "MEVA-Central - Equipo 4",    WorkingHours = 12, StandardSlotMinutes = 15, ReservedSpecialHours = 0 },
                new MachineCapacitySetting { CenterName = "CETRO",        MachineName = "CETRO - Cetro",              WorkingHours = 10, StandardSlotMinutes = 15, ReservedSpecialHours = 0 },
                new MachineCapacitySetting { CenterName = "QUILMES",      MachineName = "QUILMES - Equipo 1",         WorkingHours = 10, StandardSlotMinutes = 15, ReservedSpecialHours = 1 },
                new MachineCapacitySetting { CenterName = "QUILMES",      MachineName = "QUILMES - Equipo 2",         WorkingHours = 10, StandardSlotMinutes = 15, ReservedSpecialHours = 0 },
                new MachineCapacitySetting { CenterName = "SAN JUSTO",    MachineName = "SAN JUSTO - Equipo 2",       WorkingHours = 10, StandardSlotMinutes = 15, ReservedSpecialHours = 0 },
                new MachineCapacitySetting { CenterName = "RT MEDRANO",    MachineName = "RT MEDRANO - RT Medrano",       WorkingHours = 10, StandardSlotMinutes = 15, ReservedSpecialHours = 0 },
                new MachineCapacitySetting { CenterName = "MEVA-Viamonte", MachineName = "MEVA-Viamonte - Equipo 1",    WorkingHours = 10, StandardSlotMinutes = 15, ReservedSpecialHours = 0 },
                new MachineCapacitySetting { CenterName = "MEVA-Viamonte", MachineName = "MEVA-Viamonte - Equipo 2",    WorkingHours = 10, StandardSlotMinutes = 15, ReservedSpecialHours = 0 }
            },
            MachineCapabilities =
            {
                new RtMachineCapabilities { MachineName = "MEVA-Central - Equipo 1", CanDoVMAT = true, CanDoSBRT = true, CanDoRC = true, CanDoIGRT = true, HighEnergyBeams = ["6X"] },
                new RtMachineCapabilities { MachineName = "MEVA-Central - Equipo 2", HighEnergyBeams = ["6X"] },
                new RtMachineCapabilities { MachineName = "MEVA-Central - Equipo 3", CanDoElectrons = true, CanDoTSET = true, HighEnergyBeams = ["6X"] },
                new RtMachineCapabilities { MachineName = "MEVA-Central - Equipo 4", HighEnergyBeams = ["6X"] },
                new RtMachineCapabilities { MachineName = "CETRO - Cetro", CanDoVMAT = true, CanDoIGRT = true, HighEnergyBeams = ["6X"] },
                new RtMachineCapabilities { MachineName = "QUILMES - Equipo 1", CanDoVMAT = true, CanDoIGRT = true, HighEnergyBeams = ["6X"] },
                new RtMachineCapabilities { MachineName = "QUILMES - Equipo 2", CanDoVMAT = true, HighEnergyBeams = ["6X"] },
                new RtMachineCapabilities { MachineName = "SAN JUSTO - Equipo 2", HighEnergyBeams = ["6X"] },
                new RtMachineCapabilities { MachineName = "RT MEDRANO - RT Medrano", CanDoVMAT = true, CanDoIGRT = true, HighEnergyBeams = ["6X"] },
                new RtMachineCapabilities { MachineName = "MEVA-Viamonte - Equipo 1", HighEnergyBeams = ["6X"] },
                new RtMachineCapabilities { MachineName = "MEVA-Viamonte - Equipo 2", HighEnergyBeams = ["6X"] }
            },
            Tomographs =
            {
                new RtTomograph { CenterName = "MEVA-Central",  SitraName = "Tomógrafo",        DisplayName = "MEVA-Central - Tomografo" },
                new RtTomograph { CenterName = "MEVA-Viamonte", SitraName = "Tomografo Viamonte",  DisplayName = "MEVA-Viamonte - Tomografo" },
                new RtTomograph { CenterName = "QUILMES",        SitraName = "Tomografo Quilmes",   DisplayName = "QUILMES - Tomografo" },
                new RtTomograph { CenterName = "SAN JUSTO",      SitraName = "Tomografo San Justo", DisplayName = "SAN JUSTO - Tomografo" }
            },
            TomographCapacities =
            {
                new MachineCapacitySetting { CenterName = "MEVA-Central",  MachineName = "MEVA-Central - Tomografo",  WorkingHours = 8, StandardSlotMinutes = 30, ReservedSpecialHours = 0 },
                new MachineCapacitySetting { CenterName = "MEVA-Viamonte", MachineName = "MEVA-Viamonte - Tomografo", WorkingHours = 8, StandardSlotMinutes = 30, ReservedSpecialHours = 0 },
                new MachineCapacitySetting { CenterName = "QUILMES",        MachineName = "QUILMES - Tomografo",       WorkingHours = 8, StandardSlotMinutes = 30, ReservedSpecialHours = 0 },
                new MachineCapacitySetting { CenterName = "SAN JUSTO",      MachineName = "SAN JUSTO - Tomografo",    WorkingHours = 8, StandardSlotMinutes = 30, ReservedSpecialHours = 0 }
            }
        };
    }
}
