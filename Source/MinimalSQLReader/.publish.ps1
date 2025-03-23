# Step 1: Publish the application
dotnet publish C:\Repository\MinimalSQLReader\Source\MinimalSQLReader -c Release -o C:\Repository\MinimalSQLReader\Deployment\MinimalSQLReader_temp

# Step 2: Backup the existing auth.db file if it exists
$authDbPath = "C:\Repository\MinimalSQLReader\Deployment\MinimalSQLReader\auth.db"
$authDbBackupPath = "C:\Repository\MinimalSQLReader\Deployment\auth.db.backup"

if (Test-Path $authDbPath) {
    Copy-Item $authDbPath $authDbBackupPath -Force
}

# Step 3: Remove the existing deployment folder (except auth.db which we backed up)
if (Test-Path "C:\Repository\MinimalSQLReader\Deployment\MinimalSQLReader") {
    Remove-Item -Path "C:\Repository\MinimalSQLReader\Deployment\MinimalSQLReader" -Recurse -Force
}

# Step 4: Move the temp published files to the deployment directory
Move-Item -Path "C:\Repository\MinimalSQLReader\Deployment\MinimalSQLReader_temp" -Destination "C:\Repository\MinimalSQLReader\Deployment\MinimalSQLReader"

# Step 5: Restore the auth.db file if it existed
if (Test-Path $authDbBackupPath) {
    Copy-Item $authDbBackupPath "C:\Repository\MinimalSQLReader\Deployment\MinimalSQLReader\auth.db" -Force
    Remove-Item $authDbBackupPath -Force
}

# Step 6: Clean up unnecessary development files
$filesToRemove = @(
    "*.pdb",
    "*.xml",
    "appsettings.Development.json"
)
foreach ($pattern in $filesToRemove) {
    Get-ChildItem -Path "C:\Repository\MinimalSQLReader\Deployment\MinimalSQLReader" -Filter $pattern -Recurse | Remove-Item -Force
}

# Step 7: Remove all localized folders with SqlClient resources, except for 'en' and 'nl'
Get-ChildItem -Path "C:\Repository\MinimalSQLReader\Deployment\MinimalSQLReader" -Directory |
Where-Object {
    ($_.Name -ne "en" -and $_.Name -ne "nl") -and
    (Test-Path "$($_.FullName)\Microsoft.Data.SqlClient.resources.dll")
} | Remove-Item -Recurse -Force

Write-Host "✅ Deployment complete. The application has been published to C:\Repository\MinimalSQLReader\Deployment\MinimalSQLReader with the original auth.db preserved."
