"""Record patch validation and release checksums from actual isolated evidence."""
from datetime import datetime, timedelta, timezone
import hashlib
import json
from pathlib import Path
import shutil
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
EVIDENCE = ROOT / "docs/验证证据/v1.1.1"


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def main():
    EVIDENCE.mkdir(parents=True, exist_ok=True)
    artifacts = ROOT / "artifacts/v1.1.1"
    sources = {
        "core.trx": ROOT / "artifacts/tests/core.trx",
        "recognition.trx": artifacts / "tests/recognition.trx",
        "desktop-results.json": artifacts / "desktop-smoke/desktop-results.json",
        "install-patch-results.json": artifacts / "install-patch-results.json",
        "bitmap-before.json": artifacts / "before/bitmap-probe.json",
        "bitmap-after.json": artifacts / "after/bitmap-probe.json",
    }
    sources = {name: path if path.exists() else EVIDENCE / name for name, path in sources.items()}
    install = read(sources["install-patch-results.json"])
    test_root = Path(install["testDirectory"])
    if not test_root.is_relative_to(ROOT):
        raise ValueError("Installation evidence must stay within this project")
    for name, subdirectory in (("installed-desktop-results.json", "patched-smoke"), ("baseline-desktop-results.json", "baseline-smoke")):
        path = test_root / subdirectory / "desktop-results.json"
        sources[name] = path if path.exists() else EVIDENCE / name
    for name, source in sources.items():
        (EVIDENCE / name).write_text(source.read_text(encoding="utf-8-sig"), encoding="utf-8", newline="\n")
    namespace = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
    counters = ET.parse(sources["core.trx"]).find(".//" + namespace + "Counters").attrib
    recognition = ET.parse(sources["recognition.trx"]).find(".//" + namespace + "Counters").attrib
    desktop = read(sources["desktop-results.json"])
    installed = read(sources["installed-desktop-results.json"])
    baseline = read(sources["baseline-desktop-results.json"])
    before = read(sources["bitmap-before.json"])
    after = read(sources["bitmap-after.json"])
    assert counters["failed"] == "0" and recognition["failed"] == "0"
    assert desktop["success"] and installed["success"] and baseline["success"] and install["success"]
    assert before["staleBitmapReproduced"] and after["displayMatchesCrop"]
    installer = ROOT / "dist/LocalStockManager-1.1.1-win-x64-Setup.exe"
    with installer.open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    assert digest in (installer.with_name(installer.name + ".sha256")).read_text()
    previous_report = ROOT / "docs/验收报告.md"
    previous_archive = ROOT / "docs/验证证据/v1.1/验收报告-v1.1.0.md"
    if previous_report.read_text(encoding="utf-8").startswith("# 本地库存管理 1.1.0") and not previous_archive.exists():
        previous_archive.write_text(previous_report.read_text(encoding="utf-8"), encoding="utf-8", newline="\n")
    report = f"""# 本地库存管理 1.1.1 验收报告

记录时间：{datetime.now(timezone(timedelta(hours=8))):%Y-%m-%d %H:%M}（Asia/Shanghai）。本次使用隔离测试数据，不读取用户API Key，不上传用户照片，不改动真实安装或库存。

## 修复与验证结果

| 检查 | 结果 | 证据与边界 |
|---|---|---|
| 图片缓存根因 | 已复现并修复 | 修改前文件宽{before['fileWidth']}像素、显示宽{before['displayWidth']}像素；修改后显示{after['displayWidth']}×{after['displayHeight']}像素，颜色和选区像素一致 |
| 核心回归 | {counters['passed']}/{counters['total']}通过 | 包含库存、迁移、保留、导出及识别测试 |
| 识别专项 | {recognition['passed']}/{recognition['total']}通过 | 是核心测试的子集；缺字段、额外字段、数字数量、异常数量、重复字段和截断 |
| 自包含WPF | {len(desktop['checks'])}项通过 | 实际预览弹窗应用/取消、像素对照、缩放/滚动/越界坐标、核对门禁、保留原明细、本机OCR |
| 安装基准WPF | {len(baseline['checks'])}项通过 | 安装的v1.1.0基准使用独立数据 |
| 安装后WPF | {len(installed['checks'])}项通过 | 从隔离安装目录运行真实v1.1.1自包含程序 |
| 补丁安装/重复安装/卸载 | {len(install['checks'])}项通过 | {install['storedFileCount']}个已存储文件逐项SHA-256一致，覆盖数据库、照片、DPAPI设置和备份 |
| 本机OCR | 通过 | 源码和未改动的打包工作程序各3张中文样本；3项模型缺失、校验值错误和图片错误协议 |
| 真实云端验收 | 待完成 | 未主动调用真实API；实际失败响应具体字段、识别准确率、Token及计费未验证 |

核心回归包含20万行性能用例；本补丁未修改查询及导出流程。历史性能记录和v1→v2迁移证据保存在`验证证据/v1.1`，不能把历史记录作为本补丁新增交互的证明。

## 数据保护与发布

安装测试使用独立注册标识`LocalStockManager.PatchInstallTest`、快捷方式、安装目录及测试数据。v1.1.0→v1.1.1覆盖安装前后、重复安装后、卸载后，对全部已存储文件比较路径及SHA-256；原v2数据库在启动前字节不变，真实安装注册保持不变。

本补丁不改变数据库结构。现有三年保留维护仍按原规则在应用运行时执行。安装保存数据与运行时历史维护是两个独立行为。

最终安装包采用LZMA；隔离测试安装包采用zlib以缩短测试时间，两者使用相同发布内容。自包含程序版本为1.1.1。

- 安装包：`{installer.name}`，{installer.stat().st_size:,}字节。
- SHA-256：`{digest}`。
- 发布页：https://github.com/traveller1974/Personal-Local-Offline-Storage-System/releases/tag/local-stock-manager-v1.1.1

## 证据与尚未验证范围

精简证据保存在`docs/验证证据/v1.1.1`；文档包附裁剪预览及结果截图。自动WPF验证操作了真实弹窗和业务入口，并检查坐标转换；没有模拟实体鼠标的完整拖动链，人工鼠标体验仍需使用时核对。未测试干净电脑、实体摄像头或不同Windows用户。

编译使用锁定依赖；恢复此前归档的开发工具以复用已验证的本机OCR，未增加运行时依赖。Windows受限环境下的Python协议测试目录改为继承工作区ACL，不影响打包工作程序。

本次生成的测试数据库、开发工具和重复构建输出在证据保存后移到项目外可恢复归档；归档不会释放磁盘空间。
"""
    previous_report.write_text(report, encoding="utf-8", newline="\n")
    dist = ROOT / "dist"
    smoke = artifacts / "desktop-smoke"
    for name in ("库存首页.png", "进货清单.png", "照片核对.png"):
        if (smoke / name).exists():
            shutil.copy2(smoke / name, dist / name)
    for source, name in (("crop-preview-apply.png", "裁剪预览.png"), ("cropped-display.png", "裁剪结果.png")):
        if (smoke / "image-regression" / source).exists():
            shutil.copy2(smoke / "image-regression" / source, dist / name)
    for name in ("使用说明.md", "已知限制.md", "验收报告.md", "发布说明-v1.1.1.md"):
        shutil.copy2(ROOT / "docs" / name, dist / name)
    print(f"Recorded {counters['passed']} core checks, {len(desktop['checks'])} WPF checks, {len(install['checks'])} installation checks")


if __name__ == "__main__":
    main()
