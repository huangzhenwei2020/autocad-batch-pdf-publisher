"""Isolated PaddleOCR adapter for the LineVision OCR JSON protocol."""

from __future__ import annotations

import argparse
import json
import math
import os
import sys
import traceback
from typing import Any

PROTOCOL_VERSION = 2
ENGINE_ID = "paddleocr-worker"
EXPECTED_PADDLEOCR_VERSION = "3.7.0"
MODEL_VERSION = "PP-OCRv6-small"


def emit_progress(request_id: str | None, percent: int, stage: str, message: str) -> None:
    print(json.dumps({
        "ProtocolVersion": PROTOCOL_VERSION,
        "RequestId": request_id,
        "Type": "progress",
        "Percent": max(0, min(100, int(percent))),
        "Stage": stage,
        "Message": message,
    }, ensure_ascii=False, separators=(",", ":")), flush=True)


def worker_root() -> str:
    if getattr(sys, "frozen", False):
        return os.path.dirname(os.path.abspath(sys.executable))
    return os.path.dirname(os.path.abspath(__file__))


def packaged_model(name: str) -> str:
    relative = os.path.join("models", name)
    path = os.path.join(worker_root(), relative)
    # Paddle's Windows inference runtime currently fails to parse a PIR model
    # when its model_dir contains non-ASCII characters. The worker changes its
    # current directory to its own folder and deliberately supplies this ASCII
    # relative path, so installation under a Chinese folder remains supported.
    if not os.path.isdir(path):
        raise FileNotFoundError(f"Packaged OCR model is missing: {name}")
    return relative


def result_template(request_id: str | None) -> dict[str, Any]:
    return {
        "ProtocolVersion": PROTOCOL_VERSION,
        "RequestId": request_id,
        "EngineId": ENGINE_ID,
        "EngineVersion": EXPECTED_PADDLEOCR_VERSION,
        "Success": False,
        "Error": None,
        "Language": None,
        "ImageWidth": 0,
        "ImageHeight": 0,
        "TextRegions": [],
    }


def read_request(path: str) -> dict[str, Any]:
    with open(path, "r", encoding="utf-8-sig") as stream:
        request = json.load(stream)
    version = request.get("ProtocolVersion", request.get("protocolVersion"))
    if version != PROTOCOL_VERSION:
        raise ValueError(f"不支持的 OCR 协议版本：{version}。")
    image_path = request.get("ImagePath", request.get("imagePath"))
    if not image_path or not os.path.isfile(image_path):
        raise FileNotFoundError(f"OCR 输入图片不存在：{image_path}")
    return request


def plain(value: Any) -> Any:
    if hasattr(value, "tolist"):
        return value.tolist()
    return value


def prediction_payload(prediction: Any) -> dict[str, Any]:
    value = getattr(prediction, "json", prediction)
    if callable(value):
        value = value()
    if not isinstance(value, dict):
        return {}
    nested = value.get("res")
    return nested if isinstance(nested, dict) else value


def polygon_angle(points: list[list[float]]) -> float:
    if len(points) < 2:
        return 0.0
    return math.degrees(math.atan2(points[1][1] - points[0][1], points[1][0] - points[0][0]))


def orient_polygon_to_text_line(points: list[list[float]]) -> list[list[float]]:
    """Put the detected text-line long edge at points 0 -> 1.

    Paddle's detector emits axis-aligned four-point boxes for many 90 degree
    lines. In those boxes the first edge is the short top edge, so its angle is
    always zero. Rotating the point order by one position preserves the box but
    exposes the actual text baseline direction (90 degrees); the line
    orientation classifier then distinguishes 90 from 270 degrees.
    """
    if len(points) < 4:
        return points
    edge_01 = math.hypot(points[1][0] - points[0][0], points[1][1] - points[0][1])
    edge_12 = math.hypot(points[2][0] - points[1][0], points[2][1] - points[1][1])
    return points if edge_01 >= edge_12 else [points[1], points[2], points[3], points[0]]


def normalize_degrees(value: float) -> float:
    value %= 360.0
    if value > 180.0:
        value -= 360.0
    return value


