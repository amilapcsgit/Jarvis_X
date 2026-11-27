"""Run a bounded tool-free modeling conversation against a local inference server."""
import asyncio
import base64
import json
import re
import urllib.request


def run(request, inspect, edit, capture, save):
    def emit(kind, value):
        print(json.dumps({"kind": kind, "value": value}), flush=True)

    def ask(prompt, images):
        message = {"role": "user", "content": prompt}
        if images:
            message["images"] = images
        body = {"model": request["model"], "stream": False, "messages": [message]}
        url = "http://127.0.0.1:11434/api/chat"
        if request["inkling"]:
            url = "http://127.0.0.1:8080/v1/chat/completions"
            message.pop("images", None)
            message["content"] = [{"type": "text", "text": prompt}] + [
                {"type": "image_url", "image_url": {"url": "data:image/png;base64," + data}} for data in images]
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        call = urllib.request.Request(url, json.dumps(body).encode(), {"Content-Type": "application/json"})
        with opener.open(call, timeout=180) as response:
            answer = json.load(response)
        return (answer["choices"][0]["message"] if request["inkling"] else answer["message"])["content"].strip()

    images = []
    if request["reference"] and request["vision"]:
        with open(request["reference"], "rb") as source:
            images.append(base64.b64encode(source.read()).decode())
    initial = inspect()
    brief = request["brief"]
    context = f"Brief: {brief}\nPrevious exchange: {request['history']}\nScene: {initial}"
    instructions = ("Create or refine editable geometry in visible Blender. Preserve unrelated objects. "
                    "Return exactly one fenced python block, or QUESTION followed by a necessary design question. "
                    "Use stable object names. Prefer bpy.data.meshes.new and mesh.from_pydata for custom geometry. For simple primitives use primitive_cube_add(size=1) or primitive_cone_add(vertices=16, radius1=1, radius2=0, depth=2), then read bpy.context.object and set its dimensions. Jarvis handles saves and captures. Never save or clear the scene.\n")
    repaired = False
    for attempt in range(3):
        answer = ask(instructions + context, images)
        if answer.startswith("QUESTION ") or (answer.endswith("?") and "```" not in answer):
            emit("question", answer.removeprefix("QUESTION "))
            return
        blocks = re.findall(r"```(?:python|py)[ \t]*\r?\n(.+?)\r?\n```", answer, re.S | re.I)
        if len(blocks) != 1 or answer.count("```") != 2:
            raise ValueError("Expected one fenced Python script or a design question. No code dispatched.")
        script = blocks[0]
        emit("script", script)
        before = inspect()
        try:
            edit(script)
        except Exception as error:
            current = inspect()
            if any(word in str(error).lower() for word in ("timeout", "timed out", "connection", "no response")):
                raise RuntimeError(f"Uncertain edit outcome. Scene inspected. {error}") from error
            if repaired:
                raise
            repaired = True
            context += f"\nFailed script: {script}\nRepair this error once: {error}\nCurrent scene: {current}"
            emit("repair", str(error))
            continue
        emit("edited", "Blender executed the script.")
        after = inspect()
        preview = asyncio.run(capture())
        for item in getattr(preview, "content", preview if isinstance(preview, list) else [preview]):
            data = getattr(item, "data", None)
            if data:
                encoded = base64.b64encode(data).decode() if isinstance(data, bytes) else data
                emit("image", "data:image/png;base64," + encoded)
                if request["vision"]:
                    images = images[:int(bool(request["reference"]))] + [encoded]
                break
        else:
            raise RuntimeError("Blender returned no viewport image.")
        verdict = ask(f"{context}\nBefore: {before}\nAfter: {after}\nCompare the requested result with the evidence. "
                      "Reply DONE followed by the verified changes, or REFINE followed by the missing change.", images)
        if verdict.startswith("DONE") and initial != after:
            result = save("Jarvis Model")
            emit("done", verdict + "\n" + result["output"])
            return
        context += f"\nCurrent scene: {after}\nRequired refinement: {verdict}"
    raise RuntimeError("Could not verify the requested change within the edit limit.")
