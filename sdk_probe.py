"""Verify import, mesh creation, save and reopen with the standalone Blender SDK."""
from pathlib import Path
from tempfile import TemporaryDirectory

import bpy

if bpy.app.version != (5, 2, 2):
    raise RuntimeError(f"Expected bpy 5.2.2, got {bpy.app.version_string}")
with TemporaryDirectory(prefix="jarvis-bpy-") as folder:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.mesh.primitive_cube_add()
    bpy.context.object.name = "Jarvis SDK Probe"
    path = Path(folder) / "probe.blend"
    bpy.ops.wm.save_as_mainfile(filepath=str(path))
    bpy.ops.wm.open_mainfile(filepath=str(path))
    assert len(bpy.data.objects["Jarvis SDK Probe"].data.vertices) == 8
    print(f"PASS: bpy {bpy.app.version_string} created, saved and reopened a mesh.")
