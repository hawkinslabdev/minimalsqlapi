# Set the correct paths for your environment
$repoBase = "C:\Github\minimalsqlreader"
$toolsDir = "$repoBase\Source\Tools\SecretManager"
$deploymentDir = "$repoBase\Deployment\MinimalSQLReader\tools\SecretManager"

# Ensure deployment directory exists
if (-not (Test-Path -Path $deploymentDir)) {
    New-Item -Path $deploymentDir -ItemType Directory -Force
}

# Clear existing files in the deployment directory
Write-Host "Clearing existing files in deployment directory..." -ForegroundColor Yellow
Get-ChildItem -Path $deploymentDir -Recurse | Remove-Item -Force -Recurse
Write-Host "Deployment directory cleared" -ForegroundColor Green

# Find and copy all PS1 files from the SecretManager directory, EXCLUDING .publish.ps1 files
Write-Host "Copying PS1 files to deployment directory..." -ForegroundColor Yellow
Get-ChildItem -Path "$toolsDir\*" -Include "*.ps1" -Recurse | Where-Object { $_.Name -notlike "*.publish.ps1" } | ForEach-Object {
    $targetPath = Join-Path -Path $deploymentDir -ChildPath $_.Name
    Copy-Item -Path $_.FullName -Destination $targetPath -Force
    Write-Host "Copied: $($_.Name)" -ForegroundColor Cyan
}

# Verify files were copied
$copiedFiles = Get-ChildItem -Path $deploymentDir -Include "*.ps1" -Recurse
if ($copiedFiles.Count -gt 0) {
    Write-Host "✅ SecretManager scripts deployed successfully to $deploymentDir" -ForegroundColor Green
    Write-Host "Deployed files:" -ForegroundColor White
    $copiedFiles | ForEach-Object {
        Write-Host "  - $($_.Name)" -ForegroundColor White
    }
} else {
    Write-Host "⚠️ No PS1 files were found to deploy" -ForegroundColor Red
}