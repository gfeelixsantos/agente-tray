namespace Cmso.Biometric.Agent.Core.Models
{
    /// <summary>
    /// Comandos enviados do servidor para o agente
    /// </summary>
    public class BiometricCommand
    {
        public string RequestId { get; set; } = string.Empty;
        public string Command { get; set; } = string.Empty; // "captura", "validacao", "cancel"
        public string EmployeeCode { get; set; } = string.Empty;
        public string EmployeeName { get; set; } = string.Empty;
        public string? Finger { get; set; } // Dedo selecionado para captura
        public byte[]? Template { get; set; } // Para validação
    }

    /// <summary>
    /// Respostas enviadas do agente para o servidor
    /// </summary>
    public class BiometricResponse
    {
        public string RequestId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty; // "started", "capturing", "success", "error"
        public string Message { get; set; } = string.Empty;
        public byte[]? Template { get; set; }
        public int QualityScore { get; set; }
        public bool? Approved { get; set; }
        public int? MatchScore { get; set; }
        public string? FingerprintImageBase64 { get; set; }
    }

    /// <summary>
    /// Status do agente
    /// </summary>
    public class AgentStatus
    {
        public string Unidade { get; set; } = string.Empty;
        public string EstacaoId { get; set; } = string.Empty;
        public string IpLocal { get; set; } = string.Empty;
        public string MachineName { get; set; } = string.Empty;
        public bool BackendConectado { get; set; }
        public bool LeitorConectado { get; set; }
        public bool LeitorAberto { get; set; }
        public string EstadoLeitor { get; set; } = string.Empty;
        public string Estado { get; set; } = string.Empty;
        public string Versao { get; set; } = "1.0.0";
        public string Timestamp { get; set; } = DateTime.UtcNow.ToString("o");
        public bool DllEncontrada { get; set; }
    }
}
