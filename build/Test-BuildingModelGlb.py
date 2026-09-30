"""Run with Blender --background --python ... -- <input.glb> <roundtrip.glb>."""
import json
import sys
import runpy
from pathlib import Path

import bpy
from mathutils import Vector

source, output = (Path(value).resolve() for value in sys.argv[sys.argv.index("--") + 1:])
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=str(source))
meshes = [obj for obj in bpy.context.scene.objects if obj.type == "MESH"]
clean = runpy.run_path(str(Path(__file__).resolve().parents[1] /
    "BuildingModelStudio.AvaloniaProbe/Resources/Blender/OpenGlb.py"))["clean_planar_mesh"]
before_faces = sum(len(obj.data.polygons) for obj in meshes)
for obj in meshes:
    before_area = sum(p.area for p in obj.data.polygons)
    clean(obj)
    after_area = sum(p.area for p in obj.data.polygons)
    assert abs(before_area-after_area) < max(1e-5, before_area*1e-6), \
        f"Planar cleanup changed area: {obj.name}: {before_area} -> {after_area}"
    assert all(p.area > 0 for p in obj.data.polygons), f"Degenerate face: {obj.name}"
after_faces = sum(len(obj.data.polygons) for obj in meshes)
assert after_faces < before_faces, (before_faces, after_faces)
assert any(len(p.vertices) > 4 for obj in meshes for p in obj.data.polygons), "No n-gons created"
print(f"BLENDER_PLANAR_CLEANUP_OK {before_faces} -> {after_faces} faces")
assert len(meshes) == 16, f"Expected 16 element meshes, got {len(meshes)}"
assert all(obj.get("elementId") for obj in meshes), "Missing stable element IDs"
floors = [obj for obj in bpy.context.scene.objects if obj.get("storeyId") and obj.type == "EMPTY"]
assert len(floors) == 2, f"Expected 2 floors, got {len(floors)}"
assert all(obj.parent in floors for obj in meshes), "Lost floor hierarchy"
south = next(obj for obj in meshes if obj.get("elementId") == "1F-S")
def wall_hit(x, z):
    inverse = south.matrix_world.inverted()
    origin = inverse @ Vector((x, -1, z))
    direction = inverse.to_3x3() @ Vector((0, 1, 0))
    return south.ray_cast(origin, direction, distance=2)[0]
assert not wall_hit(2.2, 1.5), "Window hole was filled"
assert wall_hit(1.0, 1.5), "Solid wall surface was lost"
points = [obj.matrix_world @ Vector(corner) for obj in meshes for corner in obj.bound_box]
minimum = [min(point[i] for point in points) for i in range(3)]
maximum = [max(point[i] for point in points) for i in range(3)]
dimensions = [maximum[i] - minimum[i] for i in range(3)]
assert all(abs(a - b) < 0.00001 for a, b in zip(dimensions, [7.44, 5.64, 6.9])), dimensions
bpy.ops.export_scene.gltf(filepath=str(output), export_format="GLB", export_extras=True)
print("BLENDER_GLB_OK " + json.dumps({"elements": len(meshes), "storeys": len(floors),
    "dimensionsMetres": dimensions, "roundtrip": str(output)}))
