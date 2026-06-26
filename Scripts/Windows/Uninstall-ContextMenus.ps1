Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$keys = @(
    'Software\Classes\*\shell\CopyPasta',
    'Software\Classes\Directory\shell\CopyPasta',
    'Software\Classes\Directory\Background\shell\CopyPasta'
)

foreach ($key in $keys) {
    $existingKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($key)
    if ($null -eq $existingKey) {
        continue
    }

    $existingKey.Dispose()
    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($key)
}

Write-Host "Removed Copy Pasta context menus for the current user."
