[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Package,
    [switch]$DevelopmentOutput
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'RuntimeLanguages.ps1')
$root = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Package).Path).TrimEnd('\', '/')
if (!(Test-Path -LiteralPath $root -PathType Container)) {
    throw 'C++ package directory is missing.'
}
$failures = New-Object System.Collections.Generic.List[string]
$required = @(
    'HS2Manager.exe', 'HS2Agent.exe', 'resources.pri', 'MainWindow.xaml', 'CharacterEditorView.xaml', 'CreationView.xaml', 'SettingsView.xaml', 'Styles.xaml',
    'agent.example.json', 'agent.mcp.example.toml',
    'skills/hs2-card-agent/SKILL.md', 'skills/hs2-card-agent/references/assets.md',
    'Microsoft.UI.Xaml.dll', 'Microsoft.UI.Xaml.Controls.dll', 'Microsoft.UI.Xaml.Controls.pri', 'Microsoft.WindowsAppRuntime.dll',
    'zh-CN/Microsoft.ui.xaml.dll.mui', 'zh-CN/Microsoft.UI.Xaml.Phone.dll.mui',
    'en-US/Microsoft.ui.xaml.dll.mui', 'en-US/Microsoft.UI.Xaml.Phone.dll.mui',
    'msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll', 'classdata.tpk', 'native-runtime.json',
    'Assets/Brand/hs2-manager.ico', 'Assets/Brand/hs2-manager.png', 'README.md', 'THIRD-PARTY-NOTICES.md',
    'native/win-x64/HS2Manager.Physics.dll', 'native/win-x64/PhysX3_x64.dll', 'native/win-x64/PhysX3Common_x64.dll',
    'native/win-x64/PxFoundation_x64.dll', 'native/win-x64/PxPvdSDK_x64.dll',
    'native/win-x64/HS2Manager.FbxBridge.dll', 'native/win-x64/libfbxsdk.dll',
    'native/win-x64/7z.dll', 'licenses/7zip-LICENSE.txt', 'licenses/7zip-LGPL.txt', 'licenses/7zip-unRAR.txt',
    'native/win-x64/msvcp140.dll', 'native/win-x64/vcruntime140.dll', 'native/win-x64/vcruntime140_1.dll',
    'Resources/ModelExport/BlenderImport.py', 'Resources/ModelExport/BlenderNative.py',
    'Resources/ModelExport/UnrealImport.py', 'Resources/ModelExport/UnrealNative.py',
    'licenses/AssetRipper-Tpk.txt', 'licenses/Autodesk-FBX-SDK-2020.3.7.rtf', 'licenses/CppWinRT-LICENSE.txt',
    'licenses/LZMA-PUBLIC-DOMAIN.txt', 'licenses/LZ4-LICENSE.txt', 'licenses/cgltf-LICENSE.txt', 'licenses/miniz-LICENSE.txt', 'licenses/nlohmann-json-LICENSE.txt',
    'licenses/NVIDIA-PhysX-3.4.txt', 'licenses/sqlite-LICENSE.txt', 'licenses/Unity-PostProcessing.md',
    'licenses/WindowsAppSDK-LICENSE.txt', 'licenses/WindowsAppSDK-NOTICE.txt'
)
foreach ($relative in $required) {
    $path = Join-Path $root $relative
    if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) {
        $failures.Add("Missing runtime/resource: $relative")
    }
}

function Get-PackageFiles([string]$Directory) {
    $item = Get-Item -LiteralPath $Directory -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "发布目录不能包含链接：$Directory"
    }
    foreach ($child in Get-ChildItem -LiteralPath $Directory -Force) {
        if ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "发布目录不能包含链接：$($child.FullName)"
        }
        if ($child.PSIsContainer) {
            Get-PackageFiles $child.FullName
        }
        else {
            $child
        }
    }
}

