Param(
    [ValidateSet('Windows','WSL','Both')]
    [string]$Target = 'Windows',
    [string]$ServerName = 'roslyn_code_navigator',
    [string]$LogLevel = 'Information',
    [string]$RoslynLogLevel = 'Debug',
    [int]$MaxProjectConcurrency = 4,
    [bool]$Interactive = $true
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Info($msg) { Write-Host "[INFO] $msg" -ForegroundColor Cyan }
function Write-Warn($msg) { Write-Host "[WARN] $msg" -ForegroundColor Yellow }
function Write-Err($msg)  { Write-Host "[ERROR] $msg" -ForegroundColor Red }

function Ensure-Dotnet() {
    $dotnet = Join-Path $Env:ProgramFiles 'dotnet\dotnet.exe'
    if (-not (Test-Path $dotnet)) {
        Write-Err "dotnet not found at $dotnet. Please install .NET 8 SDK or adjust the path."
        exit 1
    }
    return $dotnet
}

function Prompt-Interactive() {
    if (-not $Interactive) { return }

    Write-Host "=== Roslyn MCP Server Installer (Interactive) ===" -ForegroundColor Green

    # Target selection
    Write-Host "Select install target:" -ForegroundColor Cyan
    Write-Host "  [1] Windows  (default)"
    Write-Host "  [2] WSL"
    Write-Host "  [3] Both"
    $choice = Read-Host "Enter choice (1-3)"
    switch ($choice) {
        '2' { $script:Target = 'WSL' }
        '3' { $script:Target = 'Both' }
        default { $script:Target = 'Windows' }
    }

    $sn = Read-Host "Server name [$ServerName]"
    if (-not [string]::IsNullOrWhiteSpace($sn)) { $script:ServerName = $sn }

    $ll = Read-Host "LOG_LEVEL [$LogLevel]"
    if (-not [string]::IsNullOrWhiteSpace($ll)) { $script:LogLevel = $ll }

    $rll = Read-Host "ROSLYN_LOG_LEVEL [$RoslynLogLevel]"
    if (-not [string]::IsNullOrWhiteSpace($rll)) { $script:RoslynLogLevel = $rll }

    $mpc = Read-Host "ROSLYN_MAX_PROJECT_CONCURRENCY [$MaxProjectConcurrency]"
    if ($mpc -match '^[0-9]+$') { $script:MaxProjectConcurrency = [int]$mpc }
}

function Resolve-InstallDir() {
    $default = Join-Path $Env:ProgramFiles 'RoslynMcpServer'
    try {
        if (-not (Test-Path $default)) { New-Item -ItemType Directory -Force -Path $default | Out-Null }
        return $default
    } catch {
        $fallback = Join-Path $Env:LocalAppData 'RoslynMcpServer'
        Write-Warn "Falling back to per-user install dir: $fallback"
        if (-not (Test-Path $fallback)) { New-Item -ItemType Directory -Force -Path $fallback | Out-Null }
        return $fallback
    }
}

function Publish-Exe($dotnet, $outputDir) {
    Write-Info "Publishing self-contained EXE to $outputDir"
    & $dotnet publish "$(Join-Path $PSScriptRoot '..\RoslynMcpServer\RoslynMcpServer.csproj')" `
        -c Release -r win-x64 `
        -p:SelfContained=true -p:PublishSingleFile=true -p:PublishTrimmed=false `
        -o $outputDir | Out-Host
    $exe = Join-Path $outputDir 'RoslynMcpServer.exe'
    if (-not (Test-Path $exe)) {
        Write-Err "Publish did not produce $exe"
        exit 1
    }
    return $exe
}

function Build-TomlBlock-Windows($serverName, $exePath) {
    $literalPath = $exePath -replace '''',''''''
    @"
[mcp_servers.$serverName]
command = '$literalPath'
args = []
env = { DOTNET_ENVIRONMENT = "Production", LOG_LEVEL = "$LogLevel", ROSLYN_LOG_LEVEL = "$RoslynLogLevel", ROSLYN_VERBOSE_SECURITY_LOGS = "false", ROSLYN_MAX_PROJECT_CONCURRENCY = "$MaxProjectConcurrency" }
startup_timeout_sec = 30
tool_timeout_sec = 120
"@
}

function Build-TomlBlock-WSL($serverName, $exePath) {
    # Convert C:\... to /mnt/c/... if needed
    $mntPath = $exePath
    if ($exePath -match '^[A-Za-z]:') {
        $drive = $exePath.Substring(0,1).ToLower()
        $rest = $exePath.Substring(2) -replace '\\','/'
        $mntPath = "/mnt/$drive/$rest"
    }
    @"
[mcp_servers.$serverName]
command = "$mntPath"
args = []
env = { DOTNET_ENVIRONMENT = "Production", LOG_LEVEL = "$LogLevel", ROSLYN_LOG_LEVEL = "$RoslynLogLevel" }
startup_timeout_sec = 30
tool_timeout_sec = 120
"@
}

function Upsert-TomlSection($configPath, $sectionHeader, $newContent) {
    if (-not (Test-Path $configPath)) {
        New-Item -ItemType Directory -Force -Path (Split-Path $configPath) | Out-Null
        Set-Content -Path $configPath -Value $newContent -NoNewline
        return
    }
    $text = Get-Content -Raw -Path $configPath
    $escapedHeader = [System.Text.RegularExpressions.Regex]::Escape($sectionHeader)
    $pattern = "(?ms)^$escapedHeader[\s\S]*?(?=^\[|\z)"
    if ([System.Text.RegularExpressions.Regex]::IsMatch($text, $pattern)) {
        $updated = [System.Text.RegularExpressions.Regex]::Replace($text, $pattern, [System.Text.RegularExpressions.MatchEvaluator]{ param($m) $newContent })
        Set-Content -Path $configPath -Value $updated -NoNewline
    } else {
        if ($text.Length -gt 0 -and -not $text.EndsWith("`n")) { $text += "`n" }
        $text += $newContent
        Set-Content -Path $configPath -Value $text -NoNewline
    }
}

function Configure-WindowsToml($serverName, $exePath) {
    $configPath = Join-Path $Env:USERPROFILE ".codex\config.toml"
    $block = Build-TomlBlock-Windows $serverName $exePath
    Write-Info "Writing server entry to $configPath"
    Upsert-TomlSection -configPath $configPath -sectionHeader "[mcp_servers.$serverName]" -newContent $block
    return $configPath
}

function Configure-WSLToml($serverName, $exePath) {
    # Use wsl.exe to write ~/.codex/config.toml inside default distro
    $block = Build-TomlBlock-WSL $serverName $exePath
    $escaped = $block -replace '"','\"'
    Write-Info "Writing server entry to WSL ~/.codex/config.toml via wsl.exe"
    # Ensure config file exists in WSL
    Start-Process -FilePath wsl.exe -ArgumentList @('bash','-lc','mkdir -p ~/.codex && touch ~/.codex/config.toml') -Wait | Out-Null
    # Pipe the TOML via stdin in a second call (simplifies quoting)
    $cmd = "python3 - <<'PY'
import sys,re,os
cfg=os.path.expanduser('~/.codex/config.toml')
new=sys.stdin.read()
try:
    with open(cfg,'r',encoding='utf-8') as f: txt=f.read()
except FileNotFoundError:
    txt=''
pat=re.compile(r'(?ms)^(\[mcp_servers\.roslyn_code_navigator\][\s\S]*?)(?=^\[|\Z)')
if pat.search(txt):
    txt=pat.sub(new,txt)
else:
    if txt and not txt.endswith('\n'): txt+='\n'
    txt+=new
with open(cfg,'w',encoding='utf-8') as f: f.write(txt)
PY"
    $p = Start-Process -FilePath wsl.exe -ArgumentList @('bash','-lc',$cmd) -NoNewWindow -PassThru -RedirectStandardInput pipe
    $sw = New-Object System.IO.StreamWriter($p.StandardInput.BaseStream)
    $sw.Write($block)
    $sw.Close()
    $p.WaitForExit()
    return "~/.codex/config.toml (WSL)"
}

# Main with progress
$actions = New-Object System.Collections.Generic.List[string]
$stage = 0; $total = 6

function Show-Progress([string]$activity,[string]$status) {
    $percent = [int](($stage / $total) * 100)
    Write-Progress -Activity $activity -Status $status -PercentComplete $percent
}

Prompt-Interactive

$stage = 1; Show-Progress "Environment" "Checking .NET SDK"
$dotnet = Ensure-Dotnet
$actions.Add("dotnet detected: $dotnet")

$stage = 2; Show-Progress "Install directory" "Resolving target folder"
$installDir = Resolve-InstallDir
$actions.Add("Install directory: $installDir")

$stage = 3; Show-Progress "Build/Publish" "Publishing self-contained EXE"
$exePath = Publish-Exe -dotnet $dotnet -outputDir $installDir
$actions.Add("Published EXE: $exePath")

$windowsConfigPath = $null
$wslConfigPath = $null

$stage = 4; Show-Progress "Configure" "Updating Codex TOML (Windows)"
switch ($Target) {
    'Windows' {
        $windowsConfigPath = Configure-WindowsToml -serverName $ServerName -exePath $exePath
        $actions.Add("Updated Windows TOML: $windowsConfigPath -> [$ServerName]")
    }
    'WSL'     {
        $wslConfigPath = Configure-WSLToml -serverName $ServerName -exePath $exePath
        $actions.Add("Updated WSL TOML: $wslConfigPath -> [$ServerName]")
    }
    'Both'    {
        $windowsConfigPath = Configure-WindowsToml -serverName $ServerName -exePath $exePath
        $actions.Add("Updated Windows TOML: $windowsConfigPath -> [$ServerName]")
        try {
            $wslConfigPath = Configure-WSLToml -serverName $ServerName -exePath $exePath
            $actions.Add("Updated WSL TOML: $wslConfigPath -> [$ServerName]")
        } catch {
            Write-Warn "WSL config update failed: $($_.Exception.Message). You can run scripts/install-wsl.sh inside WSL to configure manually."
        }
    }
}

$stage = 5; Show-Progress "Finalize" "Writing summary"
Write-Progress -Activity "Complete" -Completed

Write-Host "`n=== Installation Summary ===" -ForegroundColor Green
Write-Host "Target            : $Target"
Write-Host "Server Name       : $ServerName"
Write-Host "Log Level         : $LogLevel"
Write-Host "Roslyn Log Level  : $RoslynLogLevel"
Write-Host "Max Concurrency   : $MaxProjectConcurrency"
Write-Host "EXE Path          : $exePath"
if ($windowsConfigPath) { Write-Host "Windows TOML      : $windowsConfigPath" }
if ($wslConfigPath)     { Write-Host "WSL TOML          : $wslConfigPath" }

Write-Host "`nActions:" -ForegroundColor Cyan
$actions | ForEach-Object { Write-Host " - $_" }

Write-Host "`nStart the server: codex mcp start $ServerName" -ForegroundColor Cyan
