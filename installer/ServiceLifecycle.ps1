param(
    [Parameter(Mandatory=$true)][ValidateSet('Prepare','Configure','Remove')][string]$Phase,
    [string]$InstallDirectory
)
$ErrorActionPreference = 'Stop'
$serviceName = 'ZplPrintManager'
$dataRoot = Join-Path $env:ProgramData 'ZPL Print Manager'
function Invoke-Native([string]$File, [string[]]$Arguments) {
    & $File @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$File failed with exit code $LASTEXITCODE" }
}
function Read-Exact($Stream, [int]$Length) {
    $buffer = New-Object byte[] $Length
    $offset = 0
    while ($offset -lt $Length) {
        $read = $Stream.Read($buffer, $offset, $Length - $offset)
        if ($read -eq 0) { throw 'Service disconnected before completing response.' }
        $offset += $read
    }
    return ,$buffer
}
function Invoke-ServiceCommand([string]$Command, $Payload) {
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'ZplPrintManager.v1', [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::None, [System.Security.Principal.TokenImpersonationLevel]::Impersonation)
    try {
        $pipe.Connect(10000)
        $request = @{ version=1; correlationId=[guid]::NewGuid().ToString(); idempotencyId=[guid]::NewGuid().ToString(); command=$Command; payload=$Payload }
        $bytes = [Text.Encoding]::UTF8.GetBytes(($request | ConvertTo-Json -Depth 8 -Compress))
        $header = [BitConverter]::GetBytes([int]$bytes.Length)
        $pipe.Write($header,0,4); $pipe.Write($bytes,0,$bytes.Length); $pipe.Flush()
        $length = [BitConverter]::ToInt32((Read-Exact $pipe 4),0)
        if ($length -le 0 -or $length -gt 1048576) { throw 'Invalid service response length.' }
        $response = [Text.Encoding]::UTF8.GetString((Read-Exact $pipe $length)) | ConvertFrom-Json
        if (!$response.success) { throw $response.error }
        return $response.payload
    } finally { $pipe.Dispose() }
}
try {
    $existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($Phase -eq 'Prepare') {
        if ($existing) {
            if ($existing.Status -ne 'Running') { Start-Service $serviceName; (Get-Service $serviceName).WaitForStatus('Running',[TimeSpan]::FromSeconds(30)) }
            $null = Invoke-ServiceCommand 'Pause' @{scope='global';id=[guid]::Empty.ToString();paused=$true}
            $backup = Invoke-ServiceCommand 'CreateBackup' @{}
            Write-Host "Pre-upgrade backup: $($backup.path)"
            Stop-Service $serviceName
            (Get-Service $serviceName).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(45))
        }
        exit 0
    }
    if ($Phase -eq 'Remove') {
        if ($existing) {
            if ($existing.Status -ne 'Stopped') { Stop-Service $serviceName; (Get-Service $serviceName).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(45)) }
            Invoke-Native 'sc.exe' @('delete',$serviceName)
        }
        # Preserve ProgramData, groups, credentials, history and remote files.
        exit 0
    }
    if ([string]::IsNullOrWhiteSpace($InstallDirectory)) { throw 'InstallDirectory is required.' }
    $binary = Join-Path $InstallDirectory 'service\ZplPrintManager.Service.exe'
    if (!(Test-Path $binary)) { throw 'Published service executable was not found.' }
    foreach ($group in @('ZplPrintManager Readers','ZplPrintManager Operators','ZplPrintManager Administrators')) {
        if (!(Get-LocalGroup -Name $group -ErrorAction SilentlyContinue)) { New-LocalGroup -Name $group -Description 'ZPL Print Manager local authorization' | Out-Null }
    }
    # Membership is explicit; administrators grant operator/reader access after installation.
    if (!$existing) { New-Service -Name $serviceName -BinaryPathName ('"' + $binary + '"') -DisplayName 'ZPL Print Manager' -StartupType Automatic | Out-Null }
    Invoke-Native 'sc.exe' @('config',$serviceName,'obj=',('NT SERVICE\' + $serviceName),'start=','delayed-auto')
    Invoke-Native 'sc.exe' @('sidtype',$serviceName,'unrestricted')
    Invoke-Native 'sc.exe' @('failure',$serviceName,'reset=','86400','actions=','restart/60000/restart/300000/none/0')
    Invoke-Native 'sc.exe' @('failureflag',$serviceName,'1')
    New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null
    if ((Get-Item $dataRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'ProgramData directory cannot be a reparse point.' }
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true,$false)
    $administrators = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $acl.SetOwner($administrators)
    $principals = @(
        (New-Object Security.Principal.SecurityIdentifier('S-1-5-18')),
        $administrators,
        (New-Object Security.Principal.NTAccount('NT SERVICE', $serviceName))
    )
    foreach ($principal in $principals) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule($principal,'FullControl','ContainerInherit,ObjectInherit','None','Allow')
        $acl.AddAccessRule($rule)
    }
    Set-Acl -Path $dataRoot -AclObject $acl
    foreach ($child in Get-ChildItem -LiteralPath $dataRoot -Force) {
        if ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse point found in operational data.' }
        Invoke-Native 'icacls.exe' @($child.FullName,'/reset','/T')
    }
    foreach ($folder in @('data','secrets','spool','quarantine','logs','backups')) { New-Item -ItemType Directory -Force -Path (Join-Path $dataRoot $folder) | Out-Null }
    if (![Diagnostics.EventLog]::SourceExists($serviceName)) { New-EventLog -LogName Application -Source $serviceName }
    Start-Service $serviceName
    (Get-Service $serviceName).WaitForStatus('Running',[TimeSpan]::FromSeconds(30))
    $status = Invoke-ServiceCommand 'GetStatus' @{}
    Write-Host "Service healthy. Paused: $($status.paused)"
    exit 0
} catch {
    Write-Error $_ -ErrorAction Continue
    exit 1
}
