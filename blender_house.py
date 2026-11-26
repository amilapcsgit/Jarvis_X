"""Build an editable one-door, one-window house without touching existing objects."""
from pathlib import Path
import bpy
from mathutils import Quaternion, Vector

collection = bpy.data.collections.new("Jarvis House")
bpy.context.scene.collection.children.link(collection)

def material(name, color):
    value = bpy.data.materials.new(name)
    value.diffuse_color = (*color, 1.0)
    value.use_nodes = True
    shader = value.node_tree.nodes.get("Principled BSDF")
    shader.inputs["Base Color"].default_value = value.diffuse_color
    shader.inputs["Roughness"].default_value = 0.48
    return value

plaster = material("Warm plaster", (0.79, 0.61, 0.35))
timber = material("Slate timber", (0.12, 0.23, 0.31))
glass = material("Window glass", (0.16, 0.56, 0.68))

def mesh(name, vertices, faces, finish, position=(0, 0, 0)):
    data = bpy.data.meshes.new(name)
    data.from_pydata(vertices, [], faces)
    data.update()
    obj = bpy.data.objects.new(name, data)
    collection.objects.link(obj)
    obj.location = position
    obj.data.materials.append(finish)
    return obj

def block(name, x, y, z, width, depth, height, finish):
    vertices = [(a * width / 2, b * depth / 2, c * height / 2)
                for a, b, c in ((-1,-1,-1),(1,-1,-1),(1,1,-1),(-1,1,-1),
                                (-1,-1,1),(1,-1,1),(1,1,1),(-1,1,1))]
    return mesh(name, vertices, [(3,2,1,0),(4,5,6,7),(0,1,5,4),(1,2,6,5),(2,3,7,6),(3,0,4,7)],
                finish, (x, y, z))

house_spec = globals().get('house_spec')
if house_spec is None:
    # Offset the house in populated scenes, preserving their existing objects.
    offset = max([0.0] + [obj.location.x + obj.dimensions.x / 2 for obj in bpy.context.scene.objects
                          if obj.type == "MESH"])
    offset = offset + 4.0 if offset else 0.0
    block("Foundation", 0, 0, -0.12, 6.4, 4.5, 0.24, timber)
    block("Back wall", 0, 2, 1.4, 6, 0.2, 2.8, plaster)
    block("Left wall", -2.9, 0, 1.4, 0.2, 4, 2.8, plaster)
    block("Right wall", 2.9, 0, 1.4, 0.2, 4, 2.8, plaster)
    block("Front wall left pier", -2.2, -2, 1.4, 1.6, 0.2, 2.8, plaster)
    block("Front wall middle pier", 0, -2, 1.4, 0.6, 0.2, 2.8, plaster)
    block("Front wall right pier", 2.4, -2, 1.4, 1.2, 0.2, 2.8, plaster)
    block("Door lintel", -0.85, -2, 2.48, 1.1, 0.2, 0.64, plaster)
    block("Window sill wall", 1.05, -2, 0.53, 1.5, 0.2, 1.06, plaster)
    block("Window lintel", 1.05, -2, 2.49, 1.5, 0.2, 0.62, plaster)
    block("Door", -0.85, -2.08, 1.08, 1.02, 0.12, 2.12, timber)
    block("Door handle", -0.52, -2.18, 1.03, 0.15, 0.08, 0.05, plaster)
    block("Window frame", 1.05, -2.085, 1.62, 1.44, 0.14, 1.06, timber)
    block("Window pane", 1.05, -2.165, 1.62, 1.26, 0.035, 0.88, glass)
    block("Window mullion", 1.05, -2.191, 1.62, 0.045, 0.03, 0.88, timber)
    mesh("Gabled roof", [(-3.3,-2.3,2.78),(3.3,-2.3,2.78),(0,-2.3,4.25),
                                (-3.3,2.3,2.78),(3.3,2.3,2.78),(0,2.3,4.25)],
                [(0,1,2),(5,4,3),(0,3,4,1),(0,2,5,3),(1,4,5,2)], timber)

    for obj in collection.objects:
        obj.location.x += offset
