param(
    [ValidateSet("Auto", "Codex", "Claude", "Both")]
    [string]$Clients = "Auto",
    [switch]$SkipClientRegistration
)

$ErrorActionPreference = "Stop"

function Invoke-CodexCli {
    param(
        [string]$Executable,
        [string[]]$Arguments
    )

    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $output = & $Executable @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    return [PSCustomObject]@{
        ExitCode = $exitCode
        Output = @($output | ForEach-Object { $_.ToString() })
    }
}

function Find-CodexCli {
    $codexCommand = Get-Command codex -ErrorAction SilentlyContinue
    if ($codexCommand) {
        return $codexCommand.Source
    }

    $codexBinDirectory = Join-Path $env:LOCALAPPDATA "OpenAI\Codex\bin"
    if (Test-Path -LiteralPath $codexBinDirectory) {
        $candidate = Get-ChildItem -LiteralPath $codexBinDirectory -Directory |
            Sort-Object LastWriteTime -Descending |
            ForEach-Object {
                $path = Join-Path $_.FullName "codex.exe"
                if (Test-Path -LiteralPath $path) {
                    $path
                }
            } |
            Select-Object -First 1
        if ($candidate) {
            return $candidate
        }
    }

    return $null
}

function Invoke-ClaudeCli {
    param(
        [string]$Executable,
        [string[]]$Arguments
    )

    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $output = & $Executable @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    return [PSCustomObject]@{
        ExitCode = $exitCode
        Output = @($output | ForEach-Object { $_.ToString() })
    }
}

$codexPath = $null
$claudePath = $null
$selectedClients = @()
if (-not $SkipClientRegistration) {
    $codexPath = Find-CodexCli
    $claudeCommand = Get-Command claude -ErrorAction SilentlyContinue
    if ($claudeCommand) {
        $claudePath = $claudeCommand.Source
    }

    switch ($Clients) {
        "Auto" {
            if ($codexPath) {
                $selectedClients += "Codex"
            }
            if ($claudePath) {
                $selectedClients += "Claude"
            }
        }
        "Codex" { $selectedClients = @("Codex") }
        "Claude" { $selectedClients = @("Claude") }
        "Both" { $selectedClients = @("Codex", "Claude") }
    }

    if ($selectedClients -contains "Codex" -and -not $codexPath) {
        throw "Codex CLI was not found. Install Codex or use -Clients Auto to install only clients found on this machine."
    }
    if ($selectedClients -contains "Claude" -and -not $claudePath) {
        throw "Claude Code CLI was not found. Install Claude Code or use -Clients Auto to install only clients found on this machine."
    }
}

$projectPath = Join-Path $PSScriptRoot "src\Kid.Mcp\Kid.Mcp.csproj"
if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "KID project was not found at '$projectPath'. Run this script from a KID source checkout."
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    throw ".NET 10 SDK is required to install KID from source. Install it from https://dotnet.microsoft.com/download/dotnet/10.0 and try again."
}

$existingCodexServer = $null
if ($selectedClients -contains "Codex") {
    $existingResult = Invoke-CodexCli -Executable $codexPath -Arguments @("mcp", "get", "kid", "--json")
    if ($existingResult.ExitCode -eq 0) {
        $existingCodexServer = ($existingResult.Output -join [Environment]::NewLine) | ConvertFrom-Json
        if ($existingCodexServer.transport.command -ne (Join-Path $env:LOCALAPPDATA "Programs\KID\Kid.Mcp.exe")) {
            throw "A different MCP server named 'kid' is already configured in Codex. Remove or rename it before installing KID."
        }
    }
    elseif (($existingResult.Output -join [Environment]::NewLine) -notmatch "No MCP server named 'kid' found") {
        throw "Could not inspect the existing Codex MCP configuration: $($existingResult.Output -join [Environment]::NewLine)"
    }
}

$existingClaudeServer = $null
if ($selectedClients -contains "Claude") {
    $claudeConfigPath = Join-Path $HOME ".claude.json"
    if (Test-Path -LiteralPath $claudeConfigPath) {
        try {
            $claudeConfig = Get-Content -LiteralPath $claudeConfigPath -Raw | ConvertFrom-Json
        }
        catch {
            throw "Could not parse Claude Code configuration at '$claudeConfigPath': $($_.Exception.Message)"
        }
        if ($claudeConfig.mcpServers) {
            $existingClaudeServer = $claudeConfig.mcpServers.kid
        }
    }
    if ($existingClaudeServer -and $existingClaudeServer.command -ne (Join-Path $env:LOCALAPPDATA "Programs\KID\Kid.Mcp.exe")) {
        throw "A different MCP server named 'kid' is already configured for Claude Code. Remove or rename it before installing KID."
    }
}

$skillSource = Join-Path $PSScriptRoot ".agents\skills\kid-accessibility"
$skillAvailable = Test-Path -LiteralPath (Join-Path $skillSource "SKILL.md")
if (-not $skillAvailable -and $selectedClients.Count -gt 0) {
    Write-Warning "KID skill instructions were not found. The MCP server will still be registered, but client skill instructions will not be installed."
}

