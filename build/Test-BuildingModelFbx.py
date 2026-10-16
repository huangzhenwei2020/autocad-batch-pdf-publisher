"""Independent FBX import check in a fresh, background Blender process."""
import json
import sys
import collections
import math
from pathlib import Path

import bpy
from mathutils import Vector
from mathutils.bvhtree import BVHTree
from io_scene_fbx.import_fbx import validate_blend_names


def triangle_distance(point, a, b, c):
    # Double-precision fallback: BVH float closest-point queries can be inaccurate
    # on tall, narrow wall triangles even when both surfaces are identical.
    sub = lambda u,v: tuple(u[i]-v[i] for i in range(3))
    dot = lambda u,v: sum(u[i]*v[i] for i in range(3))
    ab,ac,ap = sub(b,a),sub(c,a),sub(point,a)
    d1,d2=dot(ab,ap),dot(ac,ap)
    if d1<=0 and d2<=0: return math.sqrt(dot(ap,ap))
    bp=sub(point,b); d3,d4=dot(ab,bp),dot(ac,bp)
    if d3>=0 and d4<=d3: return math.sqrt(dot(bp,bp))
    vc=d1*d4-d3*d2
    if vc<=0 and d1>=0 and d3<=0:
        t=d1/(d1-d3); q=tuple(a[i]+t*ab[i] for i in range(3)); delta=sub(point,q)
        return math.sqrt(dot(delta,delta))
    cp=sub(point,c); d5,d6=dot(ab,cp),dot(ac,cp)
    if d6>=0 and d5<=d6: return math.sqrt(dot(cp,cp))
    vb=d5*d2-d1*d6
    if vb<=0 and d2>=0 and d6<=0:
        t=d2/(d2-d6); q=tuple(a[i]+t*ac[i] for i in range(3)); delta=sub(point,q)
        return math.sqrt(dot(delta,delta))
    va=d3*d6-d5*d4
    if va<=0 and d4-d3>=0 and d5-d6>=0:
        t=(d4-d3)/((d4-d3)+(d5-d6)); q=tuple(b[i]+t*(c[i]-b[i]) for i in range(3)); delta=sub(point,q)
        return math.sqrt(dot(delta,delta))
    denominator=va+vb+vc
    if denominator==0: return min(math.dist(point,p) for p in (a,b,c))
    v,w=vb/denominator,vc/denominator
    q=tuple(a[i]+v*ab[i]+w*ac[i] for i in range(3)); delta=sub(point,q)
    return math.sqrt(dot(delta,delta))

arguments = sys.argv[sys.argv.index("--") + 1:]
source = Path(arguments[0]).resolve()
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=str(source), use_anim=False)
meshes = [obj for obj in bpy.context.scene.objects if obj.type == "MESH"]
points = [obj.matrix_world @ Vector(corner) for obj in meshes for corner in obj.bound_box]
assert points, "No imported geometry"
minimum = [min(point[i] for point in points) for i in range(3)]
maximum = [max(point[i] for point in points) for i in range(3)]
dimensions = [(maximum[i] - minimum[i]) * 1000 for i in range(3)]
scene = json.loads(Path(arguments[1]).read_text(encoding="utf-8-sig")) if len(arguments) > 1 else None
expected_size = [7440, 5640, 6900]
if scene:
    world = [Vector([p[i] + instance["Translation"][i] for i in range(3)])
             for instance in scene["Instances"] for p in scene["Meshes"][instance["MeshIndex"]]["Vertices"]]
    expected_size = [max(p[i] for p in world) - min(p[i] for p in world) for i in range(3)]
assert all(abs(a - b) < 0.05 for a, b in zip(dimensions, expected_size)), dimensions
triangles = 0
area = 0
for obj in meshes:
    obj.data.calc_loop_triangles()
    triangles += len(obj.data.loop_triangles)
    for triangle in obj.data.loop_triangles:
        a, b, c = [obj.matrix_world @ obj.data.vertices[i].co for i in triangle.vertices]
        area += (b - a).cross(c - a).length * 0.5
