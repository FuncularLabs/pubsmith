# Publisher test machine ("PubOracle") — Hyper-V setup guide

**Purpose:** keep a working copy of Publisher 2021 after Microsoft 365 Publisher stops (October 2026),
so the replacement product can keep being tested against Publisher's own output. The VM is used for
three things only:

1. Make new `.pub` test files (for example `New-FeatureCorpus.ps1`).
2. Export Publisher's PDF/PNG of a `.pub`, which is the expected result in our tests (`Export-PubHarvest.ps1`).
3. Open old files by hand when needed.

It is **not** a daily-use Office machine. Nothing else goes on it.

Written 2026-09-27 for a Windows 11 Pro workstation with Hyper-V.
Steps marked **(untested)** haven't been run yet, because the VM doesn't exist yet.

---

## 0. Before you start: what you need

| Item | Notes |
|---|---|
| **A Windows 11 licence for the VM** | A VM is a separate PC for licensing purposes, so your host's Windows licence doesn't cover it. Buy a Windows 11 Pro retail key. (Microsoft's 90-day Enterprise evaluation works for a trial, but it expires and can't be converted, so don't build the long-term machine on it.) |
| **A genuine Office 2021 key** | From Microsoft or an authorised reseller, for an edition that includes Publisher (Office Professional 2021, Office Professional Plus 2021, or Publisher 2021 on its own). Redeem a retail key first at <https://setup.office.com>. If it won't redeem, stop and resolve that before building anything. Install only from Microsoft's own downloads (the ODT below). |
| **Windows 11 ISO** | From <https://www.microsoft.com/software-download/windows11> → "Download Windows 11 Disk Image (ISO) for x64 devices". Save it to `D:\ISO\Win11.iso`. |
| **Office Deployment Tool (ODT)** | From Microsoft's Download Center (search "Office Deployment Tool"). You'll copy it into the VM later. |
| **An elevated PowerShell** | Start → type `PowerShell` → right-click → *Run as administrator*. Every host command below runs there. |

## 1. Check Hyper-V is on (host, elevated)

```powershell
Get-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All | Select-Object State
```

- `Enabled`: go to step 2.
- `Disabled`: run the following, then restart:
  ```powershell
  Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All
  ```

Optional: add yourself to the Hyper-V Administrators group so later day-to-day VM commands don't need elevation. Sign out and back in afterwards.

```powershell
Add-LocalGroupMember -Group 'Hyper-V Administrators' -Member "$env:USERDOMAIN\$env:USERNAME"
```

## 2. Create the VM (host, elevated)

The VM lives on `D:\Hyper-V`, outside any cloud-synced folder. Never put a `.vhdx` inside a synced folder.

```powershell
$name = 'PubOracle'
$root = 'D:\Hyper-V'
New-Item -ItemType Directory -Force "$root\$name" | Out-Null

New-VM -Name $name -Generation 2 -MemoryStartupBytes 8GB -Path $root `
       -NewVHDPath "$root\$name\$name.vhdx" -NewVHDSizeBytes 80GB -SwitchName 'Default Switch'
Set-VMProcessor -VMName $name -Count 4
Set-VMMemory    -VMName $name -DynamicMemoryEnabled $true -MinimumBytes 4GB -MaximumBytes 12GB

# Windows 11 needs Secure Boot (on by default for Gen 2) and a TPM.
Set-VMKeyProtector -VMName $name -NewLocalKeyProtector
Enable-VMTPM       -VMName $name

# Lets the host copy files into the VM with no network (Copy-VMFile / PowerShell Direct).
Enable-VMIntegrationService -VMName $name -Name 'Guest Service Interface'

# Checkpoints are taken by hand only, at known-good moments.
Set-VM -Name $name -CheckpointType Production -AutomaticCheckpointsEnabled $false

Add-VMDvdDrive -VMName $name -Path 'D:\ISO\Win11.iso'
Set-VMFirmware -VMName $name -FirstBootDevice (Get-VMDvdDrive -VMName $name)

Start-VM -Name $name
vmconnect.exe localhost $name
```

In the console window, press a key when it says *"Press any key to boot from CD or DVD"*. If you miss it,
use *Action → Reset* and try again.

## 3. Install Windows in the VM

1. Choose **Windows 11 Pro** and enter the VM's Windows key (or *I don't have a product key* and activate later).
2. Custom install onto the single 80 GB disk.
3. Sign in with your Microsoft account. It's the simplest route on current Windows 11, and it's also the
   account your Office key is redeemed to.
4. After first sign-in: *Settings → Windows Update → Check for updates*, and repeat until none are left.
5. *Settings → System → Activation*: confirm Windows is activated.
6. Turn on Enhanced Session (host, elevated) so the console gets clipboard sharing and a resizable window:
   ```powershell
   Set-VMHost -EnableEnhancedSessionMode $true
   ```

**Checkpoint:**
```powershell
Checkpoint-VM -Name PubOracle -SnapshotName '01 windows-activated-updated'
```

## 4. Install Publisher 2021 only (inside the VM)

1. Create `C:\ODT`, run the ODT self-extractor you downloaded, and extract it into `C:\ODT`.
2. Save the following as `C:\ODT\publisher-only.xml`. Put your key in `PIDKEY`. The product ID has to match
   the edition and key type:
   - **Office Professional 2021, retail:** `Professional2021Retail`.
   - **Office Professional Plus 2021, retail key:** `ProPlus2021Retail`, as shown below.
   - **Publisher 2021 on its own, retail:** `Publisher2021Retail` (then no `ExcludeApp` lines are needed).
   - **volume/MAK key:** `ProPlus2021Volume` (or `Publisher2021Volume`), and add `Channel="PerpetualVL2021"` to `<Add>`.

   ```xml
   <Configuration>
     <Add OfficeClientEdition="64">
       <Product ID="ProPlus2021Retail" PIDKEY="XXXXX-XXXXX-XXXXX-XXXXX-XXXXX">
         <Language ID="en-us" />
         <ExcludeApp ID="Access" />     <ExcludeApp ID="Excel" />    <ExcludeApp ID="Lync" />
         <ExcludeApp ID="OneDrive" />   <ExcludeApp ID="OneNote" />  <ExcludeApp ID="Outlook" />
         <ExcludeApp ID="PowerPoint" /> <ExcludeApp ID="Teams" />    <ExcludeApp ID="Word" />
       </Product>
     </Add>
     <!-- Freeze the build: Publisher 2021 gets no feature updates after support ends anyway,
          and a frozen build can't be changed underneath the tests. -->
     <Updates Enabled="FALSE" />
     <Display Level="Full" AcceptEULA="FALSE" />
   </Configuration>
   ```
3. In an elevated prompt **inside the VM**:
   ```powershell
   cd C:\ODT
   .\setup.exe /configure publisher-only.xml
   ```
4. Start Publisher, go to *File → Account*, and confirm it says **Product Activated** and names the edition
   your key is for.

**Checkpoint:**
```powershell
Checkpoint-VM -Name PubOracle -SnapshotName '02 publisher-2021-activated'
```

## 5. Check it can do the job (inside the VM)

Copy some test files in. The simplest way is to paste them over the Enhanced Session clipboard into `C:\PubOracle\check\`:

- one of your own publications that uses custom or cloud fonts
- `F13-fonts.pub` and `F18-F19-wordart-curved.pub` from the feature test set (`New-FeatureCorpus.ps1`)

Then:

| Check | Pass if |
|---|---|
| Your publication opens | No error |
| **Custom fonts** | Your publication's headings show in their intended faces, and the F13 lines for the cloud fonts (e.g. Aptos) show those faces. These are Microsoft 365 *cloud fonts*, so Office 2021 may not be able to get them. **Record the result either way.** If they're missing, the VM is still useful for new test files that use installed fonts, but not for re-rendering existing artwork that uses them. PDFs harvested while Microsoft 365 Publisher still worked stay the reference for those. |
| COM scripting works | In PowerShell, `New-Object -ComObject Publisher.Application` returns without error |

## 6. Take it offline

Once activated and checked, the VM doesn't need a network. Host, elevated:

```powershell
Disconnect-VMNetworkAdapter -VMName PubOracle
```

Reconnect it temporarily for Windows updates, or if Office asks to re-check activation:
`Connect-VMNetworkAdapter -VMName PubOracle -SwitchName 'Default Switch'`.

**Checkpoint:**
```powershell
Checkpoint-VM -Name PubOracle -SnapshotName '03 offline-ready'
```

## 7. Automating exports from the host (untested)

Office scripting must run **in the signed-in desktop session**. It doesn't work reliably from a remote or
background session. The design is a small inbox watcher that runs at logon inside the VM, plus a host
script that drops files in and collects the results. Both scripts are in this repo under `tools/oracle/`.

**In the VM, once:**
1. Copy `Export-PubHarvest.ps1`, `PublisherProcesses.ps1` (which it loads) and `Watch-PubInbox.ps1` to
   `C:\PubOracle\tools\`.
2. Create a Task Scheduler task: *Trigger* "At log on" (your user); *Action*
   `pwsh.exe -NoProfile -WindowStyle Hidden -File C:\PubOracle\tools\Watch-PubInbox.ps1`
   (or `powershell.exe` if PowerShell 7 isn't installed); *Run only when user is logged on*.
3. Optional, for unattended use: Sysinternals **Autologon** stores your sign-in password encrypted, so the VM
   logs in by itself after a start.

**From the host** (works with the VM offline; PowerShell Direct needs no network):
```powershell
.\tools\oracle\Invoke-PubOracle.ps1 -VMName PubOracle -Pub .\some-test.pub -OutDir .\oracle-out
```
It copies the `.pub` into the VM's inbox, waits for the PDF and PNGs, and copies them back.

## 8. Backups

The VM holds a licence activation and a Publisher build you can't get again. Export it after each checkpoint
you care about, to a drive with room:

```powershell
Export-VM -Name PubOracle -Path 'E:\backups\hyper-v'
```

## 9. Troubleshooting

| Symptom | Fix |
|---|---|
| "This PC can't run Windows 11" during setup | The TPM or Secure Boot isn't set. Shut down the VM, re-run the `Set-VMKeyProtector` / `Enable-VMTPM` lines, and check `Get-VMFirmware PubOracle` shows `SecureBoot On`. |
| No network during setup | `Get-VMSwitch` should list `Default Switch`. If not, create an external switch in Hyper-V Manager → *Virtual Switch Manager*. |
| ODT says products are incompatible | Something else Office-related is already in the VM. This VM should have Windows and Publisher 2021 only. |
| Publisher COM call hangs, window titled "Publishing..." | A COM server fault (known trigger: `Font.TextShadow.Visible`). Stop that `MSPUB` process; the harvest scripts already avoid this call. |
| Activation lost after restoring an old checkpoint | Reconnect the network adapter once and open Publisher → *File → Account*. |