$installDirectory = Join-Path $env:LOCALAPPDATA "Programs\KID"
$executablePath = Join-Path $installDirectory "Kid.Mcp.exe"
$publishDirectory = Join-Path $env:TEMP ("kid-publish-" + [Guid]::NewGuid().ToString("N"))

try {
    New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
    & $dotnet.Source publish $projectPath `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        --output $publishDirectory `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true

    if ($LASTEXITCODE -ne 0) {
        throw "Publishing KID failed with exit code $LASTEXITCODE."
    }

    $publishedExecutable = Join-Path $publishDirectory "Kid.Mcp.exe"
    if (-not (Test-Path -LiteralPath $publishedExecutable)) {
        throw "The KID executable was not produced at '$publishedExecutable'."
    }

    New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
    # Stage a complete file before replacing the launch path. A running client
    # can keep its old executable open until it reconnects.
    $stagedExecutable = Join-Path $installDirectory ("Kid.Mcp-next-" + [Guid]::NewGuid().ToString("N") + ".exe")
    $previousExecutable = Join-Path $installDirectory ("Kid.Mcp-previous-" + [Guid]::NewGuid().ToString("N") + ".exe")
    Copy-Item -LiteralPath $publishedExecutable -Destination $stagedExecutable
    if ((Get-FileHash -LiteralPath $publishedExecutable).Hash -ne (Get-FileHash -LiteralPath $stagedExecutable).Hash) {
        throw "The staged executable did not match the build."
    }
    $hadPreviousExecutable = Test-Path -LiteralPath $executablePath
    if ($hadPreviousExecutable) {
        Move-Item -LiteralPath $executablePath -Destination $previousExecutable
    }
    try {
        Move-Item -LiteralPath $stagedExecutable -Destination $executablePath
    }
    catch {
        if ($hadPreviousExecutable -and -not (Test-Path -LiteralPath $executablePath)) {
            Move-Item -LiteralPath $previousExecutable -Destination $executablePath
        }
        throw
    }
    if ($hadPreviousExecutable) {
        Write-Host "Previous executable retained at: $previousExecutable"
    }
}
finally {
    if (Test-Path -LiteralPath $publishDirectory) {
        $resolvedPublish = [IO.Path]::GetFullPath($publishDirectory)
        $temporaryRoot = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\'
        if (-not $resolvedPublish.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove a publish directory outside the temporary folder."
        }
        Remove-Item -LiteralPath $publishDirectory -Recurse -Force
    }
}

if ($skillAvailable -and $selectedClients -contains "Codex") {
    $codexSkillDirectory = Join-Path $HOME ".agents\skills\kid-accessibility"
    New-Item -ItemType Directory -Path $codexSkillDirectory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $skillSource "SKILL.md") -Destination $codexSkillDirectory -Force
}

if ($skillAvailable -and $selectedClients -contains "Claude") {
    $claudeSkillDirectory = Join-Path $HOME ".claude\skills\kid-accessibility"
    New-Item -ItemType Directory -Path $claudeSkillDirectory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $skillSource "SKILL.md") -Destination $claudeSkillDirectory -Force
}

if ($selectedClients -contains "Codex") {
    if (-not $existingCodexServer) {
        $addResult = Invoke-CodexCli -Executable $codexPath -Arguments @("mcp", "add", "kid", "--", $executablePath)
        if ($addResult.ExitCode -ne 0) {
            throw "Codex failed to register the KID MCP server: $($addResult.Output -join [Environment]::NewLine)"
        }
    }
}

if ($selectedClients -contains "Claude") {
    if (-not $existingClaudeServer) {
        $addResult = Invoke-ClaudeCli -Executable $claudePath -Arguments @(
            "mcp", "add", "--scope", "user", "--transport", "stdio", "kid", "--", $executablePath
        )
        if ($addResult.ExitCode -ne 0) {
            throw "Claude Code failed to register the KID MCP server: $($addResult.Output -join [Environment]::NewLine)"
        }
    }
}

Write-Host ""
Write-Host "KID was installed to: $executablePath"
if ($SkipClientRegistration) {
    Write-Host "Client registration skipped."
}
else {
    foreach ($client in $selectedClients) {
        Write-Host "KID MCP server registered with $client."
    }
    if ($selectedClients.Count -eq 0) {
        Write-Warning "Neither Codex nor Claude Code was found. Install a client, then rerun this script, or register the server manually."
    }
    elseif ($selectedClients.Count -lt 2) {
        $missingClient = if ($selectedClients -contains "Codex") { "Claude Code" } else { "Codex" }
        Write-Host "$missingClient was not detected; install it and rerun this script to register KID there too."
    }
    Write-Host "Restart the configured AI clients to load the MCP server and skill."
}
if ($skillAvailable -and $selectedClients -contains "Codex") {
    Write-Host "Codex skill: $(Join-Path $HOME '.agents\skills\kid-accessibility')"
}
if ($skillAvailable -and $selectedClients -contains "Claude") {
    Write-Host "Claude Code skill: $(Join-Path $HOME '.claude\skills\kid-accessibility')"
}
