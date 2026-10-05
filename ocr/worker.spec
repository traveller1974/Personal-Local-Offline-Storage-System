from PyInstaller.utils.hooks import collect_all
from pathlib import Path

root = Path(SPECPATH)
datas, binaries, hiddenimports = [], [], []
for package in ('rapidocr_onnxruntime', 'onnxruntime', 'shapely', 'pyclipper'):
    d, b, h = collect_all(package)
    datas += d
    binaries += b
    hiddenimports += h
datas += [(str(root / 'models'), 'models')]
a = Analysis([str(root / 'worker.py')], pathex=[str(root)], binaries=binaries,
             datas=datas, hiddenimports=hiddenimports, hookspath=[], hooksconfig={},
             runtime_hooks=[], excludes=['matplotlib', 'pytest', 'tkinter'], noarchive=False)
pyz = PYZ(a.pure)
exe = EXE(pyz, a.scripts, [], exclude_binaries=True, name='StockOcr', debug=False,
          bootloader_ignore_signals=False, strip=False, upx=False, console=True)
coll = COLLECT(exe, a.binaries, a.datas, strip=False, upx=False, name='StockOcr')
