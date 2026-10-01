"""Validate local versions and prepare a self-contained custom-repository release."""
import argparse
import json
from pathlib import Path
import re
import shutil
import xml.etree.ElementTree as ET
import zipfile

parser = argparse.ArgumentParser()
parser.add_argument('--check', action='store_true')
parser.add_argument('--tag')
parser.add_argument('--repository', default='zysilm/DynamicPortrait')
args = parser.parse_args()
root = Path(__file__).resolve().parents[2]
project = ET.parse(root / 'DynamicPortrait/DynamicPortrait.csproj')
version = project.findtext('.//Version')
manifest = json.loads((root / 'DynamicPortrait/DynamicPortrait.json').read_text(encoding='utf-8'))
if not version or not re.fullmatch(r'\d+\.\d+\.\d+\.\d+', version):
    raise SystemExit('Expected a four-component project version')
if manifest['AssemblyVersion'] != version or manifest['InternalName'] != 'DynamicPortrait':
    raise SystemExit('Manifest/project version or InternalName mismatch')
if manifest['DalamudApiLevel'] != 15:
    raise SystemExit('Manifest API level does not match SDK 15')
if args.tag and args.tag != f'v{version}':
    raise SystemExit('Release tag does not match project version')
if not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', args.repository):
    raise SystemExit('Invalid repository')
print(f'Version {version}, API 15: OK')
if args.check:
    raise SystemExit(0)

sdk_zip = root / 'DynamicPortrait/bin/Release/DynamicPortrait/latest.zip'
with zipfile.ZipFile(sdk_zip) as archive:
    names = set(archive.namelist())
    for name in ('DynamicPortrait.dll', 'DynamicPortrait.json', 'Silk.NET.Direct3D11.dll'):
        if name not in names:
            raise SystemExit(f'Missing {name} in SDK package')
    shipped = json.loads(archive.read('DynamicPortrait.json'))
    if shipped['AssemblyVersion'] != version:
        raise SystemExit('Packaged assembly version mismatch')

out = root / 'artifacts'
out.mkdir(exist_ok=True)
shutil.copyfile(sdk_zip, out / 'DynamicPortrait.zip')
with zipfile.ZipFile(out / 'DynamicPortrait.zip', 'a', zipfile.ZIP_DEFLATED) as archive:
    for name in ('LICENSE', 'THIRD_PARTY_NOTICES.md'):
        if name not in archive.namelist():
            archive.write(root / name, name)
url = f'https://github.com/{args.repository}/releases/download/v{version}/DynamicPortrait.zip'
manifest.update(RepoUrl=f'https://github.com/{args.repository}',
                DownloadLinkInstall=url, DownloadLinkUpdate=url, DownloadLinkTesting=url)
index_path = root / 'pluginmaster.json'
index = json.loads(index_path.read_text(encoding='utf-8')) if index_path.exists() else []
index = [entry for entry in index if entry.get('InternalName') != 'DynamicPortrait']
index.append(manifest)
(out / 'pluginmaster.json').write_text(json.dumps(index, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print(f'Prepared {out / "DynamicPortrait.zip"}')
