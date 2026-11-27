"""Apply validated editable parts to a Jarvis-owned collection in the active Blender scene."""
import math

import bpy

collection = next((item for item in bpy.context.scene.collection.children
                   if item.get("jarvis_parts") == collection_name), None)
if collection is None:
    collection = bpy.data.collections.new(collection_name)
    collection["jarvis_parts"] = collection_name
    bpy.context.scene.collection.children.link(collection)


def box():
    return ([(-.5,-.5,-.5),(.5,-.5,-.5),(.5,.5,-.5),(-.5,.5,-.5),
             (-.5,-.5,.5),(.5,-.5,.5),(.5,.5,.5),(-.5,.5,.5)],
            [(3,2,1,0),(4,5,6,7),(0,1,5,4),(1,2,6,5),(2,3,7,6),(3,0,4,7)])


def cylinder():
    sides = 12
    vertices = [(.5 * math.cos(math.tau * i / sides), .5 * math.sin(math.tau * i / sides), z)
                for z in (-.5, .5) for i in range(sides)]
    faces = [tuple(reversed(range(sides))), tuple(range(sides, sides * 2))]
    faces += [(i, (i+1)%sides, (i+1)%sides+sides, i+sides) for i in range(sides)]
    return vertices, faces


def wedge():
    return ([(-.5,-.5,-.5),(.5,-.5,-.5),(.5,.5,-.5),(-.5,.5,-.5),(-.5,-.5,.5),(-.5,.5,.5)],
            [(3,2,1,0),(0,1,4),(2,3,5),(0,4,5,3),(1,2,5,4)])


shapes = {"box": box, "cylinder": cylinder, "wedge": wedge}
for part in parts:
    old = next((item for item in collection.objects if item.get("jarvis_part") == part["name"]), None)
    if part["shape"] == "mesh":
        vertices, faces = part["vertices"], part["faces"]
    else:
        vertices, faces = shapes[part["shape"]]()
        vertices = [tuple(v * d for v, d in zip(vertex, part["size"])) for vertex in vertices]
    mesh = bpy.data.meshes.new(part["name"])
    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    material_name = "Jarvis " + "-".join(str(round(component * 255)) for component in part["color"])
    material = next((item for item in bpy.data.materials if item.get("jarvis_color") == material_name), None)
    if material is None:
        material = bpy.data.materials.new(material_name)
        material["jarvis_color"] = material_name
        material.diffuse_color = (*part["color"], 1.0)
        material.use_nodes = True
        material.node_tree.nodes.get("Principled BSDF").inputs["Base Color"].default_value = material.diffuse_color
    mesh.materials.append(material)
    if old is None:
        obj = bpy.data.objects.new(part["name"], mesh)
        obj["jarvis_part"] = part["name"]
        collection.objects.link(obj)
    else:
        old_mesh = old.data
        old.data = mesh
        if old_mesh and old_mesh.users == 0:
            bpy.data.meshes.remove(old_mesh)
        obj = old
    obj["jarvis_shape"] = part["shape"]
    obj.location = part["location"]
    obj.rotation_euler = tuple(math.radians(a) for a in part["rotation"])
    bevel = obj.modifiers.get("Jarvis Bevel")
    if part.get("bevel", 0) > 0:
        if bevel is None:
            bevel = obj.modifiers.new("Jarvis Bevel", "BEVEL")
        bevel.width = part["bevel"]
        bevel.segments = 3
    elif bevel is not None:
        obj.modifiers.remove(bevel)
print(f"Applied {len(parts)} editable parts in {collection.name}.")
