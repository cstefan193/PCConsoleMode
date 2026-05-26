# $ConnectedMonitors = Get-PnpDevice | `
#     where {$_.FriendlyName -eq "Xbox Wireless Controller"} | `
#     where {$_.status -eq "OK"}

[bool] $LastBtStatus = $false

function GetBtStatus {
    try {
        $BtStatus = Get-PnpDevice -class Bluetooth -FriendlyName 'Xbox Wireless Controller' | 
          Get-PnpDeviceProperty -KeyName '{83DA6326-97A6-4088-9453-A1923F573B29} 15' |
          Select -ExpandProperty Data    
    } catch {
        # do nothing, the device is probably missing
        return $false
    }
    return $BtStatus
}

function GetAudioDeviceID {
    param (
        [Parameter(Mandatory=$true)]
        [string]$Keyword
    )

    # Get the list of audio devices
    $audioDevices = i

    # Filter devices by keyword
    $filteredDevices = $audioDevices | Where-Object { $_.Name -like "*$Keyword*" }

    # No match found → list available devices
    if (-not $filteredDevices -or $filteredDevices.Count -eq 0) {
        Write-Host "Could not find audio device for '$Keyword'. Currently available devices:"
        foreach ($dev in $audioDevices) {
            Write-Host " • $($dev.Name)"
        }
        return $null
    }

    # Multiple matches → list matches and fail safely
    if ($filteredDevices.Count -gt 1) {
        Write-Host "Multiple audio devices match '$Keyword':"
        foreach ($dev in $filteredDevices) {
            Write-Host " • ID: $($dev.ID)  |  Name: $($dev.Name)"
        }
        return $null
    }

    # Single match → return ID
    return $filteredDevices[0].ID
}

function StopProcess {
    param (
        [Parameter(Mandatory=$true)]
        [string]$Keyword
    )

    # Use exact match to close just the main steam process
    $matchingProcesses = Get-Process | Where-Object { $_.Name -like "$Keyword" }
    # $matchingProcesses = Get-Process | Where-Object { $_.Name -like "*$Keyword*" }

    # Check if any processes were found
    if ($matchingProcesses.Count -eq 0) {
        Write-Output "No processes found containing the keyword '$Keyword'."
        return
    }

    # List the matching processes
    Write-Output "Matching processes:"
    $matchingProcesses | ForEach-Object { Write-Output "ID: $($_.Id), Name: $($_.Name)" }

    # Stop the matching processes
    $matchingProcesses | ForEach-Object { Stop-Process -Id $_.Id -Force }

    Write-Output "All matching processes have been stopped."
}

function SwitchMode {
    param (
        [Parameter(Mandatory=$true)]
        [bool]$BtStatus
    )

    if ($BtStatus -eq $true) {
        Write-Host "launching game mode.."
        DisplaySwitch.exe /external

        $retries = 1
        $deviceID = $null

        do {
            # Try Beyond first
            $deviceID = GetAudioDeviceID -Keyword "Beyond"

            # If Beyond not found, immediately try HDMI
            if (-not $deviceID) {
                $deviceID = GetAudioDeviceID -Keyword "HDMI"
            }

            if ($deviceID) {
                Start-Sleep -Seconds 1
                Set-AudioDevice -ID $deviceID
                break
            }

            Write-Host "retrying audio device.. ($retries)"
            Start-Sleep -Seconds 1
            $retries++

        } while ($retries -le 10)

        if (-not $deviceID) {
            throw "No suitable audio device found (SONY or HDMI)."
        }

        Start-Process -FilePath "C:\Program Files (x86)\Steam\steam.exe" `
            -ArgumentList "steam://open/bigpicture"
    }
    else {
        Write-Host "launching desktop mode.."
        $deviceID = GetAudioDeviceID -Keyword "Headphones"
        Set-AudioDevice -ID $deviceID
        DisplaySwitch.exe /internal
        StopProcess -Keyword "steam"
    }
}

while ($true) {
    $BtStatus = GetBtStatus
    if ($LastBtStatus -ne $BtStatus) {
        Write-host "detected change!"
        $LastBtStatus = $BtStatus
        SwitchMode -BtStatus $BtStatus
    } else {
        Write-host "no change.."
    }
    Start-Sleep -Seconds 5
}