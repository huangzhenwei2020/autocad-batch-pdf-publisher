"""Headless Blender mesh-boolean probe. Invoked by run_probe.py inside Blender."""

import json
import math
import sys
import time

import bmesh
import bpy
from mathutils import Vector


def box(name, center, size):
    bpy.ops.mesh.primitive_cube_add(size=1.0, location=center)
    obj = bpy.context.object
    obj.name = name
    # Geometry stays near its local origin and uses metres, even though the
    # source model stores double-precision architectural coordinates in mm.
    obj.scale = size
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    return obj


def measure(model):
    wall = model["Walls"][0]
    opening = model["Openings"][0]
    storey = model["Storeys"][0]
    start = time.perf_counter()

    x1, y1 = float(wall["X1"]), float(wall["Y1"])
    x2, y2 = float(wall["X2"]), float(wall["Y2"])
    length_mm = math.hypot(x2 - x1, y2 - y1)
    if length_mm <= 0:
        raise ValueError("zero-length wall")
    ux, uy = (x2 - x1) / length_mm, (y2 - y1) / length_mm
    angle = math.atan2(uy, ux)
    height_mm = float(wall["Height"] or storey["Height"])
    thickness_mm = float(wall["Thickness"])
    width_mm = float(opening["Width"])
    opening_height_mm = float(opening["Height"])
    offset_mm = float(opening["Offset"])
    sill_mm = float(opening["Sill"])
    if offset_mm - width_mm / 2 < 0 or offset_mm + width_mm / 2 > length_mm:
        raise ValueError("opening extends beyond wall")

    wall_obj = box("wall-" + wall["Id"], (0, 0, 0),
                   (length_mm / 1000, thickness_mm / 1000, height_mm / 1000))
    cutter = box("opening-" + opening["Id"],
                 ((offset_mm - length_mm / 2) / 1000, 0,
                  (sill_mm + opening_height_mm / 2 - height_mm / 2) / 1000),
                 (width_mm / 1000, thickness_mm / 1000 + 0.02, opening_height_mm / 1000))
    modifier = wall_obj.modifiers.new("window-cut", "BOOLEAN")
    modifier.operation = "DIFFERENCE"
    modifier.solver = "EXACT"
    modifier.object = cutter
    setup_ms = (time.perf_counter() - start) * 1000

    evaluated = wall_obj.evaluated_get(bpy.context.evaluated_depsgraph_get())
    mesh = evaluated.to_mesh()
    try:
        left = (offset_mm - width_mm / 2 - length_mm / 2) / 1000
        right = (offset_mm + width_mm / 2 - length_mm / 2) / 1000
        bottom = (sill_mm - height_mm / 2) / 1000
        top = (sill_mm + opening_height_mm - height_mm / 2) / 1000
        vertices = [vertex.co for vertex in mesh.vertices]
        corner_error_mm = max(
            min(math.hypot((point.x - x) * 1000, (point.z - z) * 1000)
                for point in vertices)
            for x, z in ((left, bottom), (left, top), (right, bottom), (right, top))
        )
        bm = bmesh.new()
        try:
            bm.from_mesh(mesh)
            volume_mm3 = bm.calc_volume(signed=False) * 1_000_000_000
            manifold = all(edge.is_manifold for edge in bm.edges)
        finally:
            bm.free()
        evaluated_ms = (time.perf_counter() - start) * 1000 - setup_ms
        expected_mm3 = (length_mm * thickness_mm * height_mm
                        - width_mm * thickness_mm * opening_height_mm)
        return {
            "wall_id": wall["Id"], "opening_id": opening["Id"],
            "source_length_mm": length_mm, "source_thickness_mm": thickness_mm,
            "source_window_offset_mm": offset_mm,
            "vertices": len(mesh.vertices), "faces": len(mesh.polygons),
            "all_edges_manifold": manifold,
            "max_opening_corner_error_mm": corner_error_mm,
            "volume_mm3": volume_mm3, "expected_volume_mm3": expected_mm3,
            "relative_volume_error": abs(volume_mm3 - expected_mm3) / expected_mm3,
            "setup_ms": setup_ms, "evaluate_ms": evaluated_ms,
            "total_ms": setup_ms + evaluated_ms,
        }
    finally:
        evaluated.to_mesh_clear()
        bpy.data.objects.remove(cutter, do_unlink=True)
        bpy.data.objects.remove(wall_obj, do_unlink=True)


def main():
    arguments = sys.argv[sys.argv.index("--") + 1:]
    if len(arguments) != 2:
        raise ValueError("expected fixture directory and output JSON path")
    fixture_dir, output_path = arguments
    results = []
    for name in ("wall-window-before", "wall-window-after"):
        with open(f"{fixture_dir}/{name}.json", encoding="utf-8") as stream:
            results.append({"name": name, **measure(json.load(stream))})
    with open(output_path, "w", encoding="utf-8") as stream:
        json.dump({"blender_version": bpy.app.version_string, "cases": results}, stream,
                  ensure_ascii=False, indent=2)
    print("KERNEL_PROBE_OK", output_path)


main()
