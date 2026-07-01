using Microsoft.Extensions.Logging;

namespace Cmso.Biometric.Agent.Core.Hardware
{
    /// <summary>
    /// Serviço de integração com o leitor biométrico Futronic FS80H.
    ///
    /// Ciclo de vida do handle:
    ///   Initialize() → abre handle → captura/verificação → Dispose() fecha handle
    ///
    /// Thread-safety:
    ///   Todas as chamadas à DLL passam pelo _semaforo (SemaphoreSlim 1,1).
    ///   IsFingerPresent usa Wait(0) para não bloquear — retorna false se captura ativa.
    /// </summary>
    public class BiometricHardwareService : IDisposable
    {
        private readonly ILogger<BiometricHardwareService> _logger;

        private IntPtr _handle = IntPtr.Zero;
        private bool   _isInitialized;
        private FtrScanApi.TamanhoImagem _imageSize;

        private readonly SemaphoreSlim _semaforo = new(1, 1);

        /// <summary>Expõe estado real do hardware para BiometricSignalRClient.SendStatusAsync.</summary>
        public bool IsInitialized => _isInitialized;

        public BiometricHardwareService(ILogger<BiometricHardwareService> logger)
        {
            _logger = logger;
        }

        // ─────────────────────────────────────────────────────────────────
        // Inicialização
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Abre o dispositivo e obtém as dimensões da imagem.
        /// Retorna false se o leitor não estiver conectado — o Worker fará retry.
        /// </summary>
        public bool Initialize()
        {
            try
            {
                _handle = FtrScanApi.ftrScanOpenDevice();

                if (_handle == IntPtr.Zero)
                {
                    int err = FtrScanApi.ftrScanGetLastError();
                    _logger.LogError("Falha ao abrir dispositivo biométrico (handle nulo). Código de erro: {Err}. " +
                                     "Verifique se o leitor está conectado via USB.", err);
                    return false;
                }

                // Obtém dimensões — necessário para CreateTemplate e VerifyMatch
                if (!FtrScanApi.ftrScanGetImageSize(_handle, out _imageSize) || _imageSize.nImageSize <= 0)
                {
                    _logger.LogError("Falha ao obter dimensões da imagem do leitor.");
                    CloseDevice();
                    return false;
                }

                _logger.LogInformation(
                    "Leitor biométrico inicializado com sucesso. " +
                    "Imagem: {W}x{H} ({Bytes} bytes)",
                    _imageSize.nWidth, _imageSize.nHeight, _imageSize.nImageSize);

                // Mantém LEDs desligados por padrão (acenderão sob demanda na captura)
                TrySetLed(green: false, red: false);

                _isInitialized = true;
                return true;
            }
            catch (DllNotFoundException ex)
            {
                _logger.LogError(ex,
                    "DLL Futronic não encontrada. " +
                    "Confirme que ftrScanAPI.dll está em: {Dir}", AppContext.BaseDirectory);
                return false;
            }
            catch (BadImageFormatException ex)
            {
                var arch = Environment.Is64BitProcess ? "x64" : "x86";
                _logger.LogError(ex,
                    "Arquitetura incompatível ({Arch}). " +
                    "O projeto deve compilar como x86 (PlatformTarget=x86).", arch);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro inesperado ao inicializar o leitor biométrico.");
                return false;
            }
        }

