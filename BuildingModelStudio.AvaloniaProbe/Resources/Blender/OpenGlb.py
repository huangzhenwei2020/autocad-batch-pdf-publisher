"""Prepare GLB geometry off-screen, then open its scene in a solid viewport."""
import argparse
import datetime
import json
import sys
import math
import time
import traceback
from pathlib import Path

import bpy
import bmesh

_log_path = None


def report(stage, **details):
    record = {"utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
              "stage": stage, **details}
    line = json.dumps(record, ensure_ascii=False)
    if _log_path is not None:
        with _log_path.open("a", encoding="utf-8") as output:
            output.write(line + "\n")
    try:
        print("WANLUO_BLENDER_STAGE " + line, flush=True)
    except (OSError, UnicodeError):
        pass


def flag(name):
    if _log_path is not None:
        destination = _log_path.parent / name
        temporary = destination.with_suffix(".tmp")
        temporary.write_text("ok", encoding="ascii")
        temporary.replace(destination)


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


def prepare_model(model_path):
    if model_path.suffix.lower() != ".glb":
        raise ValueError("Expected a GLB file")
    bpy.ops.wm.read_factory_settings(use_empty=True)
    started = time.perf_counter()
    report("IMPORT_START", source=str(model_path), blender=bpy.app.version_string)
    bpy.ops.import_scene.gltf(filepath=str(model_path))
    meshes = [obj for obj in bpy.context.scene.objects if obj.type == "MESH"]
    report("IMPORT_DONE", seconds=time.perf_counter() - started,
           meshes=len(meshes), materials=len(bpy.data.materials),
           polygons=sum(len(obj.data.polygons) for obj in meshes))
    previous_undo = bpy.context.preferences.edit.use_global_undo
    bpy.context.preferences.edit.use_global_undo = False
    skipped = 0
    handled = set()
    started = time.perf_counter()
    try:
        for obj in meshes:
            identity = obj.data.as_pointer()
            if identity in handled:
                continue
            handled.add(identity)
            # Cleanup is optional transport detail; retain large meshes unchanged.
            if len(obj.data.polygons) > 250000 or time.perf_counter() - started > 5:
                skipped += 1
                continue
            clean_planar_mesh(obj)
    finally:
        bpy.context.preferences.edit.use_global_undo = previous_undo
    for obj in bpy.context.scene.objects:
        obj.select_set(False)
    bpy.context.view_layer.objects.active = None
    report("CLEAN_DONE", seconds=time.perf_counter() - started, skipped=skipped,
           polygons=sum(len(obj.data.polygons) for obj in meshes))


def configure_view():
    if _log_path is not None:
        scratch = _log_path.parent / "scratch"
        scratch.mkdir(exist_ok=True)
        bpy.context.preferences.filepaths.temporary_directory = str(scratch)
    bpy.context.scene.unit_settings.system = "METRIC"
    for screen in bpy.data.screens:
        for area in screen.areas:
            if area.type == "VIEW_3D":
                shading = area.spaces.active.shading
                shading.type = "SOLID"
                shading.color_type = "MATERIAL"
                shading.show_shadows = False
                shading.show_cavity = False
    for window in bpy.context.window_manager.windows:
        for area in window.screen.areas:
            if area.type == "VIEW_3D":
                region = next((region for region in area.regions if region.type == "WINDOW"), None)
                if region:
                    with bpy.context.temp_override(window=window, area=area, region=region):
                        bpy.ops.view3d.view_all(center=False)


def wait_for_first_frame(test_close):
    import gpu
    received = False
    handle = None

    def on_draw():
        nonlocal received
        if received:
            return
        received = True
        details = {}
        try:
            details = {"gpu": gpu.platform.renderer_get(), "vendor": gpu.platform.vendor_get()}
        except Exception as error:
            details["gpu_query_error"] = str(error)
        report("FIRST_FRAME", **details)
        flag("ready.flag")

        def finish():
            bpy.types.SpaceView3D.draw_handler_remove(handle, "WINDOW")
            if test_close:
                bpy.ops.wm.quit_blender()
            return None
        bpy.app.timers.register(finish, first_interval=0.2)

    handle = bpy.types.SpaceView3D.draw_handler_add(on_draw, (), "WINDOW", "POST_PIXEL")
    for window in bpy.context.window_manager.windows:
        for area in window.screen.areas:
            if area.type == "VIEW_3D":
                area.tag_redraw()


def main():
    global _log_path
    parser = argparse.ArgumentParser()
    modes = parser.add_mutually_exclusive_group()
    modes.add_argument("--prepare", nargs=2, metavar=("GLB", "BLEND"))
    modes.add_argument("--show", metavar="BLEND")
    parser.add_argument("--log")
    parser.add_argument("--test-close", action="store_true")
    parser.add_argument("model", nargs="?")
    args = parser.parse_args(sys.argv[sys.argv.index("--") + 1:])
    if args.log:
        _log_path = Path(args.log).resolve()
        _log_path.parent.mkdir(parents=True, exist_ok=True)
    try:
        if args.prepare:
            if not bpy.app.background:
                raise ValueError("Preparation must use a background process")
            source = Path(args.prepare[0]).resolve(strict=True)
            output = Path(args.prepare[1]).resolve()
            if output.suffix.lower() != ".blend" or output.exists():
                raise ValueError("Prepared scene must be a new .blend file")
            prepare_model(source)
            configure_view()
            report("SAVE_START", scene=str(output))
            bpy.ops.wm.save_as_mainfile(filepath=str(output), check_existing=False)
            flag("prepared.flag")
            report("PREPARED")
        elif args.show:
            if bpy.app.background:
                raise ValueError("Frontend must have a viewport")
            scene = Path(args.show).resolve(strict=True)
            report("FRONTEND_START", scene=str(scene))
            bpy.ops.wm.open_mainfile(filepath=str(scene), load_ui=True)
            configure_view()
            report("VIEWPORT_WAIT", shading="SOLID")
            wait_for_first_frame(args.test_close)
        elif args.model:
            prepare_model(Path(args.model).resolve(strict=True))
            configure_view()
            report("LEGACY_IMPORTED")
        else:
            raise ValueError("Expected --prepare or --show")
    except Exception as error:
        report("ERROR", error=str(error))
        traceback.print_exc()
        raise


if __name__ == "__main__":
    main()
