"""Package current documentation and compact validation evidence for a release."""
from pathlib import Path
import shutil
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def main():
    dist = ROOT / "dist"
    dist.mkdir(exist_ok=True)
    names = ("使用说明.md", "已知限制.md", "升级计划-v1.1.md", "样单人工基准.json", "验收报告.md")
    for name in names:
        shutil.copy2(ROOT / "docs" / name, dist / name)
    archive = dist / "LocalStockManager-1.1.0-docs.zip"
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as bundle:
        for name in names:
            bundle.write(dist / name, name)
        bundle.write(ROOT / "THIRD-PARTY-NOTICES.md", "THIRD-PARTY-NOTICES.md")
        for path in sorted((ROOT / "docs/验证证据/v1.1").rglob("*")):
            if path.is_file():
                bundle.write(path, path.relative_to(ROOT / "docs").as_posix())
        for name in ("库存首页.png", "进货清单.png", "照片核对.png"):
            bundle.write(dist / name, name)
    with zipfile.ZipFile(archive) as bundle:
        assert bundle.testzip() is None
    print(f"Documentation archive: {archive.name} ({archive.stat().st_size:,} bytes)")


if __name__ == "__main__":
    main()
