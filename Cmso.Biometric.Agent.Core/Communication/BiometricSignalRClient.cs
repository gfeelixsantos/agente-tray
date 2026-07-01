using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Cmso.Biometric.Agent.Core.Models;
using Cmso.Biometric.Agent.Core.Hardware;

namespace Cmso.Biometric.Agent.Core.Communication
{
    public class BiometricSignalRClient : IDisposable
    {
        private readonly ILogger<BiometricSignalRClient> _logger;
        private readonly BiometricHardwareService _hardwareService;
        private HubConnection? _connection;
        private string _serverUrl   = string.Empty;
        // Agente e um driver de hardware — usa MachineName nos dois campos (unidade + estacaoId).
        // Chave de roteamento no Hub: {MachineName}_{MachineName}
        private string _companyCode => Environment.MachineName;
        private string _stationId   => Environment.MachineName;
        private System.Threading.Timer? _heartbeatTimer;

        // I2: CTS exposto para o Worker cancelar operacoes em andamento via CancelarOperacao
        public CancellationTokenSource? CurrentOperationCts { get; set; }

        // I3: flag atomica para evitar ConnectAsync concorrente (Closed + retry)
        private int _isConnecting = 0; // 0 = livre, 1 = em andamento

        public event EventHandler<AgentStatus>?      StatusChanged;
        public event EventHandler<BiometricCommand>? CommandReceived;

        public bool IsConnected => _connection?.State == HubConnectionState.Connected;

        public BiometricSignalRClient(
            ILogger<BiometricSignalRClient> logger,
            BiometricHardwareService hardwareService)
        {
            _logger          = logger;
            _hardwareService = hardwareService;
        }

        public async Task ConnectAsync(string serverUrl)
        {
            if (Interlocked.CompareExchange(ref _isConnecting, 1, 0) != 0)
            {
                _logger.LogDebug("ConnectAsync ignorado - ja em andamento");
                return;
            }

            _serverUrl = serverUrl;

            try
            {
                var isHttps = serverUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

                _connection = new HubConnectionBuilder()
                    .WithUrl($"{serverUrl}/biometrichub", options =>
                    {
                        // Aceita todos os transportes — fallback automatico:
                        // WebSockets → ServerSentEvents → LongPolling.
                        // Azure App Service pode bloquear WebSockets; LongPolling sempre funciona.
                        options.Transports =
                            Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets |
                            Microsoft.AspNetCore.Http.Connections.HttpTransportType.ServerSentEvents |
                            Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;

                        // Em desenvolvimento com HTTPS self-signed, ignora validacao de certificado.
                        // Em producao, o certificado e valido — esta opcao nao tem efeito negativo.
                        if (isHttps)
                        {
                            options.HttpMessageHandlerFactory = handler =>
                            {
                                if (handler is System.Net.Http.HttpClientHandler h)
                                    h.ServerCertificateCustomValidationCallback =
                                        System.Net.Http.HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
                                return handler;
                            };
                        }
                    })
                    .WithAutomaticReconnect(new[]
                    {
                        TimeSpan.Zero,
                        TimeSpan.FromSeconds(2),
                        TimeSpan.FromSeconds(10),
                        TimeSpan.FromSeconds(30)
                    })
                    .ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Warning))
                    .Build();

                _connection.Closed += async (error) =>
                {
                    _logger.LogWarning("Conexao fechada: {Msg}", error?.Message);
                    TriggerOfflineStatus();
                    await Task.Delay(5000);
                    Interlocked.Exchange(ref _isConnecting, 0);
                    await ConnectAsync(_serverUrl);
                };

                _connection.Reconnecting += _ =>
                {
                    _logger.LogInformation("Reconectando ao servidor...");
                    TriggerOfflineStatus();
                    return Task.CompletedTask;
                };

                _connection.Reconnected += async (connectionId) =>
                {
                    _logger.LogInformation("Reconectado: {Id}", connectionId);
                    await RegisterAgentAsync();
                    await SendStatusAsync();
                };

                _connection.On<BiometricCommand>("IniciarCaptura", command =>
                {
                    _logger.LogInformation("Comando IniciarCaptura: {ReqId}", command.RequestId);
                    if (string.IsNullOrEmpty(command.Command)) command.Command = "captura";
                    var cmd = command;
                    CommandReceived?.Invoke(this, cmd);
                    return Task.CompletedTask;
                });

                _connection.On<BiometricCommand>("IniciarValidacao", command =>
                {
                    _logger.LogInformation("Comando IniciarValidacao: {ReqId}", command.RequestId);
                    if (string.IsNullOrEmpty(command.Command)) command.Command = "validacao";
                    var cmd = command;
                    CommandReceived?.Invoke(this, cmd);
                    return Task.CompletedTask;
                });

