param(
    [string]$Config = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputRoot = "$env:USERPROFILE\.ros-code-nav"
)

# Resolve dotnet (prefer Windows SDK path hinted by NEXPORT_WINDOTNET)
$dotnet = if ($env:NEXPORT_WINDOTNET) {
    Join-Path $env:NEXPORT_WINDOTNET "dotnet.exe"
} else {
    "dotnet"
}

$csproj = Join-Path $PSScriptRoot "..\RoslynMcpServer\RoslynMcpServer.csproj"
$publishDir = $OutputRoot

if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

& $dotnet publish $csproj -c $Config -r $Runtime --self-contained:false /p:PublishSingleFile=false -o $publishDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$exeWin = Join-Path $publishDir "RoslynMcpServer.exe"
# Convert C:\path to /mnt/c/path for WSL configs
$drive = $exeWin.Substring(0,1).ToLowerInvariant()
$pathRest = $exeWin.Substring(2).Replace('\','/')
$exeWsl = "/mnt/$drive$pathRest"

$toml = @"
[mcp_servers.roslyn_code_navigator]
name = "Roslyn Code Navigator"
command = "$exeWsl"
# OPTIONAL LOGGING
# env = { ROSLYN_LOG_LEVEL = "Debug", ROSLYN_VERBOSE_SECURITY_LOGS = "true" }
# Optional overrides
startup_timeout_sec = 30
tool_timeout_sec = 120
"@

Write-Host "`nPublish output: $publishDir"
Write-Host "WSL TOML snippet (copy/paste into your MCP config):"
Write-Host $toml
