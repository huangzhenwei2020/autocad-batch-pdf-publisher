import json
import os
import subprocess
import sys
import tempfile
import unittest


ROOT = os.path.dirname(os.path.abspath(__file__))
WORKER = os.path.join(ROOT, "worker.py")


class WorkerContractTests(unittest.TestCase):
    def test_capabilities_do_not_require_paddleocr_runtime(self):
        completed = subprocess.run(
            [sys.executable, WORKER, "--capabilities"],
            capture_output=True, text=True, encoding="utf-8", check=True,
        )
        value = json.loads(completed.stdout)
        self.assertEqual(2, value["ProtocolVersion"])
        self.assertEqual("paddleocr-worker", value["EngineId"])
        self.assertTrue(value["SupportsPolygon"])
        self.assertTrue(value["SupportsProgress"])

    def test_missing_runtime_returns_versioned_failure_instead_of_crashing_host(self):
        with tempfile.TemporaryDirectory(prefix="linevision-paddle-test-") as root:
            image_path = os.path.join(root, "empty.png")
            request_path = os.path.join(root, "request.json")
            output_path = os.path.join(root, "result.json")
            with open(image_path, "wb") as stream:
                stream.write(b"not-needed-before-runtime-import")
            with open(request_path, "w", encoding="utf-8") as stream:
                json.dump({
                    "ProtocolVersion": 2,
                    "RequestId": "contract-test",
                    "ImagePath": image_path,
                    "Language": "zh-Hans-CN",
                }, stream)
            completed = subprocess.run(
                [sys.executable, WORKER, "--request", request_path, "--output", output_path],
                capture_output=True, text=True, encoding="utf-8",
            )
            self.assertNotEqual(0, completed.returncode)
            with open(output_path, "r", encoding="utf-8") as stream:
                value = json.load(stream)
            self.assertEqual(2, value["ProtocolVersion"])
            self.assertEqual("contract-test", value["RequestId"])
            self.assertEqual("paddleocr-worker", value["EngineId"])
            self.assertFalse(value["Success"])
            self.assertTrue(value["Error"])


if __name__ == "__main__":
    unittest.main()
