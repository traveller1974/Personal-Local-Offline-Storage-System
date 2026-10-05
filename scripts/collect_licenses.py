"""Collect original notices and dependency metadata from the locked local packages."""
import importlib.metadata as md
import json
from pathlib import Path
import shutil
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
DEST = ROOT / 'licenses'


def is_notice(path):
    name = path.name.upper()
    return path.is_file() and any(word in name for word in ['LICENSE', 'LICENCE', 'COPYING', 'NOTICE']) and path.stat().st_size < 4_000_000


def main():
    DEST.mkdir(exist_ok=True)
    if not (DEST / 'sources/geos-3.13.1.tar.bz2').is_file():
        raise RuntimeError('GEOS corresponding source is missing. Run scripts/fetch_licenses.ps1.')
    python_packages = []
    for distribution in md.distributions():
        name = distribution.metadata['Name']
        if name.lower() == 'pip':
            continue
        target = DEST / 'python' / f'{name}-{distribution.version}'
        count = 0
        for item in distribution.files or []:
            original = Path(distribution.locate_file(item))
            if is_notice(original):
                target.mkdir(parents=True, exist_ok=True)
                shutil.copy2(original, target / (str(item).replace('/', '_').replace('\\', '_')))
                count += 1
        python_packages.append({'name': name, 'version': distribution.version,
                                'license': distribution.metadata.get('License-Expression') or distribution.metadata.get('License') or '',
                                'homepage': distribution.metadata.get('Home-page', ''),
                                'notices': count})
    nuget_packages = []
    for nuspec in (ROOT / 'tools/nuget-packages').glob('*/*/*.nuspec'):
        tree = ET.parse(nuspec)
        metadata = next(e for e in tree.getroot() if e.tag.endswith('metadata'))
        values = {e.tag.rsplit('}', 1)[-1]: (e.text or '') for e in metadata}
        package = nuspec.parent
        target = DEST / 'nuget' / f"{values['id']}-{values['version']}"
        count = 0
        for original in package.rglob('*'):
            if is_notice(original):
                target.mkdir(parents=True, exist_ok=True)
                shutil.copy2(original, target / str(original.relative_to(package)).replace('\\', '_'))
                count += 1
        nuget_packages.append({'name': values['id'], 'version': values['version'],
                               'authors': values.get('authors', ''), 'copyright': values.get('copyright', ''),
                               'license': values.get('license', ''), 'licenseUrl': values.get('licenseUrl', ''),
                               'homepage': values.get('projectUrl', ''), 'notices': count})
    for original, name in [(ROOT / 'tools/python312/tools/LICENSE.txt', 'Python-3.12.10-LICENSE.txt'),
                           (ROOT / 'tools/nsis/nsis-3.11/COPYING', 'NSIS-3.11-COPYING.txt'),
                           (ROOT / 'ocr/.venv/Lib/site-packages/onnxruntime/LICENSE', 'ONNX-Runtime-LICENSE.txt'),
                           (ROOT / 'ocr/models/manifest.json', 'model-manifest.json')]:
        shutil.copy2(original, DEST / name)
    (DEST / 'dependency-inventory.json').write_text(json.dumps({'python': python_packages, 'nuget': nuget_packages}, ensure_ascii=False, indent=2), encoding='utf-8')
    print(f'Collected notices: {len(python_packages)} Python and {len(nuget_packages)} NuGet packages.')


if __name__ == '__main__':
    main()
