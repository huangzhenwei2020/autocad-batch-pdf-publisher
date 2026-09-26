"""Run the same architectural model fixtures through Blender on any desktop OS."""

import argparse
import json
import shutil
import subprocess
import tempfile
import time
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
HERE = Path(__file__).resolve().parent


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--dotnet", default=shutil.which("dotnet"))
    parser.add_argument("--blender", default=shutil.which("blender"))
    parser.add_argument("--output", default=str(ROOT / ".artifacts" / "blender-kernel-probe.json"))
    args = parser.parse_args()
    if not args.dotnet or not args.blender:
        parser.error("specify --dotnet and --blender executable paths")

    output = Path(args.output).resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="wanluo-kernel-probe-") as scratch:
        subprocess.run([args.dotnet, "run", "--project", str(HERE / "BuildingModel.KernelProbe.csproj"),
                        "-c", "Release", "--", scratch], check=True, cwd=ROOT)
        command = [args.blender, "--background", "--factory-startup", "--python",
                   str(HERE / "blender_probe.py"), "--", scratch, str(output)]
        begin = time.perf_counter()
        subprocess.run(command, check=True, cwd=ROOT)
        process_ms = (time.perf_counter() - begin) * 1000
    result = json.loads(output.read_text(encoding="utf-8"))
    result["process_ms"] = process_ms
    for case in result["cases"]:
        if (not case["all_edges_manifold"] or case["relative_volume_error"] > 0.0001
                or case["max_opening_corner_error_mm"] > 0.1):
            raise AssertionError("geometry check failed: " + case["name"])
    if result["cases"][0]["wall_id"] != result["cases"][1]["wall_id"]:
        raise AssertionError("wall identity changed")
    if result["cases"][0]["opening_id"] != result["cases"][1]["opening_id"]:
        raise AssertionError("opening identity changed")
    output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print("PASS", output)
    print("Blender process ms:", round(process_ms, 1))
    for case in result["cases"]:
        print(case["name"], "faces=", case["faces"],
              "relative_volume_error=", case["relative_volume_error"],
              "opening_corner_error_mm=", case["max_opening_corner_error_mm"],
              "total_ms=", round(case["total_ms"], 1))


if __name__ == "__main__":
    main()
