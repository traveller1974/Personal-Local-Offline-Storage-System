"""Record current patch validation using isolated evidence, never cloud credentials."""
from datetime import datetime, timedelta, timezone
import hashlib
import json
from pathlib import Path
import shutil
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
VERSION = ET.parse(ROOT / "Directory.Build.props").findtext(".//Version")
EVIDENCE = ROOT / f"docs/验证证据/v{VERSION}"


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def main():
    EVIDENCE.mkdir(parents=True, exist_ok=True)
    artifacts = ROOT / f"artifacts/v{VERSION}"
    sources = {
        "core.trx": ROOT / "artifacts/tests/core.trx",
        "recognition.trx": artifacts / "tests/recognition.trx",
        "desktop-results.json": artifacts / "desktop-smoke/desktop-results.json",
        "install-patch-results.json": artifacts / "install-patch-results.json",
        "source-ocr-results.json": ROOT / "artifacts/ocr/source-results.json",
        "bundled-ocr-results.json": ROOT / "artifacts/ocr/bundled-results.json",
        "build.log": artifacts / "build.log",
    }
    sources = {name: path if path.exists() else EVIDENCE / name for name, path in sources.items()}
    install = read(sources["install-patch-results.json"])
    test_root = Path(install["testDirectory"])
    if not test_root.is_relative_to(ROOT) or install["targetVersion"] != VERSION:
        raise ValueError("Installation evidence must belong to this project and target version")
    for name, subdirectory in (("installed-desktop-results.json", "patched-smoke"), ("baseline-desktop-results.json", "baseline-smoke")):
        path = test_root / subdirectory / "desktop-results.json"
        sources[name] = path if path.exists() else EVIDENCE / name
    namespace = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
    counters = ET.parse(sources["core.trx"]).find(".//" + namespace + "Counters").attrib
    recognition = ET.parse(sources["recognition.trx"]).find(".//" + namespace + "Counters").attrib
    desktop = read(sources["desktop-results.json"])
    installed = read(sources["installed-desktop-results.json"])
    baseline = read(sources["baseline-desktop-results.json"])
    assert counters["failed"] == "0" and counters["passed"] == counters["total"]
    assert recognition["failed"] == "0" and recognition["passed"] == recognition["total"]
    assert desktop["success"] and installed["success"] and baseline["success"] and install["success"]
    assert install["realInstallationPreserved"] and install["baselineVersion"] == "1.1.1"
    for name in ("source-ocr-results.json", "bundled-ocr-results.json"):
        responses = read(sources[name])["responses"]
        assert len(responses) == 6 and all(r["success"] for r in responses[:3])
        assert [r["errorCode"] for r in responses[3:]] == ["IMAGE_MISSING", "PROTOCOL_INVALID", "FORMAT_UNSUPPORTED"]
    installer = ROOT / f"dist/LocalStockManager-{VERSION}-win-x64-Setup.exe"
    with installer.open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    assert digest in installer.with_name(installer.name + ".sha256").read_text()
    assert f"Installer: {installer}" in sources["build.log"].read_text(encoding="utf-8-sig")
    for name, source in sources.items():
        (EVIDENCE / name).write_text(source.read_text(encoding="utf-8-sig"), encoding="utf-8", newline="\n")
    report = f"""# 本地库存管理 {VERSION} 验收报告

记录时间：{datetime.now(timezone(timedelta(hours=8))):%Y-%m-%d %H:%M}（Asia/Shanghai）。测试使用隔离数据和模拟接口，不读取用户 API Key，不上传用户照片。

## 修复与验证结果

本次修复 `actualQuantityColumn` 返回非布尔值导致整次识别失败的问题。布尔字符串去除首尾空白、忽略大小写后转换；其他值和类型保留可用明细，标记实发列未确认并禁止加入清单。未取得此次失败的真实响应内容。

| 检查 | 结果 | 证据与边界 |
|---|---|---|
| 核心回归 | {counters['passed']}/{counters['total']}通过 | 库存、迁移、保留、导出、性能及识别测试 |
| 识别专项 | {recognition['passed']}/{recognition['total']}通过 | 核心测试的子集；布尔值、字符串、异常类型、隐私提示、模拟HTTP调用、取消、截断和重复字段 |
| 自包含WPF | {len(desktop['checks'])}项通过 | 真实核对界面的提示、保留明细、阻止导入及布尔字符串经核对加入草稿；含既有裁剪回归 |
| 安装基准WPF | {len(baseline['checks'])}项通过 | 安装的v{install['baselineVersion']}基准使用独立数据 |
| 安装后WPF | {len(installed['checks'])}项通过 | 从隔离安装目录运行v{VERSION}自包含程序 |
| 覆盖安装/重复安装/卸载 | {len(install['checks'])}项通过 | {install['storedFileCount']}个既有文件逐项SHA-256一致，含数据库、照片、DPAPI设置和备份 |
| 本机OCR | 通过 | 源码及复用的打包工作程序各3张中文样本及错误协议；构建执行3项模型缺失、校验值错误和图片错误测试 |
| 真实云端验收 | 待完成 | 用户安装后在软件内主动识别原问题图片；模拟测试不能证明真实响应兼容或识别准确率 |

布尔值保持原义；字符串 `true/false` 接受大小写和首尾空白并显示兼容提示。缺失、null、数字、其他文字、数组和对象均不推断为true。不根据数量或合计反推，不以零代替缺失数量。即使所有行已核对或填写合计说明，未确认实发列仍禁止加入。

重复字段、截断和无效明细结构继续拒绝；解析失败保留旧核对内容和草稿，取消与迟到响应通过已有服务测试验证。WPF测试操作真实业务方法与弹窗，未模拟实体鼠标完整拖动链。

## 数据保护与发布

使用独立注册标识、快捷方式、安装目录及测试数据验证v{install['baselineVersion']}→v{VERSION}安装。安装、重复安装和卸载后，全部既有文件的路径与SHA-256一致；真实安装注册保持不变。新版本桌面测试使用另一份独立数据，未触碰基准数据或真实用户数据。

本补丁不改变数据库结构。安装保存数据；现有三年历史维护仍在运行时按原规则执行。最终安装包使用LZMA，隔离安装测试使用zlib，二者使用相同发布内容。

- 安装包：`{installer.name}`，{installer.stat().st_size:,}字节。
- SHA-256：`{digest}`。
- 发布页：https://github.com/traveller1974/Personal-Local-Offline-Storage-System/releases/tag/local-stock-manager-v{VERSION}

## 证据与验证边界

当前证据保存在`docs/验证证据/v{VERSION}`；v1.1.1及更早版本的报告、裁剪根因复现及迁移证据保留在历史目录。编译使用锁定依赖，复用既有离线OCR工作程序，未新增运行时依赖。

未测试真实云端、干净电脑、实体摄像头或不同Windows用户。用户失败响应的具体标志值未知，不能宣称原问题图片已通过真实云端验收。

交付后将恢复的开发工具、临时测试目录和重复构建文件移到项目外可恢复归档，项目保留当前安装包、源码及精简证据。归档不会释放磁盘空间。
"""
    (ROOT / "docs/验收报告.md").write_text(report, encoding="utf-8", newline="\n")
    dist = ROOT / "dist"
    smoke = artifacts / "desktop-smoke"
    for name in ("库存首页.png", "进货清单.png", "照片核对.png"):
        if (smoke / name).exists():
            shutil.copy2(smoke / name, dist / name)
    for source, name in (("crop-preview-apply.png", "裁剪预览.png"), ("cropped-display.png", "裁剪结果.png")):
        if (smoke / "image-regression" / source).exists():
            shutil.copy2(smoke / "image-regression" / source, dist / name)
    for name in ("使用说明.md", "已知限制.md", "验收报告.md", f"发布说明-v{VERSION}.md"):
        shutil.copy2(ROOT / "docs" / name, dist / name)
    print(f"Recorded {counters['passed']} core checks, {len(desktop['checks'])} WPF checks, {len(install['checks'])} installation checks")


if __name__ == "__main__":
    main()
