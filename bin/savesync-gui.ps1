# SaveSync - tiny Push/Pull window. created by Claude, 2026-09-18.
# Launched by C:\SaveSync\bin\SaveSync.cmd (desktop shortcut).
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$ErrorActionPreference = 'Stop'

$script = 'C:\SaveSync\bin\savesync.ps1'
$key    = 'spiderman'

$f = New-Object Windows.Forms.Form
$f.Text = 'SaveSync'
$f.Size = New-Object Drawing.Size(430, 330)
$f.StartPosition = 'CenterScreen'
$f.FormBorderStyle = 'FixedDialog'
$f.MaximizeBox = $false

$out = New-Object Windows.Forms.TextBox
$out.Multiline = $true; $out.ScrollBars = 'Vertical'; $out.ReadOnly = $true
$out.Location = New-Object Drawing.Point(12, 120)
$out.Size = New-Object Drawing.Size(390, 155)
$out.Font = New-Object Drawing.Font('Consolas', 9)
$out.Text = "Ready. Game: $key"

function Invoke-SaveSyncAction {
    param([string]$Mode, [string]$Label)
    $out.Text = "$Label ..."
    $f.Refresh()
    try {
        $res = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script $Mode $key 2>&1 | Out-String
        $summary = ($res -split "`n" | Where-Object { $_ -match 'Games:|Size:|error|FAILED' }) -join "`n"
        if (-not $summary) { $summary = $res }
        $out.Text = "$Label complete.`r`n`r`n$($summary.Trim())"
    } catch {
        $out.Text = "$Label FAILED:`r`n$_"
    }
}

$push = New-Object Windows.Forms.Button
$push.Text = 'PUSH   (this PC  ->  Deck)'
$push.Location = New-Object Drawing.Point(12, 15)
$push.Size = New-Object Drawing.Size(390, 45)
$push.Font = New-Object Drawing.Font('Segoe UI', 11, [Drawing.FontStyle]::Bold)
$push.Add_Click({ Invoke-SaveSyncAction -Mode 'backup' -Label 'Push' })

$pull = New-Object Windows.Forms.Button
$pull.Text = 'PULL   (Deck  ->  this PC)'
$pull.Location = New-Object Drawing.Point(12, 65)
$pull.Size = New-Object Drawing.Size(390, 45)
$pull.Font = New-Object Drawing.Font('Segoe UI', 11, [Drawing.FontStyle]::Bold)
$pull.Add_Click({
    $ans = [Windows.Forms.MessageBox]::Show(
        "Pull OVERWRITES this PC's live save with the synced backup.`n`nContinue?",
        'SaveSync - confirm pull', 'YesNo', 'Warning')
    if ($ans -eq 'Yes') { Invoke-SaveSyncAction -Mode 'restore' -Label 'Pull' }
})

$f.Controls.AddRange(@($push, $pull, $out))
[void]$f.ShowDialog()
