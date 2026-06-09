# deploy.ps1 - Compile et deploie les indicateurs dans ATAS
# Usage : .\deploy.ps1

$projectDir     = "$PSScriptRoot\ATAS_lecture_csv"
$destIndicators = "$env:APPDATA\ATAS\Indicators"

# 1. Build Release
Write-Host "Build en cours..."
$result = dotnet build "$projectDir" -c Release --nologo 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Error "Build echoue :`n$result"
    exit 1
}
Write-Host "Build OK."

# 2. Trouver la DLL produite
$dll = Get-ChildItem "$projectDir\bin\Release" -Recurse -Filter "CsvLevelsImporter.dll" | Select-Object -First 1
if (-not $dll) {
    Write-Error "DLL introuvable dans bin\Release"
    exit 1
}

# 3. Copier dans ATAS\Indicators
if (-not (Test-Path $destIndicators)) {
    Write-Error "Repertoire ATAS\Indicators introuvable : $destIndicators"
    exit 1
}

Copy-Item $dll.FullName "$destIndicators\CsvLevelsImporter.dll" -Force
Write-Host "Deploye : $($dll.FullName) -> $destIndicators\CsvLevelsImporter.dll"

Write-Host ""
Write-Host "OK - Redemarrer ATAS pour charger la nouvelle version."
