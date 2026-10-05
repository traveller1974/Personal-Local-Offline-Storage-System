"""Offline OCR worker. stdout is reserved for one JSON response per request."""
from __future__ import annotations

import contextlib
import hashlib
import json
from pathlib import Path
import sys
import traceback

PROTOCOL_VERSION = 1
RESOURCE_ROOT = Path(getattr(sys, "_MEIPASS", Path(__file__).resolve().parent))
MODEL_ROOT = RESOURCE_ROOT / "models"
_engine = None


class WorkerError(Exception):
    def __init__(self, code: str, message: str):
        super().__init__(message)
        self.code = code


def engine():
    global _engine
    if _engine is not None:
        return _engine
    manifest_path = MODEL_ROOT / "manifest.json"
    if not manifest_path.is_file():
        raise WorkerError("MODEL_MISSING", "离线识别模型清单缺失，请重新安装软件。")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    for name, expected in manifest["sha256"].items():
        file = MODEL_ROOT / name
        if not file.is_file():
            raise WorkerError("MODEL_MISSING", f"离线模型缺失：{name}")
        if hashlib.sha256(file.read_bytes()).hexdigest().lower() != expected.lower():
            raise WorkerError("MODEL_INVALID", f"离线模型校验失败：{name}")
    from rapidocr_onnxruntime import RapidOCR
    _engine = RapidOCR(
        det_model_path=str(MODEL_ROOT / "ch_PP-OCRv4_det_infer.onnx"),
        cls_model_path=str(MODEL_ROOT / "ch_ppocr_mobile_v2.0_cls_infer.onnx"),
        rec_model_path=str(MODEL_ROOT / "ch_PP-OCRv4_rec_infer.onnx"),
        intra_op_num_threads=2, inter_op_num_threads=1,
        print_verbose=False, text_score=0.35, det_limit_side_len=1280,
    )
    return _engine


def process(request: dict) -> dict:
    if request.get("protocolVersion") != PROTOCOL_VERSION:
        raise WorkerError("PROTOCOL_INVALID", "识别协议版本不兼容。")
    if not isinstance(request.get("requestId"), str) or not request["requestId"]:
        raise WorkerError("PROTOCOL_INVALID", "缺少识别请求编号。")
    image_path = request.get("imagePath")
    if not isinstance(image_path, str) or not image_path:
        raise WorkerError("IMAGE_MISSING", "未提供照片路径。")
    path = Path(image_path)
    if not path.is_file():
        raise WorkerError("IMAGE_MISSING", "照片不存在或已被移动。")
    if path.suffix.lower() not in {".jpg", ".jpeg", ".png"}:
        raise WorkerError("FORMAT_UNSUPPORTED", "照片格式不支持，请使用 JPG 或 PNG。")
    if path.stat().st_size > 100 * 1024 * 1024:
        raise WorkerError("IMAGE_TOO_LARGE", "照片超过100MB，请缩小照片后重试。")
    import cv2
    import numpy as np
    pixels = cv2.imdecode(np.fromfile(str(path), dtype=np.uint8), cv2.IMREAD_COLOR)
    if pixels is None:
        raise WorkerError("IMAGE_INVALID", "无法读取照片内容，请换一张照片。")
    height, width = pixels.shape[:2]
    if width * height > 60_000_000:
        raise WorkerError("IMAGE_TOO_LARGE", "照片超过6000万像素，请缩小照片后重试。")
    results, _ = engine()(pixels)
    blocks = []
    for box, text, confidence in results or []:
        blocks.append({"text": str(text), "confidence": float(confidence),
                       "box": [[float(x), float(y)] for x, y in box]})
    return {"requestId": request["requestId"], "success": True,
            "imageWidth": width, "imageHeight": height, "blocks": blocks}


def main():
    sys.stdin.reconfigure(encoding="utf-8")
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    protocol_output = sys.stdout
    for line in sys.stdin:
        request = {}
        try:
            if len(line) > 1024 * 1024:
                raise WorkerError("PROTOCOL_INVALID", "识别请求过长。")
            request = json.loads(line)
            if not isinstance(request, dict):
                request = {}
                raise WorkerError("PROTOCOL_INVALID", "识别请求必须是JSON对象。")
            # Native dependencies must not pollute the JSON protocol channel.
            with contextlib.redirect_stdout(sys.stderr):
                response = process(request)
        except WorkerError as exc:
            response = {"requestId": request.get("requestId", ""), "success": False,
                        "errorCode": exc.code, "message": str(exc)}
        except Exception:
            traceback.print_exc(file=sys.stderr)
            response = {"requestId": request.get("requestId", ""), "success": False,
                        "errorCode": "OCR_FAILED", "message": "照片识别失败，请重试或手动输入。"}
        protocol_output.write(json.dumps(response, ensure_ascii=False, separators=(",", ":")) + "\n")
        protocol_output.flush()


if __name__ == "__main__":
    main()