function Read-NativePe([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $reader = New-Object IO.BinaryReader($stream)
    try {
        if ($stream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5A4D) {
            throw "无效 PE 文件：$Path"
        }
        $stream.Position = 0x3C
        $pe = $reader.ReadInt32()
        if ($pe -lt 64 -or $pe -gt $stream.Length - 24) {
            throw "无效 PE 头：$Path"
        }
        $stream.Position = $pe
        if ($reader.ReadUInt32() -ne 0x4550) {
            throw "无效 PE 签名：$Path"
        }
        $machine = $reader.ReadUInt16()
        $sectionCount = $reader.ReadUInt16()
        $stream.Position = $pe + 20
        $optionalSize = $reader.ReadUInt16()
        $optional = $pe + 24
        if ($optionalSize -lt 112 -or $optional + $optionalSize + 40 * $sectionCount -gt $stream.Length) {
            throw "截断的 PE 头：$Path"
        }
        $stream.Position = $optional
        # 锁定的 x64 Windows App SDK 还携带用于 Arm 上 x64 仿真的 ARM64X 配套文件，应完整保留；
        # 应用及适配层仍为 x64。
        # 参考：https://learn.microsoft.com/windows/arm/arm64x-pe
        $sdkArmCompanion = $machine -eq 0xAA64 -and [IO.Path]::GetFileName($Path) -eq 'Microsoft.Windows.Workloads.Resources_ec.dll'
        if ($reader.ReadUInt16() -ne 0x20B -or ($machine -ne 0x8664 -and !$sdkArmCompanion)) {
            throw "发布包要求 x64 原生 PE：$Path"
        }
        $stream.Position = $optional + 60
        $headerSize = $reader.ReadUInt32()
        $stream.Position = $optional + 108
        $directoryCount = $reader.ReadUInt32()
        $directories = $optional + 112
        if ($directoryCount -gt [Math]::Floor(($optionalSize - 112) / 8)) {
            throw "无效 PE 数据目录：$Path"
        }
        $clr = $false
        if ($directoryCount -gt 14) {
            $stream.Position = $directories + 14 * 8
            $clrRva = $reader.ReadUInt32()
            $clrBytes = $reader.ReadUInt32()
            $clr = $clrRva -ne 0 -or $clrBytes -ne 0
        }
        $sections = @()
        for ($i = 0; $i -lt $sectionCount; $i++) {
            $stream.Position = $optional + $optionalSize + 40 * $i + 8
            $virtualSize = $reader.ReadUInt32()
            $rva = $reader.ReadUInt32()
            $rawSize = $reader.ReadUInt32()
            $offset = $reader.ReadUInt32()
            $sections += [pscustomobject]@{Rva = [long]$rva
                VirtualSize = [long]$virtualSize
                RawSize = [long]$rawSize
                Offset = [long]$offset
            }
        }

        function Get-FileOffset([long]$Rva) {
            if ($Rva -lt $headerSize -and $Rva -lt $stream.Length) {
                return $Rva
            }
            foreach ($section in $sections) {
                if ($Rva -ge $section.Rva -and $Rva -lt $section.Rva + $section.RawSize) {
                    $result = $section.Offset + $Rva - $section.Rva
                    if ($result -lt $stream.Length) {
                        return $result
                    }
                }
            }
            throw "PE 引用超出文件范围：$Path"
        }

        $imports = New-Object System.Collections.Generic.List[string]
        if ($directoryCount -gt 1) {
            $stream.Position = $directories + 8
            $importRva = $reader.ReadUInt32()
            $importSize = $reader.ReadUInt32()
            if ($importRva) {
                if ($importSize -lt 20) {
                    throw "无效 PE 导入目录：$Path"
                }
                $table = Get-FileOffset $importRva
                $ended = $false
                for ($i = 0; $i -lt [Math]::Min(4096, [Math]::Floor($importSize / 20)); $i++) {
                    $stream.Position = $table + $i * 20
                    if ($stream.Position + 20 -gt $stream.Length) {
                        throw "截断的 PE 导入表：$Path"
                    }
                    $original = $reader.ReadUInt32()
                    $timestamp = $reader.ReadUInt32()
                    $chain = $reader.ReadUInt32()
                    $nameRva = $reader.ReadUInt32()
                    $thunk = $reader.ReadUInt32()
                    if (!$original -and !$timestamp -and !$chain -and !$nameRva -and !$thunk) {
                        $ended = $true
                        break
                    }
                    $stream.Position = Get-FileOffset $nameRva
                    $name = New-Object Text.StringBuilder
                    do {
                        if ($stream.Position -ge $stream.Length -or $name.Length -ge 512) {
                            throw "无效 DLL 导入名称：$Path"
                        }
                        $ch = $reader.ReadByte()
                        if ($ch) {
                            [void]$name.Append([char]$ch)
                        }
                    } while ($ch)
                    $imports.Add($name.ToString())
                }
                if (!$ended) {
                    throw "PE 导入表缺少结束项：$Path"
                }
            }
        }
        return [pscustomobject]@{Clr = $clr
            Imports = $imports.ToArray()
        }
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

$count = 0
foreach ($file in Get-PackageFiles $root) {
    $relative = $file.FullName.Substring($root.Length).TrimStart('\', '/')
    $dataFile = $relative -match '^(Data|Logs|Covers|CardBackups|Operations|NativeCpp|NativePreview[^\\/]*|UnityPreview)[\\/]'
    if ($DevelopmentOutput -and $dataFile) {
        continue
    }
    if (!$DevelopmentOutput -and (Test-UnusedRuntimeLanguageFile $relative)) {
        $failures.Add("Unused runtime language resource: $relative")
    }
    if (!$DevelopmentOutput -and (($dataFile -and $relative -ne 'Data\README.txt') -or
            $file.Extension -in '.db', '.sqlite', '.sqlite3', '.pdb', '.ilk', '.lib', '.exp', '.iobj', '.ipdb', '.log' -or
            $file.Name -match '^(settings\.json|library\.db.*|hs2_.*_(checks|fixtures)\.exe|native-.*error.*)$')) {
        $failures.Add("Development/user data: $relative")
    }
    if ($file.Name -match '^(coreclr|clr|hostfxr|hostpolicy|mscoree|UnityPlayer|GameAssembly|Assembly-CSharp|mono.*|HS2Manager\.(App|Core|Rendering|Export|Bridge|Preview\.Contracts))\.dll$' -or
        $file.Name -like '*.runtimeconfig.json' -or $file.Name -match '^python([\d.]*\.dll|\.exe)$' -or
        $file.Extension -in '.cs', '.csproj', '.zipmod', '.unity3d', '.bundle' -or
        $relative -match '^(UserData|abdata|mods|BepInEx|tests|reference)[\\/]|^Resources[\\/]Agent[\\/]') {
        $failures.Add("Managed/engine/game payload: $relative")
    }
    if ($file.Extension -notin '.exe', '.dll') {
        continue
    }
    $pe = Read-NativePe $file.FullName
    if ($pe.Clr) {
        $failures.Add("CLR header: $relative")
    }
    foreach ($dependency in $pe.Imports) {
        if ($dependency -match '^(api-ms-|ext-ms-)') {
            continue
        }
        if ([IO.Path]::GetFileName($dependency) -ne $dependency) {
            $failures.Add("Invalid DLL import: $relative -> $dependency")
            continue
        }
        if ($dependency -match '^(coreclr|clr|hostfxr|hostpolicy|mscoree|UnityPlayer|GameAssembly|mono.*)\.dll$') {
            $failures.Add("Managed/engine import: $relative -> $dependency")
            continue
        }
        if (!(Test-Path -LiteralPath (Join-Path $file.DirectoryName $dependency) -PathType Leaf) -and
            !(Test-Path -LiteralPath (Join-Path $root $dependency) -PathType Leaf) -and
            !(Test-Path -LiteralPath (Join-Path ([Environment]::SystemDirectory) $dependency) -PathType Leaf)) {
            $failures.Add("Missing DLL import: $relative -> $dependency")
        }
    }
    $count++
}
$runtimeManifest = Join-Path $root 'native-runtime.json'
if (Test-Path -LiteralPath $runtimeManifest -PathType Leaf) {
    $runtimeEntries = Get-Content -LiteralPath $runtimeManifest -Raw | ConvertFrom-Json
    if (@($runtimeEntries).Count -lt 10) {
        $failures.Add('Native runtime manifest is incomplete.')
    }
    foreach ($entry in $runtimeEntries) {
        if ([IO.Path]::GetFileName([string]$entry.File) -ne $entry.File) {
            throw '原生运行库清单路径越界。'
        }
        $path = Join-Path $root ('native/win-x64/' + $entry.File)
        if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -ne $entry.Bytes -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.SHA256) {
            $failures.Add("Native runtime manifest mismatch: $($entry.File)")
        }
    }
}
if ($failures.Count) {
    throw ('C++ 发布目录验证失败：' + [Environment]::NewLine + ($failures -join [Environment]::NewLine))
}
Write-Output "PASS pure C++ package: $count native PE files; x64 app/adapters, complete resources, resolved imports, no CLR"
