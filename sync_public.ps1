# sync_public.ps1
# Synchronise la branche publique du projet sur GitHub.
# Depot public  : https://github.com/mobius-performance-consulting/atas-trading-tools
# Branche cible : atas-kde-levels  (main = index des projets, une branche par projet)
#
# Fichiers publies : .py .cs .ps1 .md hors repertoire versions/
# Le depot local (master) conserve TOUS les fichiers.
# Usage : .\sync_public.ps1

$ErrorActionPreference = "Continue"

$repo = $PSScriptRoot
Set-Location $repo

# -- Verification branche courante -------------------------------------------
$currentBranch = (& git branch --show-current 2>&1 | Select-Object -First 1).Trim()
if ($currentBranch -ne "master") {
    Write-Host "ERREUR : lancer depuis master (branche courante : $currentBranch)"
    exit 1
}

# -- Avertissement fichiers non commites -------------------------------------
$dirty = (& git status --porcelain 2>&1)
if ($dirty) {
    Write-Warning "Fichiers non commites detectes - ils ne seront pas dans le push public."
    $dirty | ForEach-Object { Write-Host "  $_" }
}

# -- Liste des fichiers autorises depuis master ------------------------------
$allowed = @(".py", ".cs", ".ps1", ".md")
$allFiles = (& git ls-tree -r --name-only master 2>&1)
$pubFiles = $allFiles | Where-Object {
    $ext = [System.IO.Path]::GetExtension($_).ToLower()
    $isAllowed  = $allowed -contains $ext
    $isArchive  = $_ -like "versions/*"
    $isAllowed -and (-not $isArchive)
}

if (-not $pubFiles) {
    Write-Host "ERREUR : aucun fichier .py/.cs/.ps1/.md trouve dans master (hors versions/)"
    exit 1
}

Write-Host ""
Write-Host "Fichiers a publier ($($pubFiles.Count)) :"
$pubFiles | ForEach-Object { Write-Host "  $_" }
Write-Host ""

# -- Creer la branche orphan temporaire --------------------------------------
$null = git checkout --orphan public_sync 2>&1
$null = git rm -rf . 2>&1

# -- Copier les fichiers autorises depuis master (checkout direct) -----------
foreach ($f in $pubFiles) {
    $null = git checkout master -- $f 2>&1
}

# -- Verifier qu'il y a bien des fichiers a commiter -------------------------
$staged = (& git status --porcelain 2>&1 | Where-Object { $_ -match "^A " })
if (-not $staged) {
    Write-Host "ERREUR : aucun fichier stage apres checkout - verifier les chemins."
    $null = git checkout master 2>&1
    $null = git branch -D public_sync 2>&1
    exit 1
}

# -- Commit et push ----------------------------------------------------------
$date    = Get-Date -Format "yyyy-MM-dd"
$lastMsg = (& git log master --format="%s" -1 2>&1 | Select-Object -First 1).Trim()
$msg     = "Public sync $date - $lastMsg"

$null = git commit -m $msg 2>&1
& git push origin public_sync:atas-kde-levels --force

Write-Host ""
Write-Host "OK - Depot public mis a jour :"
Write-Host "  https://github.com/mobius-performance-consulting/atas-trading-tools/tree/atas-kde-levels"
Write-Host "  $($pubFiles.Count) fichiers publies (.py/.cs/.ps1/.md, hors versions/)"
Write-Host "  Message : $msg"

# -- Retour sur master et nettoyage ------------------------------------------
$null = git checkout master 2>&1
$null = git branch -D public_sync 2>&1
Write-Host "Branche restauree : master"
Write-Host ""