def recognize(request: dict[str, Any]) -> dict[str, Any]:
    request_id = request.get("RequestId", request.get("requestId"))
    output = result_template(request_id)
    emit_progress(request_id, 8, "runtime", "正在加载 PaddleOCR 运行环境……")
    import paddleocr
    from PIL import Image
    from paddleocr import PaddleOCR

    installed = getattr(paddleocr, "__version__", "unknown")
    if installed != EXPECTED_PADDLEOCR_VERSION:
        raise RuntimeError(f"PaddleOCR 版本不匹配：需要 {EXPECTED_PADDLEOCR_VERSION}，实际 {installed}。")

    language = request.get("Language", request.get("language")) or "zh-Hans-CN"
    paddle_language = "en" if language.lower().startswith("en") else "ch"
    os.environ.setdefault("PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK", "True")
    pipeline_options: dict[str, Any] = {
        "lang": paddle_language,
        "text_detection_model_name": "PP-OCRv6_small_det",
        "text_recognition_model_name": "PP-OCRv6_small_rec",
        # PaddlePaddle 3.3.1 currently has a reproducible oneDNN/PIR failure on
        # Windows CPU inference. Keep the supported worker on the stable plain
        # CPU path until the upstream issue is fixed and revalidated.
        "enable_mkldnn": False,
        "use_doc_orientation_classify": False,
        "use_doc_unwarping": False,
        "use_textline_orientation": True,
    }
    local_models = {
        "text_detection_model_dir": packaged_model("PP-OCRv6_small_det"),
        "text_recognition_model_dir": packaged_model("PP-OCRv6_small_rec"),
        "textline_orientation_model_dir": packaged_model("PP-LCNet_x1_0_textline_ori"),
    }
    pipeline_options.update(local_models)
    emit_progress(request_id, 22, "models", "正在初始化检测、识别和方向模型……")
    pipeline = PaddleOCR(**pipeline_options)
    emit_progress(request_id, 48, "image", "OCR 模型已就绪，正在读取图片……")
    image_path = request.get("ImagePath", request.get("imagePath"))
    with Image.open(image_path) as image:
        output["ImageWidth"], output["ImageHeight"] = image.size
    emit_progress(request_id, 55, "inference", "正在检测并识别文字……")
    predictions = list(pipeline.predict(image_path))
    emit_progress(request_id, 86, "geometry", "正在整理文字方向、位置和置信度……")
    for prediction in predictions:
        data = prediction_payload(prediction)
        texts = list(plain(data.get("rec_texts", [])))
        scores = list(plain(data.get("rec_scores", [])))
        polygons = list(plain(data.get("rec_polys", [])))
        # Paddle returns class ids here: 0 = normal line, 1 = text line is
        # upside down and was rotated 180 degrees before recognition.
        orientations = list(plain(data.get("textline_orientation_angles", [])))
        for index, text in enumerate(texts):
            points = polygons[index] if index < len(polygons) else []
            points = [[float(point[0]), float(point[1])] for point in points]
            if not text or len(points) < 4:
                continue
            points = orient_polygon_to_text_line(points)
            xs = [point[0] for point in points]
            ys = [point[1] for point in points]
            rotation = polygon_angle(points)
            if index < len(orientations) and int(orientations[index]) == 1:
                rotation += 180.0
            output["TextRegions"].append({
                "Text": str(text),
                "X": min(xs), "Y": min(ys),
                "Width": max(xs) - min(xs), "Height": max(ys) - min(ys),
                "RotationDegrees": normalize_degrees(rotation),
                "Confidence": float(scores[index]) if index < len(scores) else 0.0,
                "Polygon": [{"X": point[0], "Y": point[1]} for point in points],
            })
    output["Success"] = True
    output["Language"] = language
    output["EngineVersion"] = installed + "/" + MODEL_VERSION
    emit_progress(request_id, 100, "complete", "文字识别完成")
    return output


def main() -> int:
    # The worker protocol is always UTF-8, regardless of the Windows console code page.
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    if hasattr(sys.stderr, "reconfigure"):
        sys.stderr.reconfigure(encoding="utf-8")
    os.chdir(worker_root())
    parser = argparse.ArgumentParser()
    parser.add_argument("--request")
    parser.add_argument("--output")
    parser.add_argument("--capabilities", action="store_true")
    args = parser.parse_args()
    if args.capabilities:
        print(json.dumps({
            "EngineId": ENGINE_ID, "DisplayName": "PaddleOCR 增强",
            "EngineVersion": EXPECTED_PADDLEOCR_VERSION + "/" + MODEL_VERSION,
            "ProtocolVersion": PROTOCOL_VERSION, "SupportsPolygon": True,
            "SupportsConfidence": True, "SupportsRotation": True, "SupportsProgress": True,
            "Languages": ["zh-Hans-CN", "en-US"],
        }, ensure_ascii=False))
        return 0
    if not args.output:
        raise ValueError("缺少 --output 参数。")
    request: dict[str, Any] = {}
    try:
        request = read_request(args.request)
        emit_progress(request.get("RequestId", request.get("requestId")), 3, "validate", "OCR 请求和图片校验完成")
        output = recognize(request)
        code = 0
    except Exception as exception:
        output = result_template(request.get("RequestId", request.get("requestId")))
        output["Error"] = str(exception)
        traceback.print_exc(file=sys.stderr)
        code = 2
    os.makedirs(os.path.dirname(os.path.abspath(args.output)), exist_ok=True)
    with open(args.output, "w", encoding="utf-8") as stream:
        json.dump(output, stream, ensure_ascii=False, separators=(",", ":"))
    return code


if __name__ == "__main__":
    raise SystemExit(main())