        /// <summary>
        /// Verifica se o leitor biométrico continua conectado e respondendo.
        /// Caso contrário, fecha o dispositivo e limpa o estado de inicialização.
        /// </summary>
        public bool CheckConnection()
        {
            if (!_isInitialized || _handle == IntPtr.Zero) return false;

            if (!_semaforo.Wait(0)) return true; // Se outra operação estiver em andamento (ex: captura), assume conectado
            try
            {
                // Tenta verificar presença de dedo para forçar comunicação USB com o hardware.
                bool ok = FtrScanApi.ftrScanIsFingerPresent(_handle, out _);
                if (!ok)
                {
                    int lastError = FtrScanApi.ftrScanGetLastError();
                    // 4306 é ERROR_EMPTY (leitor conectado mas sem dedo). Qualquer outro código de erro indica desconexão/falha.
                    if (lastError != 4306 && lastError != 0)
                    {
                        _logger.LogWarning("Teste de conexão com o leitor falhou (IsFingerPresent). Erro: {Err}. O leitor foi desconectado via USB.", lastError);
                        CloseDevice();
                        return false;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao verificar conexão com o leitor biométrico.");
                CloseDevice();
                return false;
            }
            finally
            {
                _semaforo.Release();
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // Detecção de dedo
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifica se há dedo no leitor usando dados de contraste.
        /// Usa Wait(0) — retorna false se outra operação estiver em andamento.
        /// </summary>
        public bool IsFingerPresent()
        {
            if (!_isInitialized || _handle == IntPtr.Zero) return false;

            // Não bloqueia: se captura ativa estiver rodando, pula
            if (!_semaforo.Wait(0)) return false;
            try
            {
                bool ok = FtrScanApi.ftrScanIsFingerPresent(_handle, out var frame);
                _logger.LogDebug("IsFingerPresent: ok={Ok}, nContrastOnDose2={Contrast}, threshold={Threshold}", ok, frame.nContrastOnDose2, FtrScanApi.ContrasteMinimoDeteccao);
                return ok && frame.nContrastOnDose2 > FtrScanApi.ContrasteMinimoDeteccao;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao verificar presença de dedo.");
                return false;
            }
            finally
            {
                _semaforo.Release();
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // Captura de imagem
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Captura a imagem da digital. Requer dedo presente (verifique IsFingerPresent antes).
        /// Retorna null em caso de falha.
        /// </summary>
        public byte[]? CaptureImage()
        {
            if (!_isInitialized || _handle == IntPtr.Zero)
            {
                _logger.LogWarning("CaptureImage chamado com leitor não inicializado.");
                return null;
            }

            _semaforo.Wait();
            try
            {
                var buffer = new byte[_imageSize.nImageSize];
                bool ok = FtrScanApi.ftrScanGetImage(_handle, FtrScanApi.DosePadrao, buffer);

                if (!ok)
                {
                    _logger.LogError("ftrScanGetImage falhou.");
                    return null;
                }

                _logger.LogDebug("Imagem capturada: {Bytes} bytes", buffer.Length);
                return buffer;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao capturar imagem biométrica.");
                return null;
            }
            finally
            {
                _semaforo.Release();
            }
        }

        public string? GetImageAsBase64Png(byte[] rawImage)
        {
            _logger.LogInformation("GetImageAsBase64Png iniciado. rawImage Length={Len}, Width={W}, Height={H}", 
                rawImage?.Length ?? 0, _imageSize.nWidth, _imageSize.nHeight);

            if (rawImage == null || rawImage.Length == 0 || _imageSize.nWidth <= 0 || _imageSize.nHeight <= 0)
            {
                _logger.LogWarning("GetImageAsBase64Png: Parâmetros inválidos.");
                return null;
            }

            try
            {
                using (var bmp = new System.Drawing.Bitmap(_imageSize.nWidth, _imageSize.nHeight, System.Drawing.Imaging.PixelFormat.Format8bppIndexed))
                {
                    var palette = bmp.Palette;
                    for (int i = 0; i < 256; i++)
                    {
                        palette.Entries[i] = System.Drawing.Color.FromArgb(i, i, i);
                    }
                    bmp.Palette = palette;

                    var bmpData = bmp.LockBits(
                        new System.Drawing.Rectangle(0, 0, _imageSize.nWidth, _imageSize.nHeight),
                        System.Drawing.Imaging.ImageLockMode.WriteOnly,
                        System.Drawing.Imaging.PixelFormat.Format8bppIndexed);
                    try
                    {
                        System.Runtime.InteropServices.Marshal.Copy(rawImage, 0, bmpData.Scan0, rawImage.Length);
                    }
                    finally
                    {
                        bmp.UnlockBits(bmpData);
                    }

                    using (var ms = new System.IO.MemoryStream())
                    {
                        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                        var base64 = Convert.ToBase64String(ms.ToArray());
                        _logger.LogInformation("GetImageAsBase64Png concluído com sucesso. Base64 Length={Len}", base64.Length);
                        return base64;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao converter imagem biométrica para Base64.");
                return null;
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // Template ANSI
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Gera template ANSI a partir de imagem bruta capturada.
        /// Retorna (null, 0) em caso de falha.
        /// QualityScore: estimativa baseada no tamanho do template (0–100).
        /// </summary>
        public (byte[]? template, int quality) CreateTemplate(byte[] image)
        {
            if (image == null || image.Length == 0)
            {
                _logger.LogWarning("CreateTemplate: imagem nula ou vazia.");
                return (null, 0);
            }

            if (!_isInitialized || _handle == IntPtr.Zero)
            {
                _logger.LogWarning("CreateTemplate: leitor não inicializado.");
                return (null, 0);
            }

            _semaforo.Wait();
            try
            {
                int maxSize = FtrAnsiSdk.ftrAnsiSdkGetMaxTemplateSize();
                if (maxSize <= 0)
                {
                    _logger.LogError("ftrAnsiSdkGetMaxTemplateSize retornou tamanho inválido: {S}", maxSize);
                    return (null, 0);
                }

                var templateBuffer = new byte[maxSize];
                int templateSize   = maxSize;

                bool ok = FtrAnsiSdk.ftrAnsiSdkCreateTemplateFromBuffer(
                    _handle,
                    FtrAnsiSdk.FingerPositionDefault,
                    image,
                    _imageSize.nWidth,
                    _imageSize.nHeight,
                    templateBuffer,
                    ref templateSize);

                if (!ok || templateSize <= 0)
                {
                    _logger.LogError("ftrAnsiSdkCreateTemplateFromBuffer falhou. ok={Ok} size={S}", ok, templateSize);
                    return (null, 0);
                }

                // Copiar apenas os bytes efetivos
                var template = new byte[templateSize];
                Array.Copy(templateBuffer, template, templateSize);

                _logger.LogInformation(
                    "Template criado: {Size} bytes (max={Max})",
                    templateSize, maxSize);

                // Qualidade não calculada — a DLL Futronic não expõe score NFIQ.
                // Retornamos 100 para indicar "template gerado com sucesso".
                // A qualidade real é avaliada pelo VerifyMatch (score de correspondência).
                return (template, 100);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao criar template ANSI.");
                return (null, 0);
            }
            finally
            {
                _semaforo.Release();
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // Matching 1:1
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifica correspondência entre imagem capturada e template armazenado.
        /// Gera template da imagem e compara com ftrAnsiSdkMatchTemplates.
        /// </summary>
        public (bool matched, int score) VerifyMatch(byte[] image, byte[] storedTemplate)
        {
            if (image == null || storedTemplate == null || storedTemplate.Length == 0)
            {
                _logger.LogWarning("VerifyMatch: parâmetros inválidos.");
                return (false, 0);
            }

            // Gera template da captura ao vivo
            var (liveTemplate, quality) = CreateTemplate(image);
            if (liveTemplate == null)
            {
                _logger.LogError("VerifyMatch: falha ao gerar template da imagem capturada.");
                return (false, 0);
            }

            _semaforo.Wait();
            try
            {
                bool ok = FtrAnsiSdk.ftrAnsiSdkMatchTemplates(liveTemplate, storedTemplate, out float score);

                if (!ok)
                {
                    _logger.LogError("ftrAnsiSdkMatchTemplates falhou.");
                    return (false, 0);
                }

                bool matched = score >= FtrAnsiSdk.MatchThresholdDefault;

                // Normaliza score float (0–189+) para escala 0–100 para o contrato da API
                int scoreInt = Math.Clamp((int)(score * 100f / 189f), 0, 100);

                _logger.LogInformation(
                    "Match: score={Raw:F1} (normalizado={Norm}/100) threshold={T} matched={M}",
                    score, scoreInt, FtrAnsiSdk.MatchThresholdDefault, matched);

                return (matched, scoreInt);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro no matching biométrico.");
                return (false, 0);
            }
            finally
            {
                _semaforo.Release();
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // LEDs
        // ─────────────────────────────────────────────────────────────────

        /// <summary>Define o estado dos LEDs verde e vermelho.</summary>
        public void SetLedState(bool green, bool red)
        {
            TrySetLed(green, red);
        }

        private void TrySetLed(bool green, bool red)
        {
            if (_handle == IntPtr.Zero) return;
            try
            {
                FtrScanApi.ftrScanSetDiodesStatus(
                    _handle,
                    (byte)(green ? 255 : 0),
                    (byte)(red   ? 255 : 0));
            }
            catch { /* LEDs são opcionais — não impede funcionamento */ }
        }

        // ─────────────────────────────────────────────────────────────────
        // Lifecycle
        // ─────────────────────────────────────────────────────────────────

        private void CloseDevice()
        {
            if (_handle == IntPtr.Zero) return;
            try
            {
                TrySetLed(green: false, red: false);
                FtrScanApi.ftrScanCloseDevice(_handle);
                _logger.LogInformation("Dispositivo biométrico fechado.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao fechar dispositivo biométrico.");
            }
            finally
            {
                _handle        = IntPtr.Zero;
                _isInitialized = false;
            }
        }

        public void Dispose()
        {
            _semaforo.Wait();
            try   { CloseDevice(); }
            finally
            {
                _semaforo.Release();
                _semaforo.Dispose();
            }
        }
    }
}
