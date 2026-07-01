using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.IO;
using Velopack;

namespace Cmso.Biometric.Agent.Tray;

public class FileLogger : ILogger
{
    private readonly string _logPath;
    public FileLogger(string logPath) => _logPath = logPath;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        try
        {
            var message = formatter(state, exception);
            var logLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{logLevel}] {message}";
            if (exception != null)
                logLine += Environment.NewLine + exception.ToString();
            File.AppendAllText(_logPath, logLine + Environment.NewLine);
        }
        catch {}
    }
}

public class FileLoggerProvider : ILoggerProvider
{
    private readonly string _logPath;
    public FileLoggerProvider(string logPath) => _logPath = logPath;
    public ILogger CreateLogger(string categoryName) => new FileLogger(_logPath);
    public void Dispose() {}
}

static class Program
{
    private const string AppMutexName = @"Global\CmsoBiometricAgentTray";

    [STAThread]
    static void Main()
    {
        // Single-instance check
        using var mutex = new Mutex(true, AppMutexName, out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("O CMSO Agente Biométrico já está em execução.",
                "CMSO Biometria", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Velopack hooks — executa no start após instalação/atualização
        VelopackApp.Build()
            .OnFirstRun(v => HandleFirstRun())
            .Run();

        ApplicationConfiguration.Initialize();

        // Log path: %LOCALAPPDATA%\CMSO\logs (evita problemas de permissão em Program Files)
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CMSO", "logs");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "agente-biometria-ciclovida.log");

        // Carrega configuração do appsettings.json
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .Build();

        var serverUrl = config["Biometric:HubUrl"] ?? "http://localhost:5163";

        // Limpa o arquivo de log no startup
        try { File.WriteAllText(logPath, ""); } catch {}

        // Logger para console + arquivo
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.AddProvider(new FileLoggerProvider(logPath));
            builder.SetMinimumLevel(LogLevel.Information);
        });

        var logger = loggerFactory.CreateLogger("TrayApp");

        logger.LogInformation("CMSO Agente Biométrico (Tray) iniciando...");
        logger.LogInformation("HubUrl: {Url}", serverUrl);

        // Roda sem janela principal — apenas o ícone na bandeja do sistema
        Application.Run(new TrayApplicationContext(serverUrl, loggerFactory));

        logger.LogInformation("CMSO Agente Biométrico (Tray) encerrado.");
    }

    private static void HandleFirstRun()
    {
        // Executado na primeira vez após instalação ou atualização
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CMSO", "logs");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "agente-biometria-ciclovida.log");
        try
        {
            File.AppendAllText(logPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [INFO] " +
                $"Primeira execução após instalação/atualização.{Environment.NewLine}");
        }
        catch {}

        // Prompt para instalação do driver do leitor biométrico
        var driverInstallerPath = Path.Combine(AppContext.BaseDirectory, "driver", "ftrDriverSetup_win8_whql_3471.exe");
        if (File.Exists(driverInstallerPath))
        {
            var result = MessageBox.Show(
                "Para que o leitor biométrico funcione corretamente, é necessário instalar o driver do dispositivo.\n\nDeseja iniciar a instalação do driver agora?",
                "CMSO Biometria - Instalação de Driver",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (result == DialogResult.Yes)
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
        }
    }
}
