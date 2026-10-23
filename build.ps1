$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet restore FishReader.csproj --configfile NuGet.Config
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed' }
    dotnet publish FishReader.csproj -c Release --no-restore -o dist
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    Copy-Item -LiteralPath README.md -Destination dist/使用说明.md -Force
    Copy-Item -LiteralPath demo.txt -Destination dist/示例.txt -Force
    Write-Output 'Ready: dist/FishReader.exe'
} finally { Pop-Location }
