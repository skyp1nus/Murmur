#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Downloads the models Murmur needs into $env:MURMUR_MODELS_DIR.

.DESCRIPTION
    Default directory: $env:LOCALAPPDATA\Murmur\models on Windows, ~/.murmur/models elsewhere.
      - Parakeet TDT v3 int8 (sherpa-onnx) -> <dir>/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/
      - Silero VAD                         -> <dir>/silero_vad.onnx
    With -Ai (phase 3, AI search and chat) additionally:
      - llama.cpp b11716 server (CPU build) -> <dir>/llama.cpp/ (llama-server(.exe) and its shared libraries)
      - Gemma 4 E4B instruct Q4_K_M and EmbeddingGemma 300M Q8_0 (GGUF, Hugging Face) -> <dir>/gguf/
        plus <dir>/gguf/models.json { chat, embed, repo, file }
      The MURMUR_LLM_MODEL / MURMUR_EMBED_MODEL / MURMUR_LLAMA_SERVER values to use are printed at the end.
      HF_TOKEN (optional) is sent to Hugging Face for gated repositories.
    Idempotent (existing files are skipped), retries with backoff, throws on any error.
    Extraction uses tar (bsdtar ships with Windows 10 1803+).
#>
[CmdletBinding()]
param(
    [int] $Retries = 4,
    [switch] $Ai
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest progress rendering is extremely slow

$baseUrl = 'https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models'
$parakeetName = 'sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8'

$modelsDir = $env:MURMUR_MODELS_DIR
if ([string]::IsNullOrWhiteSpace($modelsDir)) {
    $onWindows = $PSVersionTable.PSVersion.Major -lt 6 -or $IsWindows
    if ($onWindows) {
        $modelsDir = Join-Path $env:LOCALAPPDATA 'Murmur\models'
    }
    else {
        $modelsDir = Join-Path $HOME '.murmur/models'
    }
}

function Write-Log([string] $Message) { Write-Host "[fetch-models] $Message" }

function Save-Download([string] $Url, [string] $Dest, [hashtable] $Headers = @{}) {
    $part = "$Dest.part"
    $delay = 2
    for ($attempt = 1; $attempt -le $Retries; $attempt++) {
        try {
            if (Test-Path -LiteralPath $part) { Remove-Item -LiteralPath $part -Force }
            Invoke-WebRequest -Uri $Url -OutFile $part -Headers $Headers -UseBasicParsing
            Move-Item -LiteralPath $part -Destination $Dest -Force
            return
        }
        catch {
            if (Test-Path -LiteralPath $part) { Remove-Item -LiteralPath $part -Force -ErrorAction SilentlyContinue }
            if ($attempt -ge $Retries) {
                throw "Download failed after $Retries attempts: $Url ($($_.Exception.Message))"
            }
            Write-Log "download failed (attempt $attempt/$Retries), retrying in ${delay}s: $Url"
            Start-Sleep -Seconds $delay
            $delay *= 2
        }
    }
}

New-Item -ItemType Directory -Force -Path $modelsDir | Out-Null
Write-Log "models directory: $modelsDir"

# --- Parakeet TDT v3 int8 ---------------------------------------------------
$parakeetDir = Join-Path $modelsDir $parakeetName
if (Test-Path -LiteralPath (Join-Path $parakeetDir 'encoder.int8.onnx') -PathType Leaf) {
    Write-Log "skip: $parakeetName already present"
}
else {
    $tmp = Join-Path $modelsDir (".fetch." + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tmp | Out-Null
    try {
        $archive = Join-Path $tmp "$parakeetName.tar.bz2"
        Write-Log "downloading $baseUrl/$parakeetName.tar.bz2"
        Save-Download "$baseUrl/$parakeetName.tar.bz2" $archive
        Write-Log 'extracting'
        & tar -xjf $archive -C $tmp
        if ($LASTEXITCODE -ne 0) { throw "tar failed with exit code $LASTEXITCODE" }
        $extracted = Join-Path $tmp $parakeetName
        if (-not (Test-Path -LiteralPath (Join-Path $extracted 'encoder.int8.onnx') -PathType Leaf)) {
            throw "Archive does not contain $parakeetName/encoder.int8.onnx"
        }
        if (Test-Path -LiteralPath $parakeetDir) { Remove-Item -LiteralPath $parakeetDir -Recurse -Force }
        Move-Item -LiteralPath $extracted -Destination $parakeetDir
        Write-Log "installed $parakeetDir"
    }
    finally {
        Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# --- Silero VAD ---------------------------------------------------------------
$vadFile = Join-Path $modelsDir 'silero_vad.onnx'
if ((Test-Path -LiteralPath $vadFile -PathType Leaf) -and (Get-Item -LiteralPath $vadFile).Length -gt 0) {
    Write-Log 'skip: silero_vad.onnx already present'
}
else {
    Write-Log "downloading $baseUrl/silero_vad.onnx"
    Save-Download "$baseUrl/silero_vad.onnx" $vadFile
    if ((Get-Item -LiteralPath $vadFile).Length -le 0) { throw 'silero_vad.onnx is empty after download' }
    Write-Log "installed $vadFile"
}

# --- AI (optional): llama.cpp server + GGUF models ----------------------------
if ($Ai) {
    $llamaTag = 'b11716'
    $llamaBaseUrl = "https://github.com/ggml-org/llama.cpp/releases/download/$llamaTag"
    $hfChatRepos = @('ggml-org/gemma-4-E4B-it-GGUF', 'unsloth/gemma-4-E4B-it-GGUF', 'bartowski/google_gemma-4-E4B-it-GGUF')
    $hfEmbedRepos = @('ggml-org/embeddinggemma-300M-GGUF', 'unsloth/embeddinggemma-300m-GGUF')
    $llamaDir = Join-Path $modelsDir 'llama.cpp'
    $ggufDir = Join-Path $modelsDir 'gguf'
    $hfHeaders = @{}
    if (-not [string]::IsNullOrWhiteSpace($env:HF_TOKEN)) { $hfHeaders['Authorization'] = "Bearer $($env:HF_TOKEN)" }

    $onWindows = $PSVersionTable.PSVersion.Major -lt 6 -or $IsWindows
    if ($onWindows) {
        $llamaArchive = "llama-$llamaTag-bin-win-cpu-x64.zip"
        $llamaServer = 'llama-server.exe'
    }
    elseif ($IsLinux) {
        $llamaArchive = "llama-$llamaTag-bin-ubuntu-x64.tar.gz"
        $llamaServer = 'llama-server'
    }
    else {
        throw "-Ai: unsupported OS (llama.cpp $llamaTag server is fetched for Windows x64 and Linux x64 only)"
    }

    if (Test-Path -LiteralPath (Join-Path $llamaDir $llamaServer) -PathType Leaf) {
        Write-Log "skip: llama.cpp $llamaTag server already present"
    }
    else {
        $tmp = Join-Path $modelsDir (".fetch." + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $tmp | Out-Null
        try {
            $archive = Join-Path $tmp $llamaArchive
            Write-Log "downloading $llamaBaseUrl/$llamaArchive"
            Save-Download "$llamaBaseUrl/$llamaArchive" $archive
            $extractDir = Join-Path $tmp 'x'
            New-Item -ItemType Directory -Path $extractDir | Out-Null
            Write-Log 'extracting'
            if ($llamaArchive.EndsWith('.zip')) {
                Expand-Archive -LiteralPath $archive -DestinationPath $extractDir -Force
            }
            else {
                & tar -xzf $archive -C $extractDir
                if ($LASTEXITCODE -ne 0) { throw "tar failed with exit code $LASTEXITCODE" }
            }
            # The archive layout differs between releases (flat, llama-<tag>/, build/bin/): find the server, take its whole folder.
            $serverFile = Get-ChildItem -LiteralPath $extractDir -Recurse -File -Filter $llamaServer | Select-Object -First 1
            if ($null -eq $serverFile) { throw "Archive does not contain $llamaServer" }
            $staging = "$llamaDir.new"
            if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
            New-Item -ItemType Directory -Path $staging | Out-Null
            Copy-Item -Path (Join-Path $serverFile.DirectoryName '*') -Destination $staging -Recurse -Force
            if (-not $onWindows) { & chmod +x (Join-Path $staging $llamaServer) }
            if (Test-Path -LiteralPath $llamaDir) { Remove-Item -LiteralPath $llamaDir -Recurse -Force }
            Move-Item -LiteralPath $staging -Destination $llamaDir
            Write-Log "installed $llamaDir"
        }
        finally {
            Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    # First matching *.gguf of a Hugging Face repo (not a projector, not a shard); $null when unreachable or absent.
    function Find-HfFile([string] $Repo, [string] $Quant) {
        try {
            $info = Invoke-RestMethod -Uri "https://huggingface.co/api/models/$Repo" -Headers $hfHeaders -UseBasicParsing
            $names = @($info.siblings | ForEach-Object { $_.rfilename })
        }
        catch {
            Write-Log "Hugging Face API failed for ${Repo}: $($_.Exception.Message)"
            return $null
        }
        $match = $names |
            Where-Object { $_ -like "*$Quant*" -and $_ -like '*.gguf' -and $_ -notlike '*mmproj*' -and $_ -notmatch '-\d{5}-of-\d{5}' } |
            Sort-Object | Select-Object -First 1
        return $match
    }

    # Tries the repos in order; returns @{ Path; Repo; File } of the first that has the file (downloaded unless present).
    function Get-Gguf([string] $Label, [string] $Quant, [string[]] $Repos) {
        foreach ($repo in $Repos) {
            $file = Find-HfFile $repo $Quant
            if ([string]::IsNullOrEmpty($file)) {
                Write-Log "${Label}: no $Quant GGUF in $repo (or repo unreachable), trying the next candidate"
                continue
            }
            $dest = Join-Path $ggufDir $file
            if ((Test-Path -LiteralPath $dest -PathType Leaf) -and (Get-Item -LiteralPath $dest).Length -gt 0) {
                Write-Log "skip: $file already present"
            }
            else {
                Write-Log "downloading $repo/$file"
                Save-Download "https://huggingface.co/$repo/resolve/main/$file" $dest $hfHeaders
                if ((Get-Item -LiteralPath $dest).Length -le 0) { throw "$file is empty after download" }
                Write-Log "installed $dest"
            }
            return @{ Path = $dest; Repo = $repo; File = $file }
        }
        throw "${Label}: none of the candidate repositories worked: $($Repos -join ', ')"
    }

    New-Item -ItemType Directory -Force -Path $ggufDir | Out-Null
    # Idempotent: when models.json names two existing non-empty files, skip the Hugging Face lookup and the download
    # (a re-run works offline and never switches to a different file). Query the API only when the manifest or a file is missing.
    function Test-NonEmptyFile([string] $Path) {
        return (-not [string]::IsNullOrEmpty($Path)) -and (Test-Path -LiteralPath $Path -PathType Leaf) -and ((Get-Item -LiteralPath $Path).Length -gt 0)
    }

    $manifestPath = Join-Path $ggufDir 'models.json'
    $recorded = $null
    if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
        try {
            $parsed = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
            if ((Test-NonEmptyFile ([string] $parsed.chat)) -and (Test-NonEmptyFile ([string] $parsed.embed))) {
                $recorded = $parsed
            }
        }
        catch {
            Write-Log "ignoring unreadable ${manifestPath}: $($_.Exception.Message)"
        }
    }

    if ($null -ne $recorded) {
        Write-Log "skip: chat and embedding models already present (see $manifestPath)"
        $chat = @{ Path = [string] $recorded.chat }
        $embed = @{ Path = [string] $recorded.embed }
    }
    else {
        $chat = Get-Gguf 'chat model' 'Q4_K_M' $hfChatRepos
        $embed = Get-Gguf 'embedding model' 'Q8_0' $hfEmbedRepos

        $manifest = [ordered]@{
            chat  = $chat.Path
            embed = $embed.Path
            repo  = [ordered]@{ chat = $chat.Repo; embed = $embed.Repo }
            file  = [ordered]@{ chat = $chat.File; embed = $embed.File }
        }
        ($manifest | ConvertTo-Json -Depth 4) | Set-Content -LiteralPath "$manifestPath.part" -Encoding UTF8
        Move-Item -LiteralPath "$manifestPath.part" -Destination $manifestPath -Force
        Write-Log "wrote $manifestPath"
    }

    Write-Log 'AI is ready. Use these environment variables:'
    $serverPath = Join-Path $llamaDir $llamaServer
    if ($onWindows) {
        Write-Host "`$env:MURMUR_LLM_MODEL = '$($chat.Path)'"
        Write-Host "`$env:MURMUR_EMBED_MODEL = '$($embed.Path)'"
        Write-Host "`$env:MURMUR_LLAMA_SERVER = '$serverPath'"
    }
    else {
        Write-Host "export MURMUR_LLM_MODEL='$($chat.Path)'"
        Write-Host "export MURMUR_EMBED_MODEL='$($embed.Path)'"
        Write-Host "export MURMUR_LLAMA_SERVER='$serverPath'"
    }
}

Write-Log 'done'
