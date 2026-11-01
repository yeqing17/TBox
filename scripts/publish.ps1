# Builds a runnable TBox into .\dist (green-folder deployment, no installer).
# Requires the .NET 8 SDK; the published app needs .NET 8 Desktop Runtime + Windows App Runtime 1.7.
param(
    [string]$Output = ""
)

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\TBox.WinUI\TBox.WinUI.csproj"
if ([string]::IsNullOrWhiteSpace($Output)) { $Output = Join-Path $root "dist" }

Write-Host "Publishing TBox -> $Output"
dotnet publish $project -c Release -r win-x64 --self-contained false -p:Platform=x64 -o $Output

if ($LASTEXITCODE -ne 0) { Write-Error "publish failed"; exit $LASTEXITCODE }
Write-Host "Done: $(Join-Path $Output 'TBox.exe')"
