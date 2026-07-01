using Cmso.Biometric.Agent.Core.Communication;
using Cmso.Biometric.Agent.Core.Hardware;
using Cmso.Biometric.Agent.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Velopack;

namespace Cmso.Biometric.Agent.Tray;

/// <summary>
/// ApplicationContext do tray — roda sem janela principal.
/// Gerencia o ícone na bandeja, o estado do leitor e a conexão SignalR.
/// </summary>
public class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon      _trayIcon;
    private readonly ContextMenuStrip _menu;
    private readonly BiometricHardwareService _hardware;
    private readonly BiometricSignalRClient   _client;
    private readonly string _serverUrl;

    // Itens de menu dinâmicos
    private readonly ToolStripLabel  _lblStatus;
    private readonly ToolStripLabel  _lblMaquina;
    private readonly ToolStripLabel  _lblServidor;
    private readonly ToolStripLabel  _lblLeitor;
    private readonly ToolStripMenuItem _menuReconectar;
    private readonly ToolStripMenuItem _menuLogs;
    private readonly ToolStripMenuItem _menuAutoStart;
    private readonly ToolStripMenuItem _menuInstalarDriver;

    // Ícones (gerados a partir dos PNGs com overlay colorido de status)
    private readonly Icon _iconGreen;
    private readonly Icon _iconYellow;
    private readonly Icon _iconRed;

    private readonly IntPtr _hIconGreen;
    private readonly IntPtr _hIconYellow;
    private readonly IntPtr _hIconRed;

    // Estado atual
    private bool _servidorConectado  = false;
    private bool _leitorConectado    = false;
    private string _estadoLeitor     = "Iniciando...";

    // Rastreia o último estado CONHECIDO do leitor e servidor para detectar transições
    private bool _ultimoEstadoLeitor  = false;
    private bool _ultimoEstadoServidor = false;

    private CancellationTokenSource? _currentOperationCts;
    private readonly CancellationTokenSource _appCts = new();

    public TrayApplicationContext(string serverUrl, ILoggerFactory loggerFactory)
    {
        _serverUrl = serverUrl;

        // Serviços de hardware e comunicação
        _hardware = new BiometricHardwareService(loggerFactory.CreateLogger<BiometricHardwareService>());
        _client   = new BiometricSignalRClient(loggerFactory.CreateLogger<BiometricSignalRClient>(), _hardware);

        // Carrega ícones a partir dos PNGs com overlay colorido de status
        var (greenIcon, greenHandle) = CreateStatusIcon(Color.FromArgb(40, 167, 69));   // verde
        var (yellowIcon, yellowHandle) = CreateStatusIcon(Color.FromArgb(255, 193, 7));   // amarelo
        var (redIcon, redHandle) = CreateStatusIcon(Color.FromArgb(220, 53, 69));   // vermelho

        _iconGreen = greenIcon;
        _iconYellow = yellowIcon;
        _iconRed = redIcon;

        _hIconGreen = greenHandle;
        _hIconYellow = yellowHandle;
        _hIconRed = redHandle;

        // Itens dinâmicos do menu
        _lblStatus    = new ToolStripLabel("CMSO Agente Biométrico") { Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
        _lblMaquina   = new ToolStripLabel($"Máquina: {Environment.MachineName}");
        _lblServidor  = new ToolStripLabel("Servidor: ⏳ Conectando...");
        _lblLeitor    = new ToolStripLabel("Leitor:   ⏳ Aguardando...");
        _menuReconectar = new ToolStripMenuItem("🔄  Reconectar", null, OnReconectar);
        _menuLogs       = new ToolStripMenuItem("📋  Ver Logs",   null, OnVerLogs);
        _menuInstalarDriver = new ToolStripMenuItem("🔌  Instalar Driver do Leitor", null, OnInstalarDriver);

        // Auto-start: estado inicial = está registrado no Windows
        var autoStartAtivo = IsAutoStartEnabled();
        _menuAutoStart = new ToolStripMenuItem("⚙️  Iniciar com Windows", null, OnToggleAutoStart)
        {
            Checked = autoStartAtivo
        };

        // Monta o menu de contexto
        _menu = new ContextMenuStrip();
        _menu.Items.Add(_lblStatus);
        _menu.Items.Add(_lblMaquina);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_lblServidor);
        _menu.Items.Add(_lblLeitor);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_menuReconectar);
        _menu.Items.Add(_menuLogs);
        _menu.Items.Add(_menuInstalarDriver);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_menuAutoStart);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("❌  Fechar", null, OnFechar));

        // Cria o ícone na bandeja
        _trayIcon = new NotifyIcon
        {
            Text            = "CMSO Biometria — Iniciando...",
            Icon            = _iconRed,
            ContextMenuStrip = _menu,
            Visible         = true
        };

        // Força a criação do handle do menu (necessário para BeginInvoke em ShowNotification)
        _ = _menu.Handle;

        // Clique duplo abre os logs
        _trayIcon.DoubleClick += OnVerLogs;

        // Assina eventos do cliente
        _client.StatusChanged    += OnStatusChanged;
        _client.CommandReceived  += OnCommandReceived;

        // Inicia conexão e hardware em background
        Task.Run(StartAsync);

        // Notificação de inicialização com delay para garantir que o message loop está ativo
        // antes de chamar BeginInvoke (handles Win32 dos controles só existem após o loop iniciar)
        var startupTimer = new System.Windows.Forms.Timer { Interval = 500 };
        startupTimer.Tick += (s, e) =>
        {
            startupTimer.Stop();
            startupTimer.Dispose();

            // Registra auto-start no Windows (se ainda não estiver ativo)
            if (!IsAutoStartEnabled())
                SetAutoStart(true);

            ShowNotification("CMSO Biometria", "Agente Biométrico iniciado com sucesso.", Color.FromArgb(0, 120, 215));

            // Verifica atualizações em background
            _ = CheckForUpdatesAsync();
        };
        startupTimer.Start();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Inicialização assíncrona
    // ─────────────────────────────────────────────────────────────────────────

    private async Task StartAsync()
    {
        // Conecta ao Hub SignalR (retry automático de 10s em loop)
        while (true)
        {
            try
            {
                await _client.ConnectAsync(_serverUrl);
                _servidorConectado = true;
                UpdateTrayFromThread();
                break;
            }
            catch
            {
                _servidorConectado = false;
                UpdateTrayFromThread();
                await Task.Delay(TimeSpan.FromSeconds(10));
            }
        }

        // Inicializa hardware em background com retry de 30s
        _ = Task.Run(HardwareRetryLoopAsync);
    }

    private async Task HardwareRetryLoopAsync()
    {
        while (!_appCts.Token.IsCancellationRequested)
        {
            bool conectadoAgora;

            if (_hardware.IsInitialized)
            {
                // Leitor estava inicializado — verifica se continua fisicamente conectado
                conectadoAgora = _hardware.CheckConnection();
            }
            else
            {
                // Leitor não inicializado — tenta inicializar
                conectadoAgora = _hardware.Initialize();
            }

            // Detecta transição de estado e notifica apenas na mudança
            if (conectadoAgora && !_ultimoEstadoLeitor)
            {
                // → CONECTADO
                _leitorConectado     = true;
                _ultimoEstadoLeitor  = true;
                _estadoLeitor        = "Pronto";
                await _client.SendStatusAsync();
                UpdateTrayFromThread();
                ShowNotification("Leitor Conectado", "O leitor biométrico está pronto para uso.", Color.FromArgb(40, 167, 69));
            }
            else if (!conectadoAgora && _ultimoEstadoLeitor)
            {
                // → DESCONECTADO
                _leitorConectado     = false;
                _ultimoEstadoLeitor  = false;
                _estadoLeitor        = "Desconectado";
                await _client.SendStatusAsync();
                UpdateTrayFromThread();
                ShowNotification("Leitor Desconectado", "O leitor biométrico foi desconectado via USB.", Color.FromArgb(220, 53, 69));
            }

            // Intervalo dinâmico: 3s se conectado, 10s se desconectado
            int delaySeconds = _leitorConectado ? 3 : 10;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), _appCts.Token);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private void ShowNotification(string title, string message, Color accentColor)
    {
        void Show()
        {
            try
            {
                var form = new NotificationForm(title, message, accentColor);
                form.Show();
            }
            catch
            {
                // Evita que erros nas notificações afetem a aplicação
            }
        }

        if (_menu.IsHandleCreated && _menu.InvokeRequired)
            _menu.BeginInvoke((Action)Show);
        else
            Show();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Handlers de eventos
    // ─────────────────────────────────────────────────────────────────────────

    private void OnStatusChanged(object? sender, AgentStatus status)
    {
        _leitorConectado   = status.LeitorConectado;
        _estadoLeitor      = status.EstadoLeitor;

        var servidorOnline = status.BackendConectado;

        // Detecta transição do servidor (conectado → desconectado ou vice-versa)
        if (servidorOnline && !_ultimoEstadoServidor)
        {
            _servidorConectado = true;
            _ultimoEstadoServidor = true;
            ShowNotification("Servidor Conectado", "Conexão com o servidor restabelecida.", Color.FromArgb(40, 167, 69));
        }
        else if (!servidorOnline && _ultimoEstadoServidor)
        {
            _servidorConectado = false;
            _ultimoEstadoServidor = false;
            ShowNotification("Servidor Desconectado", "A conexão com o servidor foi perdida.", Color.FromArgb(220, 53, 69));
        }
        else
        {
            _servidorConectado = servidorOnline;
            _ultimoEstadoServidor = servidorOnline;
        }

        UpdateTrayFromThread();
    }

    private async void OnCommandReceived(object? sender, BiometricCommand command)
    {
        await ProcessCommandAsync(command, _appCts.Token);
    }

    private async Task ProcessCommandAsync(BiometricCommand command, CancellationToken serviceCt)
    {
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
            }
        }
        catch (OperationCanceledException) when (!serviceCt.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await _client.SendCapturaResponseAsync(new BiometricResponse
            {
                RequestId = command.RequestId,
                Status    = "error",
                Message   = "Erro interno: " + ex.Message
            });
        }
    }

    private async Task ProcessCapturaAsync(BiometricCommand command, CancellationToken serviceCt)
    {
        int maxWaitTime = 10000;
        int waitInterval = 1000;
        int elapsedTime = 0;

        while (!_hardware.IsInitialized && elapsedTime < maxWaitTime && !serviceCt.IsCancellationRequested)
        {
            await Task.Delay(waitInterval, serviceCt);
            elapsedTime += waitInterval;
            await _client.SendCapturaResponseAsync(new BiometricResponse
            {
                RequestId = command.RequestId,
                Status    = "started",
                Message   = $"Aguardando leitor biométrico... ({elapsedTime / 1000}s)"
            });
        }

        if (!_hardware.IsInitialized)
        {
            await _client.SendCapturaResponseAsync(new BiometricResponse
            {
                RequestId = command.RequestId,
                Status    = "error",
                Message   = "Leitor biometrico nao disponivel. Verifique a conexao USB e aguarde."
            });
            return;
        }

        await _client.SendCapturaResponseAsync(new BiometricResponse
        {
            RequestId = command.RequestId,
            Status    = "started",
            Message   = "Aguardando dedo no leitor..."
        });

        // 1ª Captura
        await _client.SendCapturaResponseAsync(new BiometricResponse
        {
            RequestId = command.RequestId,
            Status    = "capturing_first",
            Message   = "Coloque o dedo no leitor para 1ª captura..."
        });

        var image1 = await CaptureImageWithTimeoutAsync(serviceCt);
        if (image1 is null)
        {
            await _client.SendCapturaResponseAsync(new BiometricResponse
            {
                RequestId = command.RequestId,
                Status    = "error",
                Message   = "Tempo esgotado. Nenhum dedo detectado na 1ª captura."
            });
            return;
        }

        var base64Img1 = _hardware.GetImageAsBase64Png(image1);

        var (template1, _) = _hardware.CreateTemplate(image1);
        if (template1 is null)
        {
            await _client.SendCapturaResponseAsync(new BiometricResponse
            {
                RequestId = command.RequestId,
                Status    = "error",
                Message   = "Não foi possível gerar o template na 1ª captura. Tente novamente."
            });
            return;
        }

        // Espera o usuário levantar o dedo para não capturar a mesma colocação imediatamente
        await _client.SendCapturaResponseAsync(new BiometricResponse
        {
            RequestId = command.RequestId,
            Status    = "processing",
            Message   = "Remova o dedo do leitor..."
        });
        while (_hardware.IsFingerPresent() && !serviceCt.IsCancellationRequested)
        {
            await Task.Delay(200, serviceCt);
        }

        // 2ª Captura
        await _client.SendCapturaResponseAsync(new BiometricResponse
        {
            RequestId = command.RequestId,
            Status    = "capturing_second",
            Message   = "Coloque o mesmo dedo novamente para 2ª captura...",
            FingerprintImageBase64 = base64Img1
        });

        var image2 = await CaptureImageWithTimeoutAsync(serviceCt);
        if (image2 is null)
        {
            await _client.SendCapturaResponseAsync(new BiometricResponse
            {
                RequestId = command.RequestId,
                Status    = "error",
                Message   = "Tempo esgotado. Nenhum dedo detectado na 2ª captura."
            });
            return;
        }

        var base64Img2 = _hardware.GetImageAsBase64Png(image2);

        var (template2, _) = _hardware.CreateTemplate(image2);
        if (template2 is null)
        {
            await _client.SendCapturaResponseAsync(new BiometricResponse
            {
                RequestId = command.RequestId,
                Status    = "error",
                Message   = "Não foi possível gerar o template na 2ª captura. Tente novamente."
            });
            return;
        }

        var (matched, matchScore) = _hardware.VerifyMatch(image2, template1);
        if (!matched)
        {
            await _client.SendCapturaResponseAsync(new BiometricResponse
            {
                RequestId = command.RequestId,
                Status    = "error",
                Message   = "As duas capturas não correspondem. Tente novamente."
            });
            return;
        }

        var finalTemplate = template1.Length >= template2.Length ? template1 : template2;
        var finalBase64 = finalTemplate == template1 ? base64Img1 : base64Img2;

        // Delay artificial de 800ms para feedback natural
        await Task.Delay(800, serviceCt);

        await _client.SendCapturaResponseAsync(new BiometricResponse
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
        int maxWaitTime = 10000;
        int waitInterval = 1000;
        int elapsedTime = 0;

        while (!_hardware.IsInitialized && elapsedTime < maxWaitTime && !serviceCt.IsCancellationRequested)
        {
            await Task.Delay(waitInterval, serviceCt);
            elapsedTime += waitInterval;
            await _client.SendValidacaoResponseAsync(new BiometricResponse
            {
                RequestId = command.RequestId,
                Status    = "started",
                Message   = $"Aguardando leitor biométrico... ({elapsedTime / 1000}s)"
            });
        }

        if (!_hardware.IsInitialized)
        {
            await _client.SendValidacaoResponseAsync(new BiometricResponse
            {
                RequestId = command.RequestId,
                Status    = "error",
                Message   = "Leitor biometrico nao disponivel. Verifique a conexao USB e aguarde."
            });
            return;
        }

        if (command.Template is null || command.Template.Length == 0)
        {
            await _client.SendValidacaoResponseAsync(new BiometricResponse
            {
                RequestId = command.RequestId,
                Status    = "error",
                Message   = "Template nao fornecido para validacao."
            });
            return;
        }

        await _client.SendValidacaoResponseAsync(new BiometricResponse
        {
            RequestId = command.RequestId,
            Status    = "started",
            Message   = "Aguardando dedo no leitor..."
        });

        var image = await CaptureImageWithTimeoutAsync(serviceCt);
        if (image is null)
        {
            await _client.SendValidacaoResponseAsync(new BiometricResponse
            {
                RequestId = command.RequestId,
                Status    = "error",
                Message   = "Tempo esgotado. Nenhum dedo detectado."
            });
            return;
        }

        var base64Img = _hardware.GetImageAsBase64Png(image);

        var (matched, score) = _hardware.VerifyMatch(image, command.Template);

        // Delay artificial de 800ms para feedback natural
        await Task.Delay(800, serviceCt);

        await _client.SendValidacaoResponseAsync(new BiometricResponse
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

    private async Task<byte[]?> CaptureImageWithTimeoutAsync(CancellationToken serviceCt)
    {
        _currentOperationCts?.Cancel();
        _currentOperationCts?.Dispose();
        _currentOperationCts = CancellationTokenSource.CreateLinkedTokenSource(serviceCt);
        _client.CurrentOperationCts = _currentOperationCts;
        var operationCt = _currentOperationCts.Token;

        _hardware.SetLedState(green: true, red: false); // Acende o LED verde para sinalizar que está aguardando a biometria
        try
        {
            // Aguarda presença do dedo por até 120s (240 x 500ms).
            for (int i = 0; i < 240 && !operationCt.IsCancellationRequested; i++)
            {
                if (_hardware.IsFingerPresent())
                {
                    await Task.Delay(600, operationCt).ConfigureAwait(false);
                    if (!_hardware.IsFingerPresent())
                    {
                        continue;
                    }
                    return _hardware.CaptureImage();
                }
                await Task.Delay(500, operationCt).ConfigureAwait(false);
            }

            return null;
        }
        finally
        {
            _hardware.SetLedState(green: false, red: false); // Apaga os LEDs após concluir ou cancelar a operação
        }
    }

    private void OnReconectar(object? sender, EventArgs e)
    {
        _servidorConectado = false;
        _leitorConectado   = false;
        _estadoLeitor      = "Reconectando...";
        UpdateTray();
        Task.Run(StartAsync);
    }

    private static string GetLogPath()
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CMSO", "logs");
        return Path.Combine(logDir, "agente-biometria-ciclovida.log");
    }

    private void OnVerLogs(object? sender, EventArgs e)
    {
        var logPath = GetLogPath();
        if (File.Exists(logPath))
            System.Diagnostics.Process.Start("notepad.exe", logPath);
        else
            MessageBox.Show("Nenhum arquivo de log encontrado.", "CMSO Biometria",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
 
    private void OnInstalarDriver(object? sender, EventArgs e)
    {
        var driverInstallerPath = Path.Combine(AppContext.BaseDirectory, "driver", "ftrDriverSetup_win8_whql_3471.exe");
        if (File.Exists(driverInstallerPath))
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = driverInstallerPath,
                    UseShellExecute = true,
                    Verb = "runas" // Pede elevação de privilégios de administrador
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Falha ao iniciar o instalador do driver: {ex.Message}", "CMSO Biometria",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        else
        {
            MessageBox.Show("Arquivo de instalação do driver não encontrado.", "CMSO Biometria",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OnFechar(object? sender, EventArgs e)
    {
        ShowNotification("CMSO Biometria", "Agente Biométrico finalizado.", Color.FromArgb(100, 100, 110));
        Application.DoEvents();
        System.Threading.Thread.Sleep(500);
        _trayIcon.Visible = false;
        Application.Exit();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Atualização do ícone e menu
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Atualiza o ícone e o menu a partir de qualquer thread.
    /// Usa Invoke se necessário (WinForms é single-threaded).
    /// </summary>
    private void UpdateTrayFromThread()
    {
        if (_trayIcon.ContextMenuStrip?.InvokeRequired == true)
            _trayIcon.ContextMenuStrip.Invoke(UpdateTray);
        else
            UpdateTray();
    }

    private void UpdateTray()
    {
        string servidorEmoji = _servidorConectado ? "✅" : "❌";
        string leitorEmoji   = _leitorConectado   ? "✅" : "❌";

        _lblServidor.Text = $"Servidor: {servidorEmoji} {(_servidorConectado ? "Conectado" : "Offline")}";
        _lblLeitor.Text   = $"Leitor:   {leitorEmoji} {_estadoLeitor}";

        // Tooltip (máx 63 chars no Windows)
        var tooltip = _servidorConectado && _leitorConectado
            ? "CMSO Biometria — Pronto"
            : _servidorConectado
                ? "CMSO Biometria — Leitor desconectado"
                : "CMSO Biometria — Sem conexão com servidor";

        _trayIcon.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip;

        // Ícone colorido conforme estado
        _trayIcon.Icon = _servidorConectado && _leitorConectado ? _iconGreen
                       : _servidorConectado                     ? _iconYellow
                                                                : _iconRed;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Cria um Icon 32x32 a partir do PNG base (cmso_icone.png) com uma bolinha
    /// colorida no canto inferior direito indicando o estado do agente.
    /// </summary>
    private static (Icon Icon, IntPtr Handle) CreateStatusIcon(Color dotColor)
    {
        const int size = 32;
        var pngPath = Path.Combine(AppContext.BaseDirectory, "cmso_icone.png");

        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode     = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;

            // Desenha o ícone base
            if (File.Exists(pngPath))
            {
                using var src = new Bitmap(pngPath);
                // Converte para RGBA se necessário (PNG paleta modo P não suporta Graphics direto)
                using var rgba = src.Clone(new Rectangle(0, 0, src.Width, src.Height),
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                g.DrawImage(rgba, new Rectangle(0, 0, size - 10, size - 10));
            }
            else
            {
                g.FillRectangle(Brushes.DimGray, 0, 0, size - 10, size - 10);
                g.DrawString("C", new Font("Arial", 10, FontStyle.Bold), Brushes.White, 2f, 2f);
            }

            // Bolinha de status no canto inferior direito
            const int dotSize = 12;
            int dotX = size - dotSize;
            int dotY = size - dotSize;
            g.FillEllipse(Brushes.White, dotX - 1, dotY - 1, dotSize + 2, dotSize + 2);
            using var dotBrush = new SolidBrush(dotColor);
            g.FillEllipse(dotBrush, dotX, dotY, dotSize, dotSize);
        }
        catch
        {
            // fallback: retorna ícone padrão do sistema
            bmp.Dispose();
            return (SystemIcons.Application, IntPtr.Zero);
        }

        var hIcon = bmp.GetHicon();
        bmp.Dispose();

        var icon = Icon.FromHandle(hIcon);
        return (icon, hIcon);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Auto-start (registro no Windows)
    // ─────────────────────────────────────────────────────────────────────────

    private static string AutoStartKey => @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    private static bool IsAutoStartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(AutoStartKey);
        return key?.GetValue("CMSO Biometria") != null;
    }

    private static void SetAutoStart(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(AutoStartKey, writable: true);
        if (key is null) return;

        if (enable)
        {
            var exePath = Environment.ProcessPath;
            if (exePath is not null)
                key.SetValue("CMSO Biometria", $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue("CMSO Biometria", throwOnMissingValue: false);
        }
    }

    private void OnToggleAutoStart(object? sender, EventArgs e)
    {
        var ativo = !_menuAutoStart.Checked;
        SetAutoStart(ativo);
        _menuAutoStart.Checked = ativo;

        ShowNotification(
            ativo ? "Auto-start Ativado" : "Auto-start Desativado",
            ativo ? "O agente será iniciado automaticamente com o Windows." : "O agente não será mais iniciado com o Windows.",
            ativo ? Color.FromArgb(40, 167, 69) : Color.FromArgb(100, 100, 110));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Auto-update (Velopack)
    // ─────────────────────────────────────────────────────────────────────────

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            // Lê a URL de update do appsettings (ou usa um padrão)
            var config = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                .Build();

            var updateUrl = config["Biometric:UpdateUrl"];
            if (string.IsNullOrWhiteSpace(updateUrl))
                return; // Sem URL configurada — pula verificação

            UpdateManager mgr;
            if (updateUrl.Contains("github.com", StringComparison.OrdinalIgnoreCase))
            {
                var source = new Velopack.Sources.GithubSource(updateUrl, accessToken: null, prerelease: false);
                mgr = new UpdateManager(source);
            }
            else
            {
                mgr = new UpdateManager(updateUrl);
            }

            var updates = await mgr.CheckForUpdatesAsync();
            if (updates is null)
                return; // Nenhuma atualização disponível

            var targetRelease = updates.TargetFullRelease;
            _menu.BeginInvoke((Action)(() =>
            {
                var result = MessageBox.Show(
                    $"Nova versão disponível: {targetRelease.Version}\n\n" +
                    "Deseja baixar e instalar a atualização agora?",
                    "Atualização Disponível",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Information);

                if (result == DialogResult.Yes)
                {
                    _ = DownloadAndApplyUpdateAsync(mgr, updates);
                }
            }));
        }
        catch (Exception ex)
        {
            // Falha silenciosa — não interrompe o usuário
            System.Diagnostics.Debug.WriteLine($"Update check failed: {ex.Message}");
        }
    }

    private async Task DownloadAndApplyUpdateAsync(UpdateManager mgr, UpdateInfo updates)
    {
        try
        {
            ShowNotification("Atualizando", "Baixando atualização...", Color.FromArgb(0, 120, 215));

            await mgr.DownloadUpdatesAsync(updates);

            ShowNotification("Atualização Pronta",
                "A atualização será aplicada e o aplicativo será reiniciado.",
                Color.FromArgb(40, 167, 69));

            // Pequena pausa para o usuário ler a notificação
            await Task.Delay(1500);

            mgr.ApplyUpdatesAndRestart(updates);
        }
        catch (Exception ex)
        {
            ShowNotification("Erro na Atualização",
                "Não foi possível aplicar a atualização: " + ex.Message,
                Color.FromArgb(220, 53, 69));
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _appCts.Cancel();
            _appCts.Dispose();
            _currentOperationCts?.Cancel();
            _currentOperationCts?.Dispose();

            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _client.Dispose();
            _hardware.Dispose();
            _iconGreen.Dispose();
            _iconYellow.Dispose();
            _iconRed.Dispose();

            // Libera os handles de ícone nativos
            if (_hIconGreen != IntPtr.Zero) DestroyIcon(_hIconGreen);
            if (_hIconYellow != IntPtr.Zero) DestroyIcon(_hIconYellow);
            if (_hIconRed != IntPtr.Zero) DestroyIcon(_hIconRed);
        }
        base.Dispose(disposing);
    }
}
