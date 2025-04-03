param (
    [Parameter()]
    [switch]$Install,
    [Parameter()]
    [switch]$Remove,
    [Parameter()]
    [string]$KeyVaultUrl
)

function Show-Menu {
    Clear-Host
    Write-Host "===== Azure Key Vault Configuration =====" -ForegroundColor Cyan
    Write-Host "1: Add/Update Azure Key Vault URL"
    Write-Host "2: Remove Azure Key Vault URL"
    Write-Host "3: Show current configuration"
    Write-Host "Q: Quit"
    Write-Host "=======================================" -ForegroundColor Cyan
}

function Add-KeyVaultUrl {
    param (
        [string]$Url
    )

    # If URL was not passed as a parameter, prompt for it
    if ([string]::IsNullOrEmpty($Url)) {
        $Url = Read-Host "Enter Azure Key Vault URL (e.g., https://your-vault.vault.azure.net/)"
    }

    # Validate URL format
    if (-not $Url.StartsWith("https://") -or -not $Url.Contains(".vault.azure.net")) {
        Write-Host "Invalid Key Vault URL format. URL should look like: https://your-vault.vault.azure.net/" -ForegroundColor Red
        return
    }

    # Set for current process
    $env:KEYVAULT_URI = $Url
    
    # Prompt for making it permanent
    $makePermanent = Read-Host "Do you want to make this setting permanent? (Y/N)"
    
    if ($makePermanent -eq "Y" -or $makePermanent -eq "y") {
        $scope = Read-Host "Set for (U)ser or (M)achine? (U/M) - Note: Machine requires admin privileges"
        
        if ($scope -eq "U" -or $scope -eq "u") {
            [System.Environment]::SetEnvironmentVariable("KEYVAULT_URI", $Url, [System.EnvironmentVariableTarget]::User)
            Write-Host "Key Vault URL set permanently for current user" -ForegroundColor Green
        }
        elseif ($scope -eq "M" -or $scope -eq "m") {
            try {
                [System.Environment]::SetEnvironmentVariable("KEYVAULT_URI", $Url, [System.EnvironmentVariableTarget]::Machine)
                Write-Host "Key Vault URL set permanently for machine" -ForegroundColor Green
            }
            catch {
                Write-Host "Failed to set machine-level environment variable. Make sure you're running as administrator." -ForegroundColor Red
            }
        }
        else {
            Write-Host "Invalid option. The setting is only applied for the current session." -ForegroundColor Yellow
        }
    }
    else {
        Write-Host "Key Vault URL set for current session only" -ForegroundColor Yellow
    }
    
    Write-Host "Current Key Vault URL: $env:KEYVAULT_URI" -ForegroundColor Cyan
}

function Remove-KeyVaultUrl {
    # Remove from current process
    $env:KEYVAULT_URI = $null
    
    # Prompt for making it permanent
    $makePermanent = Read-Host "Do you want to permanently remove this setting? (Y/N)"
    
    if ($makePermanent -eq "Y" -or $makePermanent -eq "y") {
        $scope = Read-Host "Remove from (U)ser or (M)achine? (U/M) - Note: Machine requires admin privileges"
        
        if ($scope -eq "U" -or $scope -eq "u") {
            [System.Environment]::SetEnvironmentVariable("KEYVAULT_URI", $null, [System.EnvironmentVariableTarget]::User)
            Write-Host "Key Vault URL removed permanently for current user" -ForegroundColor Green
        }
        elseif ($scope -eq "M" -or $scope -eq "m") {
            try {
                [System.Environment]::SetEnvironmentVariable("KEYVAULT_URI", $null, [System.EnvironmentVariableTarget]::Machine)
                Write-Host "Key Vault URL removed permanently for machine" -ForegroundColor Green
            }
            catch {
                Write-Host "Failed to remove machine-level environment variable. Make sure you're running as administrator." -ForegroundColor Red
            }
        }
        else {
            Write-Host "Invalid option. The setting is only removed for the current session." -ForegroundColor Yellow
        }
    }
    else {
        Write-Host "Key Vault URL removed for current session only" -ForegroundColor Yellow
    }
    
    Write-Host "Key Vault URL environment variable has been removed" -ForegroundColor Cyan
}

function Show-Configuration {
    Write-Host "===== Current Configuration =====" -ForegroundColor Cyan
    
    # Check current process
    if ($null -eq $env:KEYVAULT_URI) {
        Write-Host "Current session: Not configured" -ForegroundColor Yellow
    } else {
        Write-Host "Current session: $env:KEYVAULT_URI" -ForegroundColor Green
    }
    
    # Check user setting
    $userSetting = [System.Environment]::GetEnvironmentVariable("KEYVAULT_URI", [System.EnvironmentVariableTarget]::User)
    if ($null -eq $userSetting) {
        Write-Host "User setting: Not configured" -ForegroundColor Yellow
    } else {
        Write-Host "User setting: $userSetting" -ForegroundColor Green
    }
    
    # Check machine setting
    $machineSetting = [System.Environment]::GetEnvironmentVariable("KEYVAULT_URI", [System.EnvironmentVariableTarget]::Machine)
    if ($null -eq $machineSetting) {
        Write-Host "Machine setting: Not configured" -ForegroundColor Yellow
    } else {
        Write-Host "Machine setting: $machineSetting" -ForegroundColor Green
    }
    
    Write-Host "===============================" -ForegroundColor Cyan
    Write-Host "Press any key to continue..."
    $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
}

# Handle direct command-line parameters
if ($Install) {
    Add-KeyVaultUrl -Url $KeyVaultUrl
    exit
}

if ($Remove) {
    Remove-KeyVaultUrl
    exit
}

# Interactive menu
do {
    Show-Menu
    $selection = Read-Host "Enter your choice"
    
    switch ($selection) {
        '1' {
            Add-KeyVaultUrl
        }
        '2' {
            Remove-KeyVaultUrl
        }
        '3' {
            Show-Configuration
        }
        'q' {
            return
        }
        default {
            Write-Host "Invalid selection. Please try again." -ForegroundColor Red
            Start-Sleep -Seconds 2
        }
    }
    
    if ($selection -ne 'q') {
        Write-Host "`nPress any key to return to the menu..."
        $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
    }
    
} until ($selection -eq 'q')