using Cmso.Biometric.Agent.Core.Hardware;
using Cmso.Biometric.Agent.Core.Communication;
using Cmso.Biometric.Agent.Core.Models;

namespace Cmso.Biometric.Agent.Service;

/// <summary>
/// Worker principal do agente biométrico CMSO.
///
/// Filosofia: o agente é um driver de hardware — conecta ao Hub imediatamente e
/// reporta o estado real do leitor. Hardware e conectividade são ciclos independentes.
///
/// Fluxo de inicialização:
///   1. Conecta ao Hub SignalR (imediatamente, sem esperar o leitor)
///   2. Registra-se no Hub com MachineName — fica visível para o servidor
///   3. Em background, faz retry do hardware a cada 30s até ter sucesso
///   4. Recebe e processa comandos (captura/validação) — rejeita se leitor ainda não pronto
///
/// Configurações em appsettings.json → seção "Biometric":
///   HubUrl  : URL base do CmsoAgendamento
///             Desenvolvimento : http://localhost:5163
///             Produção        : https://agenda.cmsocupacional.com.br
/// </summary>
public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly BiometricHardwareService _hardwareService;
    private readonly BiometricSignalRClient _signalRClient;
    private readonly IConfiguration _configuration;

    private static readonly TimeSpan HardwareRetryInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SignalRRetryInterval  = TimeSpan.FromSeconds(10);

    // I2: CTS por operacao — permite cancelar captura/validacao via CancelarOperacao
    private CancellationTokenSource? _currentOperationCts;

    // I3: protege ConnectAsync contra chamadas concorrentes
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    public Worker(
        ILogger<Worker> logger,
        BiometricHardwareService hardwareService,
        BiometricSignalRClient signalRClient,
        IConfiguration configuration)
    {
        _logger          = logger;
        _hardwareService = hardwareService;
        _signalRClient   = signalRClient;
        _configuration   = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Iniciando Agente Biometrico CMSO");

        var serverUrl = _configuration["Biometric:HubUrl"] ?? "http://localhost:5000";

        // ── Alerta de configuracao ────────────────────────────────────────────
        LogUrlAlert(serverUrl);

        // ── Passo 1: conecta ao Hub imediatamente (leitor pode estar ausente) ──
        // O agente precisa estar registrado no Hub para receber comandos.
        // O estado do leitor é reportado via heartbeat — o servidor sabe que está "SemLeitor".
        await ConnectWithRetryAsync(serverUrl, stoppingToken);
        if (stoppingToken.IsCancellationRequested) return;

        // ── Passo 2: registra handler de comandos ──────────────────────────────
        _signalRClient.CommandReceived += async (_, command) =>
            await ProcessCommandAsync(command, stoppingToken);

        // ── Passo 3: inicia retry do hardware em background ────────────────────
        // Não bloqueia o loop principal — hardware é inicializado quando disponível.
        _ = Task.Run(() => HardwareRetryLoopAsync(stoppingToken), stoppingToken);

        _logger.LogInformation("Agente Biometrico registrado no Hub e aguardando comandos");

        while (!stoppingToken.IsCancellationRequested)
            await Task.Delay(1000, stoppingToken).ConfigureAwait(false);

        _logger.LogInformation("Agente Biometrico parado");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Conectividade SignalR
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Tenta conectar ao Hub em loop com backoff até ter sucesso ou serviço parar.
    /// Separado de WaitForHardware — os dois ciclos são independentes.
    /// </summary>
    private async Task ConnectWithRetryAsync(string serverUrl, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectWithLockAsync(serverUrl, ct);
                _logger.LogInformation("Conectado ao Hub SignalR com sucesso");
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao conectar ao Hub {Url}. Nova tentativa em {S}s...",
                    serverUrl, SignalRRetryInterval.TotalSeconds);
                await Task.Delay(SignalRRetryInterval, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// I3: SemaphoreSlim garante que ConnectAsync não seja chamado simultaneamente.
    /// </summary>
    private async Task ConnectWithLockAsync(string serverUrl, CancellationToken ct)
    {
        await _connectLock.WaitAsync(ct);
        try
        {
            await _signalRClient.ConnectAsync(serverUrl);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Hardware — ciclo independente
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Loop de retry do hardware em background.
    /// Se o leitor já estiver inicializado (ex: conectado antes do serviço), retorna imediatamente.
    /// Após inicialização bem-sucedida, envia status atualizado ao Hub.
    /// </summary>
    private async Task HardwareRetryLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_hardwareService.IsInitialized)
            {
                // Se já está inicializado, verifica se continua conectado
                if (!_hardwareService.CheckConnection())
                {
                    _logger.LogWarning("Leitor desconectado. Enviando status ao hub.");
                    await _signalRClient.SendStatusAsync();
                }
            }
            else
            {
                _logger.LogInformation("Tentando inicializar leitor biometrico...");
                if (_hardwareService.Initialize())
                {
                    _logger.LogInformation("Leitor biometrico inicializado com sucesso");
                    // Atualiza o Hub com o novo estado do hardware
                    await _signalRClient.SendStatusAsync();
                }
            }

            // Intervalo dinâmico: 3 segundos se inicializado (conectado), senão 10 segundos
            TimeSpan delay = _hardwareService.IsInitialized ? TimeSpan.FromSeconds(3) : HardwareRetryInterval;
            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Processamento de comandos
    // ─────────────────────────────────────────────────────────────────────────

    private async Task ProcessCommandAsync(BiometricCommand command, CancellationToken serviceCt)
    {
        _logger.LogInformation("Processando comando: {Cmd} requestId={ReqId}",
            command.Command, command.RequestId);
        try
        {
            switch (command.Command)
            {
                case "captura":
                    await ProcessCapturaAsync(command, serviceCt);
                    break;
                case "validacao":
                    await ProcessValidacaoAsync(command, serviceCt);
                    break;
                default:
                    _logger.LogWarning("Comando desconhecido: {Cmd}", command.Command);
                    break;
            }
        }
        catch (OperationCanceledException) when (!serviceCt.IsCancellationRequested)
        {
            _logger.LogInformation("Operacao {Cmd} requestId={ReqId} cancelada pelo cliente",
                command.Command, command.RequestId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao processar comando {Cmd}", command.Command);
            await _signalRClient.SendCapturaResponseAsync(new BiometricResponse
            {
                RequestId = command.RequestId,
                Status    = "error",
                Message   = "Erro interno: " + ex.Message
            });
        }
    }

    private async Task ProcessCapturaAsync(BiometricCommand command, CancellationToken serviceCt)
    {
        _logger.LogInformation("ProcessCapturaAsync iniciado para requestId={RequestId}, dedo={Finger}", command.RequestId, command.Finger);
        
        // Aguarda leitor inicializar — timeout reduzido para 10s para resposta mais rápida ao operador
        int maxWaitTime = 10000;
        int waitInterval = 1000;
        int elapsedTime = 0;

        while (!_hardwareService.IsInitialized && elapsedTime < maxWaitTime && !serviceCt.IsCancellationRequested)
        {
            await Task.Delay(waitInterval, serviceCt);
            elapsedTime += waitInterval;
            await _signalRClient.SendCapturaResponseAsync(new BiometricResponse
            {
                RequestId = command.RequestId,
                Status    = "started",
                Message   = $"Aguardando leitor biométrico... ({elapsedTime / 1000}s)"
            });
        }

        if (!_hardwareService.IsInitialized)
        {
            _logger.LogError("Leitor não inicializado após {MaxWaitTime}ms", maxWaitTime);
            await _signalRClient.SendCapturaResponseAsync(new BiometricResponse
            {
                RequestId = command.RequestId,
                Status    = "error",
                Message   = "Leitor biometrico nao disponivel. Verifique a conexao USB e aguarde."
            });
            return;
        }

        _logger.LogInformation("Leitor inicializado! Iniciando captura para requestId={RequestId}", command.RequestId);

        // Initial started status
        await _signalRClient.SendCapturaResponseAsync(new BiometricResponse
        {
            RequestId = command.RequestId,
            Status    = "started",
            Message   = "Aguardando dedo no leitor..."
        });

        // 1ª Captura
        _logger.LogInformation("Iniciando 1ª captura para requestId={RequestId}", command.RequestId);
        await _signalRClient.SendCapturaResponseAsync(new BiometricResponse
        {
            RequestId = command.RequestId,
            Status    = "capturing_first",
            Message   = "Coloque o dedo no leitor para 1ª captura..."
        });

        _logger.LogInformation("Aguardando 1ª captura de imagem para requestId={RequestId}", command.RequestId);
        var image1 = await CaptureImageWithTimeoutAsync(serviceCt);
        if (image1 is null)
        {
            _logger.LogWarning("Timeout na 1ª captura para requestId={RequestId}", command.RequestId);
            await _signalRClient.SendCapturaResponseAsync(new BiometricResponse
            {
                RequestId = command.RequestId,
                Status    = "error",
                Message   = "Tempo esgotado. Nenhum dedo detectado na 1ª captura."
            });
            return;
        }
        _logger.LogInformation("1ª imagem capturada com sucesso! Tamanho: {Size} bytes", image1.Length);

        var base64Img1 = _hardwareService.GetImageAsBase64Png(image1);

        var (template1, _) = _hardwareService.CreateTemplate(image1);
        if (template1 is null)
        {
            _logger.LogWarning("Falha ao gerar template na 1ª captura");
            await _signalRClient.SendCapturaResponseAsync(new BiometricResponse
            {
                RequestId = command.RequestId,
                Status    = "error",
                Message   = "Não foi possível gerar o template na 1ª captura. Tente novamente."
            });
            return;
        }
        _logger.LogInformation("1ª template criada com sucesso ({Size} bytes)", template1.Length);

        // Espera o usuário levantar o dedo para não capturar a mesma colocação imediatamente
        await _signalRClient.SendCapturaResponseAsync(new BiometricResponse
        {
            RequestId = command.RequestId,
            Status    = "processing",
            Message   = "Remova o dedo do leitor..."
        });
        while (_hardwareService.IsFingerPresent() && !serviceCt.IsCancellationRequested)
        {
            await Task.Delay(200, serviceCt);
        }

        // 2ª Captura
        _logger.LogInformation("Iniciando 2ª captura para requestId={RequestId}", command.RequestId);
        await _signalRClient.SendCapturaResponseAsync(new BiometricResponse
        {
            RequestId = command.RequestId,
            Status    = "capturing_second",
            Message   = "Coloque o mesmo dedo novamente para 2ª captura...",
            FingerprintImageBase64 = base64Img1
        });

        _logger.LogInformation("Aguardando 2ª captura de imagem para requestId={RequestId}", command.RequestId);
        var image2 = await CaptureImageWithTimeoutAsync(serviceCt);
        if (image2 is null)
        {
            _logger.LogWarning("Timeout na 2ª captura para requestId={RequestId}", command.RequestId);
            await _signalRClient.SendCapturaResponseAsync(new BiometricResponse
            {
                RequestId = command.RequestId,
                Status    = "error",
                Message   = "Tempo esgotado. Nenhum dedo detectado na 2ª captura."
            });
            return;
        }
        _logger.LogInformation("2ª imagem capturada com sucesso! Tamanho: {Size} bytes", image2.Length);

        var base64Img2 = _hardwareService.GetImageAsBase64Png(image2);

        var (template2, _) = _hardwareService.CreateTemplate(image2);
        if (template2 is null)
        {
            _logger.LogWarning("Falha ao gerar template na 2ª captura");
            await _signalRClient.SendCapturaResponseAsync(new BiometricResponse
            {
                RequestId = command.RequestId,
                Status    = "error",
                Message   = "Não foi possível gerar o template na 2ª captura. Tente novamente."
            });
            return;
        }
        _logger.LogInformation("2ª template criada com sucesso ({Size} bytes)", template2.Length);

        // Verifica se as duas capturas são do mesmo dedo
        _logger.LogInformation("Verificando correspondência entre as duas capturas...");
        var (matched, matchScore) = _hardwareService.VerifyMatch(image2, template1);
        _logger.LogInformation("Resultado da verificação: matched={Matched}, score={Score}/100", matched, matchScore);
        
        if (!matched)
        {
            await _signalRClient.SendCapturaResponseAsync(new BiometricResponse
            {
                RequestId = command.RequestId,
                Status    = "error",
                Message   = "As duas capturas não correspondem. Tente novamente."
            });
            return;
        }

        // Se tudo der certo, usa a template com o maior tamanho (mais minúcias)
        var finalTemplate = template1.Length >= template2.Length ? template1 : template2;
        var finalBase64 = finalTemplate == template1 ? base64Img1 : base64Img2;

        _logger.LogInformation("Cadastro biométrico concluído com sucesso");

        // Delay artificial de 800ms para feedback natural
        await Task.Delay(800, serviceCt);

        await _signalRClient.SendCapturaResponseAsync(new BiometricResponse
        {
            RequestId    = command.RequestId,
            Status       = "success",
            Template     = finalTemplate,
            QualityScore = 100,
            Message      = "Cadastro realizado com sucesso!",
            FingerprintImageBase64 = finalBase64
        });
    }

    private async Task ProcessValidacaoAsync(BiometricCommand command, CancellationToken serviceCt)
    {
        _logger.LogInformation("ProcessValidacaoAsync iniciado para requestId={RequestId}", command.RequestId);
        
        // Aguarda leitor inicializar — timeout de 10s
        int maxWaitTime = 10000;
        int waitInterval = 1000;
        int elapsedTime = 0;

        while (!_hardwareService.IsInitialized && elapsedTime < maxWaitTime && !serviceCt.IsCancellationRequested)
        {
            await Task.Delay(waitInterval, serviceCt);
            elapsedTime += waitInterval;
            await _signalRClient.SendValidacaoResponseAsync(new BiometricResponse
            {
                RequestId = command.RequestId,
                Status    = "started",
                Message   = $"Aguardando leitor biométrico... ({elapsedTime / 1000}s)"
            });
        }

        if (!_hardwareService.IsInitialized)
        {
            _logger.LogError("Leitor não inicializado para validação após {MaxWaitTime}ms", maxWaitTime);
            await _signalRClient.SendValidacaoResponseAsync(new BiometricResponse
            {
                RequestId = command.RequestId,
                Status    = "error",
                Message   = "Leitor biometrico nao disponivel. Verifique a conexao USB e aguarde."
            });
            return;
        }

        if (command.Template is null || command.Template.Length == 0)
        {
            _logger.LogError("Template não fornecido para validação requestId={RequestId}", command.RequestId);
            await _signalRClient.SendValidacaoResponseAsync(new BiometricResponse
            {
                RequestId = command.RequestId,
                Status    = "error",
                Message   = "Template nao fornecido para validacao."
            });
            return;
        }

        _logger.LogInformation("Template recebido para validação: {Size} bytes", command.Template.Length);
        await _signalRClient.SendValidacaoResponseAsync(new BiometricResponse
        {
            RequestId = command.RequestId,
            Status    = "started",
            Message   = "Aguardando dedo no leitor..."
        });

        _logger.LogInformation("Aguardando captura para validação requestId={RequestId}", command.RequestId);
        var image = await CaptureImageWithTimeoutAsync(serviceCt);
        if (image is null)
        {
            _logger.LogWarning("Timeout na captura para validação requestId={RequestId}", command.RequestId);
            await _signalRClient.SendValidacaoResponseAsync(new BiometricResponse
            {
                RequestId = command.RequestId,
                Status    = "error",
                Message   = "Tempo esgotado. Nenhum dedo detectado."
            });
            return;
        }
        _logger.LogInformation("Imagem capturada para validação: {Size} bytes", image.Length);
        var base64Img = _hardwareService.GetImageAsBase64Png(image);

        _logger.LogInformation("Iniciando verificação biométrica...");
        var (matched, score) = _hardwareService.VerifyMatch(image, command.Template);
        _logger.LogInformation("Resultado da verificação: matched={Matched}, score={Score}/100", matched, score);

        // Delay artificial de 800ms para feedback natural
        await Task.Delay(800, serviceCt);

        // FIX #2: negativa usa status "success" com Approved=false.
        // "error" é reservado para falhas técnicas (hardware, timeout, exceção).
        await _signalRClient.SendValidacaoResponseAsync(new BiometricResponse
        {
            RequestId  = command.RequestId,
            Status     = "success",
            Approved   = matched,
            MatchScore = score,
            Message    = matched
                ? "Identidade confirmada. Score: " + score + "/100"
                : "Digital nao reconhecida. Score: " + score + "/100",
            FingerprintImageBase64 = base64Img
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Captura com timeout
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Aguarda presença do dedo por até 120s (240 x 500ms).
    /// Após detectar o dedo, aguarda 600ms de estabilização antes de capturar —
    /// evita capturar enquanto o dedo ainda está sendo posicionado (imagem parcial).
    /// I2: CTS vinculado ao BiometricSignalRClient — cancelável via CancelarOperacao.
    /// </summary>
    private async Task<byte[]?> CaptureImageWithTimeoutAsync(CancellationToken serviceCt)
    {
        _logger.LogDebug("CaptureImageWithTimeoutAsync iniciado");

        _currentOperationCts?.Cancel();
        _currentOperationCts?.Dispose();
        _currentOperationCts = CancellationTokenSource.CreateLinkedTokenSource(serviceCt);
        _signalRClient.CurrentOperationCts = _currentOperationCts;
        var operationCt = _currentOperationCts.Token;

        _hardwareService.SetLedState(green: true, red: false); // Acende o LED verde para sinalizar que está aguardando a biometria
        try
        {
            // Aguarda presença do dedo por até 120s (240 x 500ms).
            for (int i = 0; i < 240 && !operationCt.IsCancellationRequested; i++)
            {
                if (_hardwareService.IsFingerPresent())
                {
                    _logger.LogDebug("Dedo detectado na tentativa {Attempt}. Aguardando estabilização...", i + 1);

                    // Estabilização: aguarda o dedo assentar para evitar imagem parcial
                    await Task.Delay(600, operationCt).ConfigureAwait(false);

                    // Verifica se o dedo ainda está presente após estabilização
                    if (!_hardwareService.IsFingerPresent())
                    {
                        _logger.LogDebug("Dedo retirado durante estabilização. Aguardando novamente...");
                        continue;
                    }

                    _logger.LogDebug("Dedo estabilizado. Capturando imagem...");
                    return _hardwareService.CaptureImage();
                }
                await Task.Delay(500, operationCt).ConfigureAwait(false);
            }

            _logger.LogDebug("CaptureImageWithTimeoutAsync: timeout ou cancelado");
            return null;
        }
        finally
        {
            _hardwareService.SetLedState(green: false, red: false); // Apaga os LEDs após concluir ou cancelar a operação
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Alerta de configuracao
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Exibe logs de alerta coloridos no console indicando para qual ambiente
    /// o agente está apontando. Evita publicar em produção com URL errada.
    /// </summary>
    private void LogUrlAlert(string serverUrl)
    {
        var isLocal      = serverUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase)
                        || serverUrl.Contains("127.0.0.1");
        var isProducao   = serverUrl.Contains("agenda.cmsocupacional.com.br", StringComparison.OrdinalIgnoreCase);
        var urlDesconhecida = !isLocal && !isProducao;

        // Separador visual
        _logger.LogInformation("════════════════════════════════════════════════════════");
        _logger.LogInformation("  AGENTE BIOMETRICO CMSO — CONFIGURACAO DE AMBIENTE");
        _logger.LogInformation("════════════════════════════════════════════════════════");
        _logger.LogInformation("  Maquina  : {Machine}", Environment.MachineName);
        _logger.LogInformation("  HubUrl   : {Url}", serverUrl);

        if (isLocal)
        {
            _logger.LogInformation("  Ambiente : DESENVOLVIMENTO LOCAL ✓");
            _logger.LogInformation("  → CmsoAgendamento deve estar rodando em http://localhost:5163");
            _logger.LogInformation("  → Para publicar em PRODUCAO, altere HubUrl em appsettings.json:");
            _logger.LogInformation("    \"HubUrl\": \"https://agenda.cmsocupacional.com.br\"");
        }
        else if (isProducao)
        {
            _logger.LogWarning("  Ambiente : PRODUCAO ⚠");
            _logger.LogWarning("  → Conectando ao servidor PUBLICO: {Url}", serverUrl);
            _logger.LogWarning("  → Para desenvolvimento local, altere HubUrl em appsettings.json:");
            _logger.LogWarning("    \"HubUrl\": \"http://localhost:5163\"");
        }
        else
        {
            _logger.LogWarning("  Ambiente : URL DESCONHECIDA ⚠");
            _logger.LogWarning("  → HubUrl nao reconhecida: {Url}", serverUrl);
            _logger.LogWarning("  → LOCAL    : \"HubUrl\": \"http://localhost:5163\"");
            _logger.LogWarning("  → PRODUCAO : \"HubUrl\": \"https://agenda.cmsocupacional.com.br\"");
        }

        _logger.LogInformation("  Arquivo  : appsettings.json → secao Biometric → HubUrl");
        _logger.LogInformation("════════════════════════════════════════════════════════");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Shutdown
    // ─────────────────────────────────────────────────────────────────────────

    public override async Task StopAsync(CancellationToken ct)
    {
        _logger.LogInformation("Parando Agente Biometrico...");
        _currentOperationCts?.Cancel();
        _currentOperationCts?.Dispose();
        _connectLock.Dispose();
        await base.StopAsync(ct);
    }
}
