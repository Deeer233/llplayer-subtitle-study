$ErrorActionPreference = 'Stop'
$pin = (Get-Item -LiteralPath (Join-Path $PSScriptRoot '../../.pilot/huaweicloud-124.71.172.123.known_hosts')).FullName
$fixture = (Get-Item -LiteralPath (Join-Path $PSScriptRoot 'check-recovery.mjs')).FullName
$ssh = (Get-Item -LiteralPath (Join-Path $env:WINDIR 'System32/OpenSSH/ssh.exe')).FullName
$remote = 'hostname -s | grep -Fxq hcss-ecs-0b7d && systemctl is-active private-review.service && ss -H -ltn sport = :8765 | grep -E ''[[:space:]]127[.]0[.]0[.]1:8765[[:space:]]'' && exec /usr/sbin/runuser -u private-review -- /opt/private-review/runtime/bin/node --input-type=module - /opt/private-review/app /opt/private-review/runtime/bin/ffmpeg'
$info = [Diagnostics.ProcessStartInfo]::new($ssh)
$info.Arguments = '-T -F none -o "UserKnownHostsFile={0}" -o GlobalKnownHostsFile=none -o StrictHostKeyChecking=yes -o UpdateHostKeys=no -o HostKeyAlgorithms=ssh-ed25519 -o ClearAllForwardings=yes -o BatchMode=no -o ConnectTimeout=15 root@124.71.172.123 "{1}"' -f $pin, $remote
$info.UseShellExecute = $false
$info.RedirectStandardInput = $true

Write-Host 'If SSH prompts for its password, type it in this terminal only.'
$child = [Diagnostics.Process]::Start($info)
try {
    $stdin = $child.StandardInput.BaseStream
    try {
        $bytes = [IO.File]::ReadAllBytes($fixture)
        $stdin.Write($bytes, 0, $bytes.Length)
    } catch [IO.IOException] {
        Write-Warning 'SSH closed its input before the fixture was sent.'
    } finally {
        $stdin.Close()
    }
    $child.WaitForExit()
    exit $child.ExitCode
} finally {
    $child.Dispose()
}
