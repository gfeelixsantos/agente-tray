param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $false)]
    [string]$Configuration = "Release",

    [Parameter(Mandatory = $false)]
    [string]$Runtime = "win-x86",

    [Parameter(Mandatory = $false)]
    [string]$OutputDir = "Releases",

    [Parameter(Mandatory = $false)]
    [string]$PublishDir = "publish",

    [Parameter(Mandatory = $false)]
    [string]$HubUrl = "https://agenda.cmsocupacional.com.br",

    [Parameter(Mandatory = $false)]
    [string]$UpdateUrl = "",

    [Parameter(Mandatory = $false)]
    [string]$Channel = "win"
)

$ProjectRoot = Split-Path $PSScriptRoot -Parent
$TrayProject = Join-Path $ProjectRoot "Cmso.Biometric.Agent.Tray\Cmso.Biometric.Agent.Tray.csproj"
$SolutionDir = $ProjectRoot
$PublishPath = Join-Path $ProjectRoot $PublishDir
$OutputPath = Join-Path $ProjectRoot $OutputDir

Write-Host "=== CMSO Agente Biométrico - Pack Release ===" -ForegroundColor Cyan
Write-Host "Versão: $Version"
Write-Host "HubUrl: $HubUrl"
Write-Host "UpdateUrl: $UpdateUrl"
Write-Host ""

# 1. Atualiza appsettings.json com os valores de produção
$AppSettingsPath = Join-Path $ProjectRoot "Cmso.Biometric.Agent.Tray\appsettings.json"
$AppSettings = Get-Content $AppSettingsPath -Raw | ConvertFrom-Json
$AppSettings.Biometric.HubUrl = $HubUrl
if (-not [string]::IsNullOrWhiteSpace($UpdateUrl)) {
    $AppSettings.Biometric.UpdateUrl = $UpdateUrl
}
$AppSettings | ConvertTo-Json -Depth 10 | Set-Content $AppSettingsPath -Encoding UTF8
Write-Host "[OK] appsettings.json atualizado" -ForegroundColor Green

# 2. Publica o projeto como self-contained
Write-Host ""
Write-Host "Publicando aplicação..." -ForegroundColor Yellow
dotnet publish $TrayProject `
    -c $Configuration `
    --self-contained `
    -r $Runtime `
    -o $PublishPath `
    -p:Version=$Version `
    -p:AssemblyVersion=$Version `
    -p:FileVersion=$Version

if ($LASTEXITCODE -ne 0) {
    Write-Host "[ERRO] Falha na publicação" -ForegroundColor Red
    exit 1
}
Write-Host "[OK] Publicação concluída" -ForegroundColor Green

# 3. Executa vpk pack para gerar o instalador
Write-Host ""
Write-Host "Gerando instalador Velopack..." -ForegroundColor Yellow

vpk pack `
    --packId CmsoBiometricAgent `
    --packVersion $Version `
    --packDir $PublishPath `
    --mainExe Cmso.Biometric.Agent.Tray.exe `
    --packTitle "CMSO Agente Biométrico" `
    --packAuthors CMSO `
    --icon (Join-Path $ProjectRoot "Cmso.Biometric.Agent.Tray\cmso_icone.ico") `
    --outputDir $OutputPath `
    --channel $Channel

if ($LASTEXITCODE -ne 0) {
    Write-Host "[ERRO] Falha ao gerar instalador" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "=== Release $Version gerada com sucesso! ===" -ForegroundColor Cyan
Write-Host "Instalador: $OutputPath"
Write-Host "Para distribuir, faça upload do conteúdo de: $OutputPath"
Write-Host "   - $OutputPath\CmsoBiometricAgent-$Version-full.nupkg"
Write-Host "   - $OutputPath\CmsoBiometricAgent-$Version-delta.nupkg (se houver)"
Write-Host "   - $OutputPath\Setup.exe"
Write-Host "   - $OutputPath\releases.win.json"
