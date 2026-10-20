param([string]$Version='0.1.0')
$ErrorActionPreference='Stop'
foreach ($item in @(@('Service','service'),@('Manager','manager'))) {
    & dotnet publish "src/ZplPrintManager.$($item[0])/ZplPrintManager.$($item[0]).csproj" -c Release -r win-x64 --self-contained true -p:EnableWindowsTargeting=true -p:PublishTrimmed=false -p:PublishReadyToRun=false -p:RestoreLockedMode=true "-p:Version=$Version" -o "artifacts/publish/$($item[1])"
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $($item[0])" }
}