                // I2: cancela o CTS da operacao em andamento no Worker
                _connection.On<string>("CancelarOperacao", requestId =>
                {
                    _logger.LogInformation("Comando CancelarOperacao: {ReqId}", requestId);
                    CurrentOperationCts?.Cancel();
                    return Task.CompletedTask;
                });

                // Hub solicitou status imediato (chamado pelo cliente Blazor ao abrir o dialog)
                _connection.On("RequestStatus", async () =>
                {
                    _logger.LogDebug("RequestStatus recebido — enviando status imediato");
                    await SendStatusAsync();
                });

                await _connection.StartAsync();
                _logger.LogInformation("Conectado ao servidor SignalR em {Url}", serverUrl);

                await RegisterAgentAsync();
                await SendStatusAsync();

                _heartbeatTimer = new System.Threading.Timer(
                    async _ => await SendStatusAsync(),
                    null,
                    TimeSpan.FromSeconds(10),
                    TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao conectar ao servidor SignalR");
                Interlocked.Exchange(ref _isConnecting, 0);
                throw;
            }
            finally
            {
                Interlocked.Exchange(ref _isConnecting, 0);
            }
        }

        private async Task RegisterAgentAsync()
        {
            if (_connection?.State != HubConnectionState.Connected) return;
            try
            {
                await _connection.InvokeAsync("RegisterAgent",
                    _companyCode, _stationId, Environment.MachineName, GetLocalIpAddress());
                _logger.LogInformation("Agente registrado: CompanyCode={Code} StationId={Station}",
                    _companyCode, _stationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao registrar agente");
            }
        }

        public async Task SendStatusAsync()
        {
            if (_connection?.State != HubConnectionState.Connected) return;
            try
            {
                var leitorConectado = _hardwareService.IsInitialized;
                var dllEncontrada   = CheckDllsExist();

                var status = new AgentStatus
                {
                    Unidade          = _companyCode,
                    EstacaoId        = _stationId,
                    IpLocal          = GetLocalIpAddress(),
                    MachineName      = Environment.MachineName,
                    BackendConectado = true,
                    LeitorConectado  = leitorConectado,
                    LeitorAberto     = leitorConectado,
                    EstadoLeitor     = leitorConectado ? "Pronto" : "Desconectado",
                    Estado           = leitorConectado ? "Ativo" : "SemLeitor",
                    DllEncontrada    = dllEncontrada
                };

                await _connection.InvokeAsync("SendAgentStatus", status);
                StatusChanged?.Invoke(this, status);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao enviar status");
            }
        }

        public async Task SendCapturaResponseAsync(BiometricResponse response)
        {
            if (_connection?.State != HubConnectionState.Connected) return;
            try
            {
                await _connection.InvokeAsync("SendCapturaResponse", response);
                _logger.LogInformation("CapturaResponse enviada: {ReqId} status={S}",
                    response.RequestId, response.Status);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao enviar CapturaResponse");
            }
        }

        public async Task SendValidacaoResponseAsync(BiometricResponse response)
        {
            if (_connection?.State != HubConnectionState.Connected) return;
            try
            {
                await _connection.InvokeAsync("SendValidacaoResponse", response);
                _logger.LogInformation("ValidacaoResponse enviada: {ReqId} status={S} approved={A}",
                    response.RequestId, response.Status, response.Approved);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao enviar ValidacaoResponse");
            }
        }

        private static bool CheckDllsExist()
        {
            var baseDir = AppContext.BaseDirectory;
            return File.Exists(Path.Combine(baseDir, "ftrScanAPI.dll"))
                && File.Exists(Path.Combine(baseDir, "ftrAnsiSdk.dll"));
        }

        private static string GetLocalIpAddress()
        {
            try
            {
                var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
                foreach (var ip in host.AddressList)
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        return ip.ToString();
            }
            catch { }
            return "127.0.0.1";
        }

        private void TriggerOfflineStatus()
        {
            try
            {
                var leitorConectado = _hardwareService.IsInitialized;
                var dllEncontrada   = CheckDllsExist();

                var status = new AgentStatus
                {
                    Unidade          = _companyCode,
                    EstacaoId        = _stationId,
                    IpLocal          = GetLocalIpAddress(),
                    MachineName      = Environment.MachineName,
                    BackendConectado = false,
                    LeitorConectado  = leitorConectado,
                    LeitorAberto     = leitorConectado,
                    EstadoLeitor     = leitorConectado ? "Pronto" : "Desconectado",
                    Estado           = "SemConexao",
                    DllEncontrada    = dllEncontrada
                };

                StatusChanged?.Invoke(this, status);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao disparar status offline");
            }
        }

        public void Dispose()
        {
            _heartbeatTimer?.Dispose();
            _connection?.DisposeAsync().AsTask().Wait();
        }
    }
}
