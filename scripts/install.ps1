Param(
    [ValidateSet('Windows','WSL','Both')]
    [string]$Target = 'Windows',
    [string]$ServerName = 'roslyn_code_navigator',
    [string]$LogLevel = 'Information',
    [string]$RoslynLogLevel = 'Debug',
    [int]$MaxProjectConcurrency = 4
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

function Escape-TomlPath($path) {
    # TOML needs backslashes escaped
    return ($path -replace '\\','\\\\')
}

function Build-TomlBlock-Windows($serverName, $exePath) {
    $p = Escape-TomlPath $exePath
    @"
[mcp_servers.$serverName]
command = "$p"
args = []
env = {
  DOTNET_ENVIRONMENT = "Production",
  LOG_LEVEL = "$LogLevel",
  ROSLYN_LOG_LEVEL = "$RoslynLogLevel",
  ROSLYN_VERBOSE_SECURITY_LOGS = "false",
  ROSLYN_MAX_PROJECT_CONCURRENCY = "$MaxProjectConcurrency"
}
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
    $pattern = "(?ms)^(\Q$sectionHeader\E[\s\S]*?)(?=^\[|\z)"
    if ($text -match $pattern) {
        $updated = [System.Text.RegularExpressions.Regex]::Replace($text, $pattern, $newContent)
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
}

function Configure-WSLToml($serverName, $exePath) {
    # Use wsl.exe to write ~/.codex/config.toml inside default distro
    $block = Build-TomlBlock-WSL $serverName $exePath
    $escaped = $block -replace '"','\"'
    Write-Info "Writing server entry to WSL ~/.codex/config.toml via wsl.exe"
    $script = "mkdir -p ~/.codex && touch ~/.codex/config.toml && python3 - "$(@'
import sys,re,os
cfg=os.path.expanduser("~/.codex/config.toml")
new=sys.stdin.read()
try:
    with open(cfg,'r',encoding='utf-8') as f: txt=f.read()
except FileNotFoundError:
    txt=''
pat=re.compile(r'(?ms)^(\[mcp_servers\.roslyn_code_navigator\][\s\S]*?)(?=^\[|\Z)')
if pat.search(txt):
    txt=pat.sub(new,txt)
else:
    if txt and not txt.endswith("\n"): txt+="\n"
    txt+=new
with open(cfg,'w',encoding='utf-8') as f: f.write(txt)
'@)
""
    wsl.exe bash -lc $script | Out-Null
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
}

# Main
$dotnet = Ensure-Dotnet
$installDir = Resolve-InstallDir
$exePath = Publish-Exe -dotnet $dotnet -outputDir $installDir

switch ($Target) {
    'Windows' { Configure-WindowsToml -serverName $ServerName -exePath $exePath }
    'WSL'     { Configure-WSLToml     -serverName $ServerName -exePath $exePath }
    'Both'    {
        Configure-WindowsToml -serverName $ServerName -exePath $exePath
        try { Configure-WSLToml -serverName $ServerName -exePath $exePath } catch { Write-Warn "WSL config update failed: $($_.Exception.Message). You can run scripts/install-wsl.sh inside WSL to configure manually." }
    }
}

Write-Info "Installation complete. EXE: $exePath"
Write-Info "You can start the server with: codex mcp start $ServerName"

