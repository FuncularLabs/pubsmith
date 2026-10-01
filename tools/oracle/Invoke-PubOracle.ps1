<#
Runs on the HOST. Sends one .pub to the PubOracle VM's inbox over PowerShell Direct (no network needed),
waits for Watch-PubInbox.ps1 to write a final <name>.status (OK or FAIL; a WAITING status means the VM will try
again, so it keeps waiting, up to -TimeoutSeconds), then copies the PDF/PNG outputs back to -OutDir.
Needs: the VM running with the user signed in (watcher active), and guest credentials.
#>
param(
    [Parameter(Mandatory)][string]$Pub,
    [string]$VMName = 'PubOracle',
    [string]$OutDir = '.\oracle-out',
    [pscredential]$Credential = (Get-Credential -Message "Sign-in for $VMName"),
    [int]$TimeoutSeconds = 300
)
$ErrorActionPreference = 'Stop'
$pubItem = Get-Item -LiteralPath $Pub
$s = New-PSSession -VMName $VMName -Credential $Credential
try {
    $name = $pubItem.BaseName
    # Clear any previous result for this name so a stale .status can't be mistaken for this run.
    Invoke-Command -Session $s { param($n) Remove-Item "C:\PubOracle\out\$n.status" -ErrorAction SilentlyContinue } -ArgumentList $name
    Copy-Item -LiteralPath $pubItem.FullName -Destination 'C:\PubOracle\inbox\' -ToSession $s
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        Start-Sleep -Seconds 2
        # Read as one string (an error may span lines). WAITING is not final: the watcher tries the file again.
        $status = Invoke-Command -Session $s { param($n)
            $t = Get-Content -Raw "C:\PubOracle\out\$n.status" -ErrorAction SilentlyContinue
            if ($t) { $t.Trim() }
        } -ArgumentList $name
    } until (($status -and $status -notlike 'WAITING*') -or (Get-Date) -gt $deadline)
    if (-not $status) { throw "Timed out after $TimeoutSeconds s waiting for $name (is the user signed in and the watcher running?)" }
    if ($status -like 'WAITING*') { throw "Timed out after $TimeoutSeconds s: the VM could not run $name yet ($status)" }
    if ($status -ne 'OK') { throw "Oracle failed for ${name}: $status" }
    New-Item -ItemType Directory -Force $OutDir | Out-Null
    $files = Invoke-Command -Session $s { param($n)
        Get-ChildItem 'C:\PubOracle\out' -Recurse -File | Where-Object { $_.Name -like "$n.pdf" -or $_.Name -like "$n-p*.png" } | ForEach-Object FullName
    } -ArgumentList $name
    foreach ($f in $files) { Copy-Item -Path $f -Destination $OutDir -FromSession $s }
    Get-ChildItem $OutDir | Where-Object { $_.Name -like "$name*" }
} finally { Remove-PSSession $s }
