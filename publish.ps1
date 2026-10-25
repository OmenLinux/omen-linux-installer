# Build a single-file OmenPrep.exe into .\artifacts\
# Needs: .NET 10 SDK. Runtime on target PCs: .NET 10 Desktop + Node.js.
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
dotnet publish "$root\windows-tool\OmenPrep.csproj" `
  -c Release `
  -p:PublishProfile=win-x64-single

$exe = Get-Item "$root\artifacts\OmenPrep.exe"
"{0:N1} MB  {1}" -f ($exe.Length / 1MB), $exe.FullName
