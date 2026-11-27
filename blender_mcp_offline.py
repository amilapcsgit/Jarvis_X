"""Start the pinned MCP server with an explicit loopback-only network policy."""
import ipaddress
import logging
import os
import sys
import json
from pathlib import Path

def require_local(host):
    if isinstance(host, bytes):
        host = host.decode("ascii")
    if host == "localhost":
        return
    try:
        if ipaddress.ip_address(host).is_loopback:
            return
    except ValueError:
        pass
    raise PermissionError("Jarvis Blender MCP permits loopback networking only.")

def audit(event, args):
    if event in {"socket.getaddrinfo", "socket.gethostbyname", "socket.gethostbyname_ex", "socket.gethostbyaddr"}:
        require_local(args[0])
    elif event in {"socket.connect", "socket.sendto"}:
        address = args[1]
        if not isinstance(address, tuple):
            raise PermissionError("Unsupported Jarvis Blender MCP socket address.")
        require_local(address[0])

sys.addaudithook(audit)
os.environ["DISABLE_TELEMETRY"] = "true"
os.environ["BLENDER_HOST"] = "127.0.0.1"
os.environ["BLENDER_PORT"] = "19876"
logging.disable(logging.CRITICAL)

if "--verify-guard" in sys.argv:
    import socket
    for target in [("192.0.2.1", 443), ("example.invalid", 443)]:
        try:
            socket.create_connection(target, timeout=1)
        except PermissionError:
            continue
        raise RuntimeError("External destination was not blocked.")
    assert socket.getaddrinfo("127.0.0.1", 19876)
    print("PASS: external DNS and connections denied; loopback allowed")
elif len(sys.argv) > 1 and sys.argv[1] != "--fallback":
    from blender_mcp.server import main
    main()
