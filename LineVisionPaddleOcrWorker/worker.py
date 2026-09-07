"""Isolated PaddleOCR adapter for the LineVision OCR JSON protocol."""

from __future__ import annotations

import argparse
import json
import math
import os
import sys
from typing import Any

PROTOCOL_VERSION = 1
ENGINE_ID = "paddleocr-worker"
EXPECTED_PADDLEOCR_VERSION = "3.7.0"
MODEL_VERSION = "PP-OCRv6-small"


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


def recognize(request: dict[str, Any]) -> dict[str, Any]:
    request_id = request.get("RequestId", request.get("requestId"))
    output = result_template(request_id)
    import paddleocr
    from PIL import Image
    from paddleocr import PaddleOCR

    installed = getattr(paddleocr, "__version__", "unknown")
    if installed != EXPECTED_PADDLEOCR_VERSION:
        raise RuntimeError(f"PaddleOCR 版本不匹配：需要 {EXPECTED_PADDLEOCR_VERSION}，实际 {installed}。")

    language = request.get("Language", request.get("language")) or "zh-Hans-CN"
    paddle_language = "en" if language.lower().startswith("en") else "ch"
    pipeline = PaddleOCR(
        lang=paddle_language,
        text_detection_model_name="PP-OCRv6_small_det",
        text_recognition_model_name="PP-OCRv6_small_rec",
        use_doc_orientation_classify=True,
        use_doc_unwarping=False,
        use_textline_orientation=True,
    )
    image_path = request.get("ImagePath", request.get("imagePath"))
    with Image.open(image_path) as image:
        output["ImageWidth"], output["ImageHeight"] = image.size
    predictions = list(pipeline.predict(image_path))
    for prediction in predictions:
        data = prediction_payload(prediction)
        texts = list(plain(data.get("rec_texts", [])))
        scores = list(plain(data.get("rec_scores", [])))
        polygons = list(plain(data.get("rec_polys", [])))
        for index, text in enumerate(texts):
            points = polygons[index] if index < len(polygons) else []
            points = [[float(point[0]), float(point[1])] for point in points]
            if not text or len(points) < 4:
                continue
            xs = [point[0] for point in points]
            ys = [point[1] for point in points]
            output["TextRegions"].append({
                "Text": str(text),
                "X": min(xs), "Y": min(ys),
                "Width": max(xs) - min(xs), "Height": max(ys) - min(ys),
                "RotationDegrees": polygon_angle(points),
                "Confidence": float(scores[index]) if index < len(scores) else 0.0,
                "Polygon": [{"X": point[0], "Y": point[1]} for point in points],
            })
    output["Success"] = True
    output["Language"] = language
    output["EngineVersion"] = installed + "/" + MODEL_VERSION
    return output


def main() -> int:
    # The worker protocol is always UTF-8, regardless of the Windows console code page.
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    if hasattr(sys.stderr, "reconfigure"):
        sys.stderr.reconfigure(encoding="utf-8")
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
            "SupportsConfidence": True, "SupportsRotation": True,
            "Languages": ["zh-Hans-CN", "en-US"],
        }, ensure_ascii=False))
        return 0
    if not args.output:
        raise ValueError("缺少 --output 参数。")
    request: dict[str, Any] = {}
    try:
        request = read_request(args.request)
        output = recognize(request)
        code = 0
    except Exception as exception:
        output = result_template(request.get("RequestId", request.get("requestId")))
        output["Error"] = str(exception)
        print(str(exception), file=sys.stderr)
        code = 2
    os.makedirs(os.path.dirname(os.path.abspath(args.output)), exist_ok=True)
    with open(args.output, "w", encoding="utf-8") as stream:
        json.dump(output, stream, ensure_ascii=False, separators=(",", ":"))
    return code


if __name__ == "__main__":
    raise SystemExit(main())
