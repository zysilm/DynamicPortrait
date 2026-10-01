param([Parameter(Mandatory=$true)][string]$GameExe)
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
$failed = $false
foreach ($match in $signatureMatches) {
    $sig = $match.Groups[1].Value
    $hits = [PortraitSignatureScan]::Find($data, $sig, $start, $length)
    $targets = @($hits | ForEach-Object {
        $target = $_ - $start + $rva
        if ($data[$_] -eq 0xe8 -or $data[$_] -eq 0xe9) {
            $target += 5 + [BitConverter]::ToInt32($data, $_+1)
        }
        $target
    } | Select-Object -Unique)
    if ($targets.Count -ne 1) {
        Write-Output "FAIL $($hits.Count) matches, $($targets.Count) destinations: $sig"
        $failed = $true
    } else {
        Write-Output ('PASS RVA 0x{0:X} ({1} call sites): {2}' -f $targets[0], $hits.Count, $sig)
    }
}
if ($failed) { exit 1 }
Write-Output "$($signatureMatches.Count) signatures resolved to unambiguous destinations. This verifies locations, not runtime behavior or struct layouts."
