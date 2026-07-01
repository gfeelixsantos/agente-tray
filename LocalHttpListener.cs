using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Cmso.Biometric.Agent.Tray
{
    public class LocalHttpListener : IDisposable
    {
        private HttpListener? _listener;
        private readonly string _machineName;
        private readonly ILogger _logger;
        private CancellationTokenSource? _cts;

        public LocalHttpListener(string machineName, ILogger logger)
        {
            _machineName = machineName;
            _logger = logger;
        }

        public void Start(int port = 5163)
        {
            try
            {
                _listener = new HttpListener();
                // Escuta apenas conexões locais originadas do mesmo computador (loopback)
                _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                _listener.Start();
                _logger.LogInformation("Servidor HTTP local iniciado na porta {Port}", port);

                _cts = new CancellationTokenSource();
                _ = Task.Run(() => ListenAsync(_cts.Token));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao iniciar o servidor HTTP local na porta {Port}", port);
            }
        }

        private async Task ListenAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _listener != null && _listener.IsListening)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    _ = Task.Run(() => HandleRequestAsync(context), token);
                }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested) break;
                    _logger.LogDebug("Erro ao aceitar requisição HTTP: {Msg}", ex.Message);
                }
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext context)
        {
            var response = context.Response;

            // Adiciona cabeçalhos CORS para permitir requisições via navegador do Blazor Server
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            response.Headers.Add("Access-Control-Allow-Methods", "GET, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "*");

            if (context.Request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = (int)HttpStatusCode.OK;
                response.Close();
                return;
            }

            try
            {
                var path = context.Request.Url?.AbsolutePath;
                if (path != null && (path.Equals("/info", StringComparison.OrdinalIgnoreCase) ||
                                     path.Equals("/machine-name", StringComparison.OrdinalIgnoreCase) ||
                                     path.Equals("/", StringComparison.OrdinalIgnoreCase)))
                {
                    var json = $"{{\"machineName\": \"{_machineName}\"}}";
                    var buffer = Encoding.UTF8.GetBytes(json);

                    response.ContentType = "application/json; charset=utf-8";
                    response.ContentLength64 = buffer.Length;
                    response.StatusCode = (int)HttpStatusCode.OK;

                    await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                }
                else
                {
                    response.StatusCode = (int)HttpStatusCode.NotFound;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao processar resposta no HttpListener");
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
            }
            finally
            {
                response.Close();
            }
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            try
            {
                if (_listener != null && _listener.IsListening)
                {
                    _listener.Stop();
                    _listener.Close();
                }
            }
            catch {}
        }
    }
}
