<#
Shared by the scripts that drive Microsoft Publisher through COM (dot-source it).
#>

# Publisher automation could hand a request to a copy the user already has open, which the script would later quit.
# So a script refuses to start while Publisher is running. It waits briefly first, because a copy this tool chain
# just quit can take a moment to exit.
function Assert-PublisherClosed([int]$WaitSeconds = 10) {
    $deadline = (Get-Date).AddSeconds($WaitSeconds)
    while (Get-Process MSPUB -ErrorAction SilentlyContinue) {
        if ((Get-Date) -gt $deadline) {
            throw 'Close Microsoft Publisher first: this script starts and quits its own copy, and could otherwise close yours.'
        }
        Start-Sleep -Milliseconds 500
    }
}

# After a child script finished or timed out: stop the Publisher processes it recorded, and any automation instance
# started since the child began (one that hung while starting was never recorded, one that outlived Quit would block
# the next file). Publisher started through COM runs with
# "/Automation -Embedding" on its command line; a Publisher the user opens never has it, so it is left alone.
# (Publisher opened to edit an object embedded in another document runs the same way and is not told apart; the
# scripts refuse to start while any Publisher runs, so that would have to begin during a run.)
function Stop-ChildPublisher([string]$PidFile, [datetime]$Since) {
    $ids = @(Get-Content -LiteralPath $PidFile -ErrorAction SilentlyContinue)
    $ids += @(Get-CimInstance Win32_Process -Filter "Name = 'MSPUB.EXE'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match '(^|\s)-Embedding(\s|$)' -and $_.CreationDate -ge $Since } |
        ForEach-Object ProcessId)
    foreach ($id in $ids | Where-Object { $_ } | Sort-Object -Unique) {
        Get-Process -Id $id -ErrorAction SilentlyContinue | Where-Object ProcessName -eq 'MSPUB' | Stop-Process -Force
    }
}
