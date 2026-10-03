param([Parameter(Mandatory=$true)][string]$GameExe, [switch]$RenderCandidates)
$ErrorActionPreference = 'Stop'
# Read-only PE scan. This never opens the game process or writes game files.
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
public static class PortraitSignatureScan {
    public static int[] Find(byte[] data, string signature, int start, int length) {
        var tokens = signature.Split(new[] {' '}, StringSplitOptions.RemoveEmptyEntries);
        var bytes = new int[tokens.Length];
        for (int j=0; j<tokens.Length; j++) bytes[j] = tokens[j].Contains("?") ? -1 : Convert.ToInt32(tokens[j], 16);
        var hits = new List<int>();
        for (int i=start; i<=start+length-bytes.Length; i++) {
            int j=0;
            for (; j<bytes.Length; j++) if (bytes[j]>=0 && data[i+j]!=bytes[j]) break;
            if (j==bytes.Length) hits.Add(i);
        }
        return hits.ToArray();
    }
}
'@
$data = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $GameExe))
$pe = [BitConverter]::ToInt32($data, 0x3c)
if ([BitConverter]::ToUInt32($data, $pe) -ne 0x4550) { throw 'Not a PE image' }
$sections = [BitConverter]::ToUInt16($data, $pe + 6)
$table = $pe + 24 + [BitConverter]::ToUInt16($data, $pe + 20)
$start = 0; $length = 0; $rva = 0
for ($i=0; $i -lt $sections; $i++) {
    $offset = $table + 40*$i
    $name = [Text.Encoding]::ASCII.GetString($data, $offset, 8).Trim([char]0)
    if ($name -eq '.text') {
        $start = [BitConverter]::ToInt32($data, $offset+20)
        $length = [BitConverter]::ToInt32($data, $offset+16)
        $rva = [BitConverter]::ToInt32($data, $offset+12)
    }
}
if ($length -le 0) { throw 'No .text section' }
$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../DynamicPortrait/Rendering/PortraitRenderer.cs') -Raw
$signatureMatches = [regex]::Matches($source, 'scanner\.ScanText\("([^"]+)"\)')
$entries = @($signatureMatches | ForEach-Object { @{ Name = 'Portrait'; Signature = $_.Groups[1].Value } })
if ($RenderCandidates) {
    # Candidates from local FFXIVClientStructs. Resolving their signatures does
    # not authorize invoking them or establish a render-only pass boundary.
    $entries += @(
        @{ Name = 'RenderManager.Render'; Signature = '40 53 57 41 54 41 55 48 83 EC ?? 65 48 8B 04 25' }
        @{ Name = 'RenderManager.RenderView'; Signature = 'E8 ?? ?? ?? ?? FF C5 49 83 C6 ?? BA' }
        @{ Name = 'TaskManager.ExecuteAllTasks'; Signature = 'E8 ?? ?? ?? ?? 48 8B 8B ?? ?? ?? ?? 48 85 C9 74 ?? F3 0F 10 8B' }
        @{ Name = 'Device.BeginBatch'; Signature = 'E8 ?? ?? ?? ?? F3 0F 10 83 ?? ?? ?? ?? E8' }
        @{ Name = 'Device.EndBatch'; Signature = '48 89 5C 24 10 48 89 6C 24 18 48 89 74 24 20 57 41 54 41 55 41 56 41 57 B8 30 43 00 00' }
        @{ Name = 'RenderManager.Update'; Signature = '40 56 48 83 EC ?? 48 8B F1 44 0F 29 54 24 ?? 8B 89 ?? ?? ?? ?? 44 0F 28 D1' }
    )
}
$failed = $false
foreach ($entry in $entries) {
    $sig = $entry.Signature
    $hits = [PortraitSignatureScan]::Find($data, $sig, $start, $length)
    $targets = @($hits | ForEach-Object {
        $target = $_ - $start + $rva
        if ($data[$_] -eq 0xe8 -or $data[$_] -eq 0xe9) {
            $target += 5 + [BitConverter]::ToInt32($data, $_+1)
        }
        $target
    } | Select-Object -Unique)
    if ($targets.Count -ne 1) {
        Write-Output "FAIL $($entry.Name): $($hits.Count) matches, $($targets.Count) destinations ($($targets -join ', ')): $sig"
        $failed = $true
    } else {
        Write-Output ('PASS {0}: RVA 0x{1:X} ({2} sites): {3}' -f $entry.Name, $targets[0], $hits.Count, $sig)
    }
}
if ($failed) { exit 1 }
Write-Output "$($entries.Count) signatures resolved to unambiguous destinations. This verifies locations, not runtime behavior or struct layouts."
