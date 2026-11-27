$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$pin = (Get-Item -LiteralPath (Join-Path $root '.pilot/huaweicloud-124.71.172.123.known_hosts')).FullName
$script = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'huaweicloud-loopback.sh') -Raw -Encoding UTF8) -replace "`r`n", "`n"
$encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($script))
$launch = ('/bin/bash -c ''exec /bin/bash <(printf %s {0} | base64 -d) --resume-stdin''' -f $encoded)

function Read-ReviewPasswordBytes {
    $utf8 = [Text.UTF8Encoding]::new($false, $true)
    while ($true) {
        $first = Read-Host 'New review site password (12-256 characters, not the SSH password)' -AsSecureString
        $second = $null
        $p1 = $p2 = [IntPtr]::Zero
        $chars = $null
        try {
            if ($first.Length -lt 12 -or $first.Length -gt 256) {
                Write-Host ("Received {0} characters. Type a new password with 12-256 characters." -f $first.Length)
                continue
            }
            $second = Read-Host 'Confirm review site password' -AsSecureString
            if ($first.Length -ne $second.Length) {
                Write-Host 'Passwords do not match. Try again.'
                continue
            }
            $p1 = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($first)
            $p2 = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($second)
            $chars = [char[]]::new($first.Length)
            $same = $true
            $valid = $true
            for ($i = 0; $i -lt $chars.Length; $i++) {
                $a = [int][uint16]([Runtime.InteropServices.Marshal]::ReadInt16($p1, 2 * $i) -band 0xffff)
                $b = [int][uint16]([Runtime.InteropServices.Marshal]::ReadInt16($p2, 2 * $i) -band 0xffff)
                if ($a -ne $b) { $same = $false }
                if ($a -lt 32 -or $a -eq 127) { $valid = $false }
                $chars[$i] = [char]$a
            }
            if (-not $same) { Write-Host 'Passwords do not match. Try again.'; continue }
            if (-not $valid) { Write-Host 'Do not use control characters. Try again.'; continue }
            return ,$utf8.GetBytes($chars)
        } finally {
            if ($null -ne $chars) { [Array]::Clear($chars, 0, $chars.Length) }
            if ($p1 -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($p1) }
            if ($p2 -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($p2) }
            if ($null -ne $second) { $second.Dispose() }
            if ($null -ne $first) { $first.Dispose() }
        }
    }
}

$info = [Diagnostics.ProcessStartInfo]::new("$env:WINDIR/System32/OpenSSH/ssh.exe")
$info.Arguments = '-T -o "UserKnownHostsFile={0}" -o StrictHostKeyChecking=yes -o HostKeyAlgorithms=ssh-ed25519 -o ConnectTimeout=15 root@124.71.172.123 "{1}"' -f $pin, $launch
$info.UseShellExecute = $false
$info.RedirectStandardInput = $true
$secret = Read-ReviewPasswordBytes
$child = $null
try {
    Write-Host 'SSH is connecting. Type the server SSH password at the next prompt.'
    $child = [Diagnostics.Process]::Start($info)
    try {
        $inputPipe = $child.StandardInput.BaseStream
        try { $inputPipe.Write($secret, 0, $secret.Length) } finally { $inputPipe.Close() }
    } finally { [Array]::Clear($secret, 0, $secret.Length) }
    $child.WaitForExit()
    exit $child.ExitCode
} finally {
    [Array]::Clear($secret, 0, $secret.Length)
    if ($null -ne $child) { $child.Dispose() }
}