if scene:
    assert len(meshes) == len(scene["Instances"]), "Element identities/count changed"
    matched = set()
    max_surface_distance = 0
    maximum_area_relative_error = 0
    opening_boundaries = 0
    for obj in meshes:
        matches = [i for i in scene["Instances"] if obj.name == validate_blend_names(
            ("WL_Element: " + i["Name"] + " [" + i["Id"] + "]").encode("utf-8"))]
        assert len(matches) == 1, obj.name
        instance = matches[0]; matched.add(instance["Id"])
        original = scene["Meshes"][instance["MeshIndex"]]
        expected_double = [tuple(p[i] + instance["Translation"][i] for i in range(3)) for p in original["Vertices"]]
        matrix = obj.matrix_world
        actual_double = [tuple((sum(float(matrix[i][k])*float(v.co[k]) for k in range(3))+float(matrix[i][3]))*1000
                               for i in range(3)) for v in obj.data.vertices]
        expected_vertices = [Vector(p) for p in expected_double]
        actual_vertices = [Vector(p) for p in actual_double]
        # A target importer must not have to guess how to triangulate a concave
        # bent-frame cap. Check the serialized polygons, not just rendered tris.
        def cross(u,v):
            return (u[1]*v[2]-u[2]*v[1],u[2]*v[0]-u[0]*v[2],u[0]*v[1]-u[1]*v[0])
        for polygon in obj.data.polygons:
            loop=[actual_double[i] for i in polygon.vertices]
            normal=[0.0,0.0,0.0]
            for i in range(1,len(loop)-1):
                u=[loop[i][k]-loop[0][k] for k in range(3)]
                v=[loop[i+1][k]-loop[0][k] for k in range(3)]
                c=cross(u,v)
                normal=[normal[k]+c[k] for k in range(3)]
            length=math.sqrt(sum(n*n for n in normal))
            assert length>0, "Zero-area polygon"
            normal=[n/length for n in normal]
            for i in range(len(loop)):
                u=[loop[i][k]-loop[i-1][k] for k in range(3)]
                v=[loop[(i+1)%len(loop)][k]-loop[i][k] for k in range(3)]
                turn=sum(a*b for a,b in zip(cross(u,v),normal))
                tolerance=max(1e-8,math.sqrt(sum(x*x for x in u)*sum(x*x for x in v))*1e-7)
                assert turn>=-tolerance, (obj.name,"Concave exported polygon",turn)
        assert len(set(tuple(v.co) for v in obj.data.vertices)) == len(obj.data.vertices), "Duplicate control points: " + obj.name
        referenced = {v for p in obj.data.polygons for v in p.vertices}
        assert len(referenced) == len(obj.data.vertices), "Unused transport vertices: " + obj.name
        edges = collections.Counter(tuple(sorted(edge)) for p in obj.data.polygons for edge in p.edge_keys)
        if "opening" in instance["Id"].lower():
            opening_boundaries += sum(n == 1 for n in edges.values())
            assert all(n == 2 for n in edges.values()), "An opening's solid faces are unwelded/nonmanifold: " + obj.name
        expected_parts = collections.defaultdict(list)
        actual_parts = collections.defaultdict(list)
        for t in original["Triangles"]:
            expected_parts[scene["Materials"][t["MaterialIndex"]]["Name"]].append((t["A"], t["B"], t["C"]))
        for t in obj.data.loop_triangles:
            name = obj.data.materials[t.material_index].name
            kind = name.split(": ", 1)[1].rsplit(" [", 1)[0]
            actual_parts[kind].append(tuple(t.vertices))
        assert expected_parts.keys() == actual_parts.keys(), "Material boundary lost"
        for kind, expected_faces in expected_parts.items():
            actual_faces = actual_parts[kind]
            def surface_area(vertices, faces):
                total=0
                for a,b,c in faces:
                    u=[vertices[b][i]-vertices[a][i] for i in range(3)]; v=[vertices[c][i]-vertices[a][i] for i in range(3)]
                    cross=[u[1]*v[2]-u[2]*v[1],u[2]*v[0]-u[0]*v[2],u[0]*v[1]-u[1]*v[0]]
                    total+=math.sqrt(sum(x*x for x in cross))/2
                return total
            expected_area = surface_area(expected_double, expected_faces)
            actual_area = surface_area(actual_double, actual_faces)
            relative = abs(expected_area - actual_area)/max(1,expected_area)
            maximum_area_relative_error = max(maximum_area_relative_error, relative)
            assert abs(expected_area - actual_area) < max(0.1,expected_area * 1e-6), (obj.name,kind,expected_area,actual_area)
            original_bvh = BVHTree.FromPolygons(expected_vertices, expected_faces, all_triangles=True)
            result_bvh = BVHTree.FromPolygons(actual_vertices, actual_faces, all_triangles=True)
            # Both directions catch caps across holes as well as missing regions.
            for vertices, faces, target in ((actual_vertices,actual_faces,original_bvh), (expected_vertices,expected_faces,result_bvh)):
                for a,b,c in faces:
                    for sample in ((vertices[a]+vertices[b]+vertices[c])/3,
                                   (vertices[a]+vertices[b])/2, (vertices[b]+vertices[c])/2, (vertices[c]+vertices[a])/2):
                        nearest = target.find_nearest(sample)
                        assert nearest is not None and nearest[0] is not None
                        distance=nearest[3]
                        if distance>=0.005:
                            target_vertices=expected_vertices if vertices is actual_vertices else actual_vertices
                            target_faces=expected_faces if vertices is actual_vertices else actual_faces
                            distance=triangle_distance(sample,*(target_vertices[index] for index in target_faces[nearest[2]]))
                            if distance>=0.02:
                                distance=min(triangle_distance(sample,*(target_vertices[index] for index in face)) for face in target_faces)
                        max_surface_distance = max(max_surface_distance,distance)
                        assert distance < 0.02, (obj.name,kind,distance,tuple(sample))
    assert len(matched)==len(scene["Instances"])
    print("FBX_SURFACE_TOPOLOGY_OK " + json.dumps({"elements":len(matched),"polygons":sum(len(o.data.polygons) for o in meshes),
        "maxSurfaceDistanceMm":max_surface_distance,"maxAreaRelativeError":maximum_area_relative_error,
        "openingBoundaryEdges":opening_boundaries,"convexCornerPartitions":True}))
else:
    assert triangles <= 1180, f"Unexpected new geometry: {triangles}"
    assert sum(len(obj.data.polygons) for obj in meshes) < triangles, "FBX still contains transport triangles only"
    assert abs(area - 555.579769173) < 0.001, area
print("BLENDER_FBX_IMPORT_OK " + json.dumps({"meshes": len(meshes), "triangles": triangles,
    "dimensionsMillimetres": dimensions, "surfaceSquareMetres": area}))
