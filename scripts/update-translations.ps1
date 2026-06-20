$ErrorActionPreference = "Stop"

$repoBase = "https://raw.githubusercontent.com/Kvan7/Exiled-Exchange-2/master/renderer/public/data"
$localDir = Join-Path (Join-Path (Join-Path $PSScriptRoot "..") "ocr") "translations"
$nonewline = [System.Text.UTF8Encoding]::new($false)  # no BOM

# Language mapping: EE2 dir name → local filename (without extension)
$langs = @{
    "fr"       = "fra"
    "de"       = "deu"
    "es"       = "spa"
    "pt"       = "por"
    "ru"       = "rus"
    "ko"       = "kor"
    "ja"       = "jpn"
    "cmn-Hant" = "chi_tra"
}

$updated = $false
$hasError = $false

foreach ($ee2Dir in $langs.Keys) {
    $localFile = $langs[$ee2Dir]
    $localPath = Join-Path $localDir "$localFile.ndjson"
    $url = "$repoBase/$ee2Dir/items.ndjson"

    try {
        Write-Host "Checking $localFile.ndjson..." -NoNewline

        # Fetch with conditional request using ETag if available
        $etagPath = Join-Path $localDir "$localFile.etag"
        $headers = @{}
        if (Test-Path $etagPath) {
            $etag = Get-Content $etagPath -Raw -ErrorAction SilentlyContinue
            if ($etag) {
                $headers["If-None-Match"] = $etag.Trim()
            }
        }

        try {
            $response = Invoke-WebRequest -Uri $url -Headers $headers -UseBasicParsing -TimeoutSec 30 -ErrorAction Stop
        }
        catch {
            if ($_.Exception.Response.StatusCode -eq 304) {
                Write-Host " up to date"
                continue
            }
            Write-Host " FAILED - $_"
            $hasError = $true
            continue
        }

        if ($response.StatusCode -ne 200) {
            Write-Host " FAILED (HTTP $($response.StatusCode))"
            $hasError = $true
            continue
        }

        # Compare content length as quick heuristic
        $newContent = $response.Content
        if (Test-Path $localPath) {
            $existingBytes = [System.IO.File]::ReadAllBytes($localPath)
            $newBytes = [System.Text.Encoding]::UTF8.GetBytes($newContent)
            if ($existingBytes.Length -eq $newBytes.Length) {
                # Same size — compute hash for certainty
                $existingHash = [System.BitConverter]::ToString([System.Security.Cryptography.SHA256]::Create().ComputeHash($existingBytes)) -replace '-', ''
                $newHash = [System.BitConverter]::ToString([System.Security.Cryptography.SHA256]::Create().ComputeHash($newBytes)) -replace '-', ''
                if ($existingHash -eq $newHash) {
                    Write-Host " identical (hash)"
                    # Save new ETag
                    if ($response.Headers["ETag"]) {
                        [System.IO.File]::WriteAllText($etagPath, $response.Headers["ETag"], $nonewline)
                    }
                    continue
                }
            }
        }

        # Content differs — update
        [System.IO.File]::WriteAllText($localPath, $newContent, $nonewline)
        if ($response.Headers["ETag"]) {
            [System.IO.File]::WriteAllText($etagPath, $response.Headers["ETag"], $nonewline)
        }
        Write-Host " UPDATED"
        $updated = $true
    }
    catch {
        Write-Host " ERROR: $($_.Exception.Message)"
        $hasError = $true
    }
}

if ($updated) {
    Write-Host ""
    Write-Host "⚠ Translation files updated. Rebuild to embed the new data."
}

if ($hasError) {
    Write-Host ""
    Write-Host "Warning: Some translation files could not be checked. Using local versions."
}

exit 0  # Always exit 0 — build should succeed even if check fails
