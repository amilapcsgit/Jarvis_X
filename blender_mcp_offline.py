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
elif len(sys.argv) > 1:
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
        return server.get_blender_connection().send_command("execute_code", {"code": code})

    @bridge.tool()
    def get_scene_info() -> dict:
        """List Blender objects and their types and locations."""
        active_scope()
        return server.get_blender_connection().send_command("get_scene_info", read_only=True)

    @bridge.tool()
    def get_addon_status() -> dict:
        """Check the running Blender add-on version and capabilities."""
        active_scope()
        return server.get_blender_connection().send_command("get_addon_info", read_only=True)

    @bridge.tool()
    def execute_blender_code(code: str) -> dict:
        """Run approved Blender Python and return its printed result. Use small scripts and inspect results between edits."""
        compile(code, "<Jarvis Blender Python>", "exec")
        active_scope()
        return server.get_blender_connection().send_command("execute_code", {"code": code})

    @bridge.tool()
    async def look(mode: str = "viewport", shading: str = "solid", max_size: int = 512):
        """Preview the scene. Mode: viewport (default), camera, angles. Shading: solid (default), material, rendered, wireframe, xray."""
        mode = mode.strip().lower()
        shading = shading.strip().lower()
        if mode in {"render", "rendered", "solid", "material", "wireframe", "xray"}:
            mode = "viewport"
        if mode not in {"viewport", "camera", "angles"} or shading not in {"solid", "material", "rendered", "wireframe", "xray"}:
            raise ValueError("Choose viewport, camera or angles and a supported shading mode.")
        active_scope()
        return await server.look(None, mode=mode, shading=shading, max_size=max(200, min(768, max_size)))

    bridge.run()
