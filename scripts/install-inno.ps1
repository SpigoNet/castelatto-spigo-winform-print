param(
    [string]$Destination = (Join-Path ([IO.Path]::GetTempPath()) 'zpl-innosetup-6.4.3')
)
$ErrorActionPreference = 'Stop'
$version = '6.4.3'
$url = 'https://github.com/jrsoftware/issrc/releases/download/is-6_4_3/innosetup-6.4.3.exe'
$expectedHash = 'f3c42116542c4cc57263c5ba6c4feabfc49fe771f2f98a79d2f7628b8762723b'
$download = Join-Path ([IO.Path]::GetTempPath()) ('zpl-inno-' + [guid]::NewGuid().ToString('N') + '.exe')
try {
    Invoke-WebRequest -Uri $url -OutFile $download
    $actualHash = (Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash
    if ($actualHash -ne $expectedHash) {
        throw "Inno Setup $version checksum mismatch. Installation aborted."
    }
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', "/DIR=`"$Destination`"")
    $process = Start-Process -FilePath $download -ArgumentList $arguments -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "Inno Setup installation failed with exit code $($process.ExitCode)."
    }
    $compiler = Join-Path $Destination 'ISCC.exe'
    if (!(Test-Path -LiteralPath $compiler -PathType Leaf)) {
        throw "Inno Setup compiler missing after installation: $compiler"
    }
    if ($env:GITHUB_ENV) {
        "ISCC_PATH=$compiler" | Out-File -LiteralPath $env:GITHUB_ENV -Append -Encoding utf8
    }
    Write-Output "Verified Inno Setup $version installed at $compiler"
} finally {
    Remove-Item -LiteralPath $download -Force -ErrorAction SilentlyContinue
}
