function Assert-PackagedNknPathNoReparsePoint {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [switch]$Recurse
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $current = $fullPath
    while (-not [string]::IsNullOrWhiteSpace($current)) {
        $item = $null
        try { $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop }
        catch [System.Management.Automation.ItemNotFoundException] { }
        if ($null -ne $item -and ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
            throw "Packaged runtime path contains a reparse point: $current"
        }
        $current = Split-Path -Parent $current
    }

    if ($Recurse -and (Test-Path -LiteralPath $fullPath -PathType Container)) {
        $pending = [System.Collections.Generic.Stack[string]]::new()
        $pending.Push($fullPath)
        while ($pending.Count -gt 0) {
            foreach ($item in Get-ChildItem -LiteralPath $pending.Pop() -Force -ErrorAction Stop) {
                if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
                    throw "Packaged runtime path contains a reparse point: $($item.FullName)"
                }
                if ($item.PSIsContainer) { $pending.Push($item.FullName) }
            }
        }
    }
}

function Resolve-PackagedNknFilesystemPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $current = [System.IO.Path]::GetFullPath($Path)
    $suffix = [System.Collections.Generic.Stack[string]]::new()
    while (-not [string]::IsNullOrWhiteSpace($current)) {
        $item = $null
        try { $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop }
        catch [System.Management.Automation.ItemNotFoundException] { }
        if ($null -ne $item) {
            # Filesystem FullName expands existing 8.3 aliases. Resolve the nearest
            # existing ancestor before appending any not-yet-created output suffix.
            $resolved = $item.FullName
            foreach ($part in $suffix) { $resolved = Join-Path $resolved $part }
            return $resolved
        }
        $suffix.Push((Split-Path -Leaf $current))
        $current = Split-Path -Parent $current
    }
    throw "Cannot resolve packaged runtime filesystem path: $Path"
}

function Assert-PackagedNknOutputPath {
    param(
        [Parameter(Mandatory = $true)][string]$ResolvedExePath,
        [Parameter(Mandatory = $true)][string]$OutputPath,
        [switch]$Disjoint
    )

    $exePath = [System.IO.Path]::GetFullPath($ResolvedExePath)
    if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
        throw "Packaged runtime executable was not found: $exePath"
    }
    $appDir = Split-Path -Parent $exePath
    Assert-PackagedNknPathNoReparsePoint -Path $appDir -Recurse
    Assert-PackagedNknPathNoReparsePoint -Path $OutputPath -Recurse:$Disjoint
    $source = (Resolve-PackagedNknFilesystemPath -Path $appDir).TrimEnd('\', '/')
    $output = (Resolve-PackagedNknFilesystemPath -Path $OutputPath).TrimEnd('\', '/')
    if ([string]::Equals($source, $output, [System.StringComparison]::OrdinalIgnoreCase) -or
        $output.StartsWith($source + '\', [System.StringComparison]::OrdinalIgnoreCase) -or
        ($Disjoint -and $source.StartsWith($output + '\', [System.StringComparison]::OrdinalIgnoreCase))) {
        throw "Packaged runtime output overlaps the supplied package: $output"
    }
}

function Assert-PackagedNknBridgeRuntime {
    param([Parameter(Mandatory = $true)][string]$BridgeDir)

    Assert-PackagedNknPathNoReparsePoint -Path $BridgeDir -Recurse
    foreach ($name in @('index.js', 'node.exe', 'bridge-manifest.json',
            'bridge-dependencies.json', 'package.json', 'package-lock.json')) {
        $requiredPath = Join-Path $BridgeDir $name
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "NKN bridge runtime is incomplete; missing '$requiredPath'."
        }
    }
}