else:
    from mcp.server.fastmcp import FastMCP
    from blender_mcp import server

    bridge = FastMCP("Jarvis Blender")

    scope_path = Path(os.environ["JARVIS_RUN_SCOPE"])

    def active_scope():
        try:
            scope = json.loads(scope_path.read_text(encoding="utf-8"))
        except (OSError, ValueError) as error:
            raise RuntimeError("Blender run was stopped.") from error
        if scope.get("Agent") != "blender":
            raise RuntimeError("Start a Blender run before calling this tool.")
        return scope

    checkpointed = None

    uncertain_run = None

    def dispatch(scope, code, observation=False):
        global uncertain_run
        if not observation and uncertain_run == scope["Id"]:
            raise RuntimeError("The last edit may have run. Inspect the scene or capture a preview before another edit.")
        if active_scope()["Id"] != scope["Id"]:
            raise RuntimeError("Blender run expired before dispatch.")
        try:
            return server.get_blender_connection().send_command("execute_code", {"code": code})
        except Exception as error:
            detail = str(error).lower()
            if isinstance(error, (TimeoutError, ConnectionError)) or any(
                phrase in detail for phrase in ("timed out", "timeout", "connection closed", "no response")
            ):
                uncertain_run = scope["Id"]
            raise

    def checkpoint(scope):
        global checkpointed
        if checkpointed == scope["Id"]:
            return
        output = scope_path.parent / "checkpoints" / scope["Id"] / "before.blend"
        output.parent.mkdir(parents=True, exist_ok=True)
        dispatch(scope, f"import bpy\n"
                        f"if bpy.data.filepath or bpy.context.scene.objects:\n"
                        f"    bpy.ops.wm.save_as_mainfile(filepath={str(output)!r}, copy=True, relative_remap=False)\n")
        checkpointed = scope["Id"]

    def record_output(scope, output):
        if active_scope()["Id"] != scope["Id"]:
            return
        receipt = scope_path.parent / "verified-output.json"
        temporary = receipt.with_suffix(".tmp")
        temporary.write_text(json.dumps({"run": scope["Id"], "output": str(output)}), encoding="utf-8")
        temporary.replace(receipt)

    @bridge.tool()
    def save_project(name: str) -> dict:
        """Save and verify an editable Blend file. Supply a simple filename stem."""
        import re
        scope = active_scope()
        if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9 _-]{0,70}", name) or name.upper() in {
            "CON", "PRN", "AUX", "NUL", *(f"COM{i}" for i in range(1,10)), *(f"LPT{i}" for i in range(1,10))
        }:
            raise ValueError("Use a simple name of 1–71 letters, digits, spaces, underscores or hyphens.")
        project = Path(scope["Project"]).resolve(strict=True)
        for index in range(1, 1000):
            output = project / (f"{name}.blend" if index == 1 else f"{name} {index}.blend")
            if not output.exists():
                break
        else:
            raise RuntimeError("No unused output name is available.")
        dispatch(scope, f"import bpy\nfrom pathlib import Path\noutput = Path({str(output)!r})\n"
                        "if output.exists(): raise FileExistsError(str(output))\n"
                        "if 'FINISHED' not in bpy.ops.wm.save_as_mainfile(filepath=str(output), check_existing=False): "
                        "raise RuntimeError('Save did not complete')\n")
        if not output.is_file() or output.stat().st_size == 0:
            raise RuntimeError("Blender did not produce a verified output file.")
        record_output(scope, output)
        return {"output": str(output), "verified": True, "bytes": output.stat().st_size}

    from typing import Literal
    from pydantic import BaseModel, Field

    class Part(BaseModel):
        name: str = Field(min_length=1, max_length=60)
        shape: Literal["box", "wedge", "cylinder", "mesh"] = "box"
        location: tuple[float, float, float] = (0, 0, 0)
        size: tuple[float, float, float] = (1, 1, 1)
        rotation: tuple[float, float, float] = (0, 0, 0)
        color: tuple[float, float, float] = (.55, .6, .65)
        vertices: list[tuple[float, float, float]] = Field(default_factory=list, max_length=300)
        faces: list[list[int]] = Field(default_factory=list, max_length=300)

    @bridge.tool()
    def put_parts(collection: str, parts: list[Part]) -> dict:
        """Create or update editable parts by name. Shape box/wedge/cylinder/mesh, location/size xyz,
        rotation xyz degrees, color RGB 0–1. For mesh supply local vertices and polygon faces."""
        import math
        scope = active_scope()
        if not collection.strip() or len(collection) > 60 or not 1 <= len(parts) <= 24:
            raise ValueError("Use a named collection and supply 1–24 parts per call.")
        if len({part.name for part in parts}) != len(parts):
            raise ValueError("Part names must be unique within a call.")
        for part in parts:
            if not all(math.isfinite(value) and abs(value) <= 10000
                       for row in (part.location, part.size, part.rotation, *part.vertices) for value in row):
                raise ValueError("Geometry values must be finite and in range.")
            if not all(0 < value <= 1000 for value in part.size) or not all(0 <= value <= 1 for value in part.color):
                raise ValueError("Part dimensions and colors are out of range.")
            if part.shape == "mesh" and (len(part.vertices) < 3 or not part.faces or any(
                not 3 <= len(face) <= 24 or len(set(face)) != len(face) or
                any(index < 0 or index >= len(part.vertices) for index in face) for face in part.faces)):
                raise ValueError("Mesh faces must refer to distinct valid vertex indices.")
        script = Path(__file__).with_name("blender_parts.py").read_text(encoding="utf-8")
        values = [part.model_dump() for part in parts]
        checkpoint(scope)
        return dispatch(scope, f"collection_name = {collection!r}\nparts = {values!r}\n" + script)

    invalid_house = {}

    @bridge.tool()
    def build_house(width: float = 6, depth: float = 4, floors: int = 1, glazing_bays: int = 3, roof: str = "gabled", palette: str = "warm", style: str = "simple") -> dict:
        """Build and save an editable house with one entrance. Use style modern for French windows, floors 1–3, width 5–24 and depth 4–20 meters, glazing_bays 2–5, roof gabled/hipped/flat and palette warm/charcoal. Style simple retains the original one-window preset when called with defaults."""
        scope = active_scope()
        run_id = scope["Id"]
        previous = invalid_house.get(run_id, 0)
        if previous >= 2:
            raise ValueError("House validation exhausted its one repair attempt. Start a new prompt.")
        if not (5 <= width <= 24 and 4 <= depth <= 20 and 1 <= floors <= 3
                and 2 <= glazing_bays <= 5 and width / glazing_bays >= 1.1
                and roof in {"gabled", "hipped", "flat"} and palette in {"warm", "charcoal"}
                and style in {"simple", "modern"}):
            invalid_house.clear()
            invalid_house[run_id] = previous + 1
            raise ValueError("Invalid house specification. Use width 5–24, depth 4–20, floors 1–3, 2–5 bays at least 1.1 m wide, and a supported roof, palette and style.")
        spec = (width, depth, floors, glazing_bays, roof, palette)
        if style == "simple" and spec == (6, 4, 1, 3, "gabled", "warm"):
            spec = None
        project = Path(scope["Project"]).resolve(strict=True)
        destination = next((project / ("Jarvis House.blend" if n == 1 else f"Jarvis House {n}.blend")
                            for n in range(1, 1000)
                            if not (project / ("Jarvis House.blend" if n == 1 else f"Jarvis House {n}.blend")).exists()), None)
        if destination is None:
            raise RuntimeError("No unused house filename is available in this project.")
        script = Path(__file__).with_name("blender_house.py").read_text(encoding="utf-8")
        code = f"destination = {str(destination)!r}\nhouse_spec = {spec!r}\n"
        code += """import bpy
scene = bpy.context.scene
original_camera = scene.camera
groups = ("objects", "collections", "meshes", "materials", "cameras", "lights")
before = {group: {item.as_pointer() for item in getattr(bpy.data, group)} for group in groups}
try:
"""
        code += "\n".join("    " + line for line in script.splitlines())
        code += """
except Exception as error:
    scene.camera = original_camera
    for group in groups:
        items = getattr(bpy.data, group)
        for item in list(items):
            if item.as_pointer() not in before[group]:
                items.remove(item, do_unlink=True)
    raise RuntimeError(f"House build failed before save completed: {error}") from error
"""
        if active_scope()["Id"] != scope["Id"]:
            raise RuntimeError("The house request belongs to an expired run.")
        checkpoint(scope)
        result = dispatch(scope, code)
        if destination.is_file() and destination.stat().st_size:
            record_output(scope, destination)
        return result

    @bridge.tool()
    def get_scene_info() -> dict:
        """List Blender objects, dimensions and materials."""
        global inspect_before_edit, uncertain_run
        code = """import bpy, json
rows = []
for ob in list(bpy.context.scene.objects)[:40]:
    row = {"name": ob.name, "type": ob.type, "location": list(ob.location), "rotation": list(ob.rotation_euler), "size": [round(v, 3) for v in ob.dimensions]}
    if ob.type == "MESH":
        row.update(vertices=len(ob.data.vertices), materials=[m.name for m in ob.data.materials if m][:8])
    rows.append(row)
print(json.dumps({"version": bpy.app.version_string, "scene": bpy.context.scene.name, "count": len(bpy.context.scene.objects), "objects": rows}))
"""
        result = dispatch(active_scope(), code, observation=True)
        inspect_before_edit = False
        uncertain_run = None
        return result

    @bridge.tool()
    def get_addon_status() -> dict:
        """Check the running Blender add-on version and capabilities."""
        active_scope()
        return server.get_blender_connection().send_command("get_addon_info", read_only=True)

    python_run = None
    python_failures = 0
    inspect_before_edit = False

    @bridge.tool()
    def execute_blender_code(code: str) -> dict:
        """Execute Blender Python for edits beyond put_parts. Use short scripts."""
        global python_run, python_failures, inspect_before_edit
        scope = active_scope()
        if python_run != scope["Id"]:
            python_run = scope["Id"]
            python_failures = 0
            inspect_before_edit = False
        if inspect_before_edit:
            raise RuntimeError("The last edit may have run. Inspect scene info or take a preview before retrying.")
        if python_failures >= 2:
            raise RuntimeError("Python repair attempt exhausted for this run. Use put_parts for geometry or report the blocker.")
        try:
            compile(code, "<Jarvis Blender Python>", "exec")
            checkpoint(scope)
            result = dispatch(scope, code)
        except Exception as exc:
            python_failures += 1
            detail = str(exc).lower()
            inspect_before_edit = isinstance(exc, (TimeoutError, ConnectionError)) or any(
                phrase in detail for phrase in ("timed out", "timeout", "connection closed", "no response"))
            raise
        python_failures = 0
        return result

    @bridge.tool()
    async def look(mode: str = "viewport", shading: str = "solid", max_size: int = 512):
        """Preview the scene. Mode: viewport (default), camera, angles. Shading: solid (default), material, rendered, wireframe, xray."""
        global inspect_before_edit, uncertain_run
        mode = mode.strip().lower()
        shading = shading.strip().lower()
        if mode in {"render", "rendered", "solid", "material", "wireframe", "xray"}:
            mode = "viewport"
        if mode not in {"viewport", "camera", "angles"} or shading not in {"solid", "material", "rendered", "wireframe", "xray"}:
            raise ValueError("Choose viewport, camera or angles and a supported shading mode.")
        active_scope()
        result = await server.look(None, mode=mode, shading=shading, max_size=max(200, min(768, max_size)))
        inspect_before_edit = False
        uncertain_run = None
        return result

    if "--fallback" in sys.argv:
        from fallback_model import run
        request = json.loads(Path(sys.argv[-1]).read_text(encoding="utf-8-sig"))
        if active_scope()["Id"] != request["run"]:
            raise RuntimeError("Expired Blender run.")
        run(request, get_scene_info, execute_blender_code, look, save_project)
    else:
        bridge.run()
