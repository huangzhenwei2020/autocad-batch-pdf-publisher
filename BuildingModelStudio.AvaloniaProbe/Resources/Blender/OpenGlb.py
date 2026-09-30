"""Import the GLB supplied after -- into a new Blender session."""
import sys
import math
from pathlib import Path

import bpy
import bmesh


def clean_planar_mesh(obj):
    """Remove transport triangulation without crossing element/material boundaries."""
    mesh = obj.data
    bm = bmesh.new()
    try:
        bm.from_mesh(mesh)
        # GLB splits vertices at normal seams; reconnect identical positions first.
        bmesh.ops.remove_doubles(bm, verts=list(bm.verts), dist=1e-7)
        bm.normal_update()
        delimit = {"MATERIAL"}
        if bm.loops.layers.uv.active is not None:
            delimit.add("UV")
        bmesh.ops.dissolve_limit(
            bm, angle_limit=math.radians(0.05),
            use_dissolve_boundaries=False, verts=list(bm.verts),
            edges=list(bm.edges), delimit=delimit)
        bm.normal_update()
        bm.to_mesh(mesh)
        mesh.update()
    finally:
        bm.free()


def main():
    arguments = sys.argv[sys.argv.index("--") + 1:]
    if len(arguments) != 1:
        raise ValueError("Expected one GLB file path")
    model_path = Path(arguments[0]).resolve(strict=True)
    if model_path.suffix.lower() != ".glb":
        raise ValueError("Expected a GLB file")
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=str(model_path))
    for obj in bpy.context.scene.objects:
        if obj.type == "MESH":
            clean_planar_mesh(obj)
        obj.select_set(obj.type == "MESH")
    for window in bpy.context.window_manager.windows:
        for area in window.screen.areas:
            if area.type == "VIEW_3D":
                region = next((region for region in area.regions if region.type == "WINDOW"), None)
                if region:
                    with bpy.context.temp_override(window=window, area=area, region=region):
                        bpy.ops.view3d.view_all(center=False)
    print("WANLUO_BLENDER_OPEN_OK " + str(model_path))


if __name__ == "__main__":
    main()
