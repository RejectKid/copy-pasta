param(
    [string] $AppPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-CopyPastaPath {
    param([string] $CandidatePath)

    if ($CandidatePath) {
        return (Resolve-Path -LiteralPath $CandidatePath).Path
    }

    $repoRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')
    $candidates = @(
        (Join-Path $PSScriptRoot 'CopyPasta.exe'),
        (Join-Path $repoRoot 'CopyPasta.exe'),
        (Join-Path $repoRoot 'bin\Release\net10.0-windows\win-x64\publish\CopyPasta.exe'),
        (Join-Path $repoRoot 'bin\Release\net10.0-windows\CopyPasta.exe'),
        (Join-Path $repoRoot 'bin\Debug\net10.0-windows\CopyPasta.exe')
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw 'CopyPasta.exe was not found. Pass -AppPath with the full path to CopyPasta.exe.'
}

function Set-RegistryString {
    param(
        [string] $SubKey,
        [string] $Name,
        [string] $Value
    )

    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($SubKey)
    if ($null -eq $key) {
        throw "Could not open HKCU\$SubKey."
    }

    try {
        $key.SetValue($Name, $Value, [Microsoft.Win32.RegistryValueKind]::String)
    }
    finally {
        $key.Dispose()
    }
}

$resolvedAppPath = Resolve-CopyPastaPath -CandidatePath $AppPath
$iconValue = "`"$resolvedAppPath`",0"

$items = @(
    @{
        SubKey = 'Software\Classes\*\shell\CopyPasta'
        Label = 'Add file path to Copy Pasta'
        Argument = '%1'
    },
    @{
        SubKey = 'Software\Classes\Directory\shell\CopyPasta'
        Label = 'Add folder path to Copy Pasta'
        Argument = '%V'
    },
    @{
        SubKey = 'Software\Classes\Directory\Background\shell\CopyPasta'
        Label = 'Add current folder to Copy Pasta'
        Argument = '%V'
    },
    @{
        SubKey = 'Software\Classes\DesktopBackground\shell\CopyPasta'
        Label = 'Add desktop folder to Copy Pasta'
        Argument = '%V'
    }
)

foreach ($item in $items) {
    $shellSubKey = $item.SubKey
    $commandSubKey = "$shellSubKey\command"
    $commandValue = "`"$resolvedAppPath`" --add-to-history `"$($item.Argument)`""

    Set-RegistryString -SubKey $shellSubKey -Name '' -Value $item.Label
    Set-RegistryString -SubKey $shellSubKey -Name 'Icon' -Value $iconValue
    Set-RegistryString -SubKey $shellSubKey -Name 'NoWorkingDirectory' -Value ''
    Set-RegistryString -SubKey $commandSubKey -Name '' -Value $commandValue
}

Write-Host "Installed Copy Pasta context menus for the current user."
Write-Host "App path: $resolvedAppPath"