else:
    width, depth, floors, glazing, roof, palette = house_spec
    height = 3.1
    offset = max([0.0] + [o.location.x + o.dimensions.x / 2 for o in bpy.context.scene.objects if o.type == "MESH"])
    offset += width / 2 + 1 if offset else 0

    def texture(finish, light, dark, scale):
        tree = finish.node_tree
        noise = tree.nodes.new("ShaderNodeTexNoise")
        noise.inputs["Scale"].default_value = scale
        ramp = tree.nodes.new("ShaderNodeValToRGB")
        ramp.color_ramp.elements[0].color = (*dark, 1)
        ramp.color_ramp.elements[1].color = (*light, 1)
        tree.links.new(noise.outputs["Fac"], ramp.inputs["Fac"])
        tree.links.new(ramp.outputs["Color"], tree.nodes.get("Principled BSDF").inputs["Base Color"])

    wood = (.42,.22,.10) if palette == "warm" else (.25,.19,.15)
    texture(timber, wood, tuple(v * .48 for v in wood), 17)
    texture(plaster, (.88,.81,.68), (.66,.61,.52), 36)
    roof_mat = material("Charcoal roofing", (.055,.075,.09))
    texture(roof_mat, (.10,.13,.16), (.04,.05,.06), 44)
    glass.node_tree.nodes.get("Principled BSDF").inputs["Transmission Weight"].default_value = .55
    glass.node_tree.nodes.get("Principled BSDF").inputs["Roughness"].default_value = .16
    front = -depth / 2
    for level in range(floors):
        base = level * height
        title = f"Floor {level + 1}"
        block(title + " slab", 0, 0, base - .1, width + .22, depth + .22, .2, roof_mat)
        for x in (-width / 2, width / 2):
            block(title + " side wall", x, 0, base + height / 2, .22, depth, height, plaster)
        block(title + " rear wall", 0, -front, base + height / 2, width, .22, height, plaster)
        span = width / glazing
        for bay in range(glazing):
            x = -width / 2 + (bay + .5) * span
            door = level == 0 and bay == 0
            label = title + (" Entrance door" if door else f" French window {bay + 1}")
            opening = min(1.35, span - .32) if door else span - .36
            glass_height = 2.45 if door else 2.8
            pier = (span - opening) / 2
            for side in (-1, 1):
                block(label + " pier", x + side * (opening + pier) / 2, front, base + height / 2, pier, .22, height, plaster)
                block(label + " jamb", x + side * (opening / 2 - .035), front - .09, base + glass_height / 2, .07, .16, glass_height, timber)
            block(label + " header", x, front, base + (height + glass_height) / 2, opening, .22, height - glass_height, plaster)
            block(label + " glazing", x, front, base + glass_height / 2, opening - .12, .045, glass_height - .07, glass)
            block(label + " mullion", x, front - .09, base + glass_height / 2, .05, .12, glass_height, timber)
            for z in (base + .035, base + glass_height - .035):
                block(label + " rail", x, front - .09, z, opening, .12, .07, timber)
            if door:
                block("Entrance pull handle", x + opening * .29, front - .18, base + 1.1, .045, .09, .3, roof_mat)
    top = floors * height
    rw, rd = width / 2 + .7, depth / 2 + .7
    block("Deep roof eaves", 0, 0, top, 2 * rw, 2 * rd, .2, roof_mat)
    if roof != "flat":
        ridge = min(rw, rd) * .45 if roof == "hipped" else rd
        mesh("Pitched roof", [(-rw,-rd,top),(rw,-rd,top),(rw,rd,top),(-rw,rd,top),
                              (0,-ridge,top+1.5),(0,ridge,top+1.5)],
             [(3,2,1,0),(0,1,4),(1,2,5,4),(2,3,5),(3,0,4,5)], roof_mat)
    for obj in collection.objects:
        obj.location.x += offset

def point_at(obj, target):
    obj.rotation_euler = (Vector(target) - obj.location).to_track_quat("-Z", "Y").to_euler()

scale = max(1, house_spec[0] / 6, house_spec[1] / 4, house_spec[2] * .93) if house_spec else 1
focus = (offset, 0, 1.75 * scale)
camera_data = bpy.data.cameras.new("House camera")
camera = bpy.data.objects.new("House camera", camera_data)
collection.objects.link(camera)
camera.location = (offset + 7.6 * scale, -10.5 * scale, 6.3 * scale)
camera_data.lens = 41
point_at(camera, focus)
bpy.context.scene.camera = camera

for name, position, energy, size in (
    ("House key", (offset - 3, -5, 8), 1500, 6),
    ("House fill", (offset + 5, -1, 5), 600, 5),
):
    data = bpy.data.lights.new(name, "AREA")
    data.energy = energy
    data.shape = "DISK"
    data.size = size
    light = bpy.data.objects.new(name, data)
    collection.objects.link(light)
    light.location = position
    point_at(light, focus)

for screen in bpy.data.screens:
    for area in screen.areas:
        if area.type == "VIEW_3D":
            view = area.spaces.active
            view.shading.color_type = "MATERIAL"
            view.overlay.show_overlays = False
            view.region_3d.view_location = focus
            view.region_3d.view_distance = 11 * scale
            view.region_3d.view_rotation = Quaternion((0.841, 0.544, 0, 0))
if Path(destination).exists():
    raise FileExistsError(f"A file already exists at {destination}")
bpy.ops.wm.save_as_mainfile(filepath=destination, check_existing=False)
print(f"Saved editable house with {sum(obj.type == 'MESH' for obj in collection.objects)} meshes to {destination}")
