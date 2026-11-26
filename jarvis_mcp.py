"""Local Codex diagnostics client for the Jarvis Windows app."""
import json
import struct

from mcp.server.fastmcp import FastMCP
from mcp.types import ToolAnnotations

mcp = FastMCP("Jarvis_X", instructions="Inspect Jarvis run traces and run fixed health checks. Blender submissions require the session queue to be enabled in the desktop app.")
readonly = ToolAnnotations(readOnlyHint=True)
pipe_name = r"\\.\pipe\Jarvis_X.Diagnostics.v1"


def read_exact(pipe, size):
    data = bytearray()
    while len(data) < size:
        block = pipe.read(size - len(data))
        if not block:
            raise RuntimeError("Jarvis disconnected.")
        data.extend(block)
    return data


def call(method, **args):
    data = json.dumps({"method": method, **args}).encode("utf-8")
    try:
        with open(pipe_name, "r+b", buffering=0) as pipe:
            packet = struct.pack("<I", len(data)) + data
            while packet:
                written = pipe.write(packet)
                if not written:
                    raise RuntimeError("Could not send request to Jarvis.")
                packet = packet[written:]
            size, = struct.unpack("<I", read_exact(pipe, 4))
            if not 0 < size <= 200_000:
                raise RuntimeError("Invalid diagnostics response size.")
            return json.loads(read_exact(pipe, size))
    except OSError:
        return {"error": "Open the staged Jarvis app in the same Windows account and try again."}


@mcp.tool(annotations=readonly)
def get_status() -> dict:
    """Read Jarvis readiness, mode, model, grants and queue status."""
    return call("get_status")


@mcp.tool(annotations=readonly)
def get_run(run_id: str) -> dict:
    """Read bounded, redacted events for a known run ID."""
    return call("get_run", run_id=run_id)


@mcp.tool(annotations=readonly)
def get_recent_errors() -> dict:
    """Read recent redacted failures and their run IDs."""
    return call("get_recent_errors")


@mcp.tool()
def run_health_check() -> dict:
    """Check Blender, standalone bpy, OpenCode and the selected local model with fixed probes."""
    return call("run_health_check")


@mcp.tool()
def submit_blender_task(prompt: str, reference: str = "") -> dict:
    """Queue a Blender brief with an optional project-relative image when the Jarvis queue is enabled."""
    return call("submit_blender_task", prompt=prompt, reference=reference)


@mcp.tool(annotations=readonly)
def get_task(task_id: str) -> dict:
    """Read the progress and verified output path of a submitted task."""
    return call("get_task", task_id=task_id)


if __name__ == "__main__":
    mcp.run()
