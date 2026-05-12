param(
	[string]$Configuration = "Release",
	[string]$RuntimeIdentifier = "win-x64"
)

$ErrorActionPreference = "Stop"

$projectRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$framework = "net10.0-windows10.0.19041.0"
$outputRoot = Join-Path $projectRoot "artifacts"
$publishDir = Join-Path $outputRoot "WindowsAiTranscriber-local"
$zipPath = Join-Path $outputRoot "WindowsAiTranscriber-local.zip"

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null

if (Test-Path -LiteralPath $publishDir) {
	Remove-Item -LiteralPath $publishDir -Recurse -Force
}

if (Test-Path -LiteralPath $zipPath) {
	Remove-Item -LiteralPath $zipPath -Force
}

dotnet publish (Join-Path $projectRoot "WindowsAiTranscriber.csproj") `
	-f $framework `
	-c $Configuration `
	-p:RuntimeIdentifierOverride=$RuntimeIdentifier `
	-p:WindowsPackageType=None `
	-p:WindowsAppSDKSelfContained=true `
	-o $publishDir

if ($LASTEXITCODE -ne 0) {
	throw "dotnet publish failed with exit code $LASTEXITCODE."
}

Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -Force

Write-Host ""
Write-Host "Publish folder:" $publishDir
Write-Host "Zip package:" $zipPath
