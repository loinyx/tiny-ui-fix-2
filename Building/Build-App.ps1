<# SPDX-LICENSE-IDENTIFIER: BSL-1.0 #>

<#
	Builds Tiny UI Fix 2, the Windows app that runs the Tiny UI Fix without its web configurator,
	as one self-contained exe with the tool's .bat inside it.

	The .bat comes from Package-SingleFileDistributables.ps1 (or pass any built tiny-ui-fix-for-ts3.bat).
	Needs the .NET 8 SDK; the Windows App SDK packages are restored from NuGet.
#>

[CmdletBinding()]
Param
(
	[Parameter()]
		[ValidateNotNullOrEmpty()]
			[String] $ToolBat = (Join-Path $PSScriptRoot '../Build/Package/Release/TinyUIFixForTS3/tiny-ui-fix-for-ts3.bat'),

	[Parameter()]
		[ValidateNotNullOrEmpty()]
			[String] $OutputPath = (Join-Path $PSScriptRoot '../Build/App'),

	[Parameter()]
			[Switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
$AppPath = Join-Path $PSScriptRoot '../TinyUIFixForTS3/App/TinyUIFixApp'
$TestsPath = Join-Path $PSScriptRoot '../TinyUIFixForTS3/App/TinyUIFixApp.Tests'

if (-not (Test-Path -LiteralPath $ToolBat))
{
	throw "The tool's .bat wasn't found at `"$ToolBat`". Build it with Package-SingleFileDistributables.ps1, or pass -ToolBat."
}

if (-not $SkipTests)
{
	dotnet run --project $TestsPath | Write-Host
	if ($LASTEXITCODE -ne 0) {exit $LASTEXITCODE}
}

dotnet publish $AppPath -c Release -o $OutputPath "-p:ToolBat=$((Resolve-Path -LiteralPath $ToolBat).Path)" | Write-Host
if ($LASTEXITCODE -ne 0) {exit $LASTEXITCODE}

Get-Item -LiteralPath (Join-Path $OutputPath 'Tiny UI Fix 2.exe')
