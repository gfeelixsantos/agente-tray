using Cmso.Biometric.Agent.Service;
using Cmso.Biometric.Agent.Core.Hardware;
using Cmso.Biometric.Agent.Core.Communication;

var builder = Host.CreateApplicationBuilder(args);

// Configuração
builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

// Serviços
builder.Services.AddSingleton<BiometricHardwareService>();
builder.Services.AddSingleton<BiometricSignalRClient>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
