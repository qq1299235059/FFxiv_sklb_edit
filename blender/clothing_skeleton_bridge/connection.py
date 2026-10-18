"""Loopback transport. Workers handle HTTP only; all Blender edits run in tick()."""
import base64
import hashlib
import json
from pathlib import Path
import queue
import struct
import threading
import uuid
from urllib import request, error

PROTOCOL = 1
DEFAULT_PORT = 43867
PROJECT_NAMES = ("skeleton.gltf", "skeleton.ffxiv.json", "skeleton.complete.json", "reference.gltf", "reference.ffxiv.json", "reference.complete.json", "anchor-rig.json", "skeleton.bin", "reference.bin")
RETURN_NAMES = ("edited.gltf", "edited.ffxiv.json", "edited.complete.json")
MAX_BYTES = 48 * 1024 * 1024


class Connection:
    def __init__(self):
        self.incoming = queue.Queue(maxsize=1)
        self.outgoing = queue.Queue(maxsize=4)
        self.thread = None
        self.stop_event = threading.Event()
        self.client_id = str(uuid.uuid4())
        self.token = None
        self.connected = False
        self.message = "等待连接游戏"
        self.port = DEFAULT_PORT
        self.cache = None
        self.last_transfer = None

    def start(self, port, cache):
        self.stop()
        if self.thread and self.thread.is_alive():
            raise ValueError("连接正在关闭，请稍后重试")
        self.port = int(port)
        if not 1 <= self.port <= 65535:
            raise ValueError("端口应在 1 到 65535 之间")
        self.cache = Path(cache)
        self.stop_event = threading.Event()
        self.token = None
        self.last_transfer = None
        self.message = "正在连接游戏"
        self.thread = threading.Thread(target=self._run, name="CSW loopback", daemon=True)
        self.thread.start()

    def stop(self):
        self.stop_event.set()
        if self.thread and self.thread.is_alive():
            self.thread.join(timeout=2.5)
        self.connected = False
        self.message = "已断开连接"
        for pending in (self.incoming, self.outgoing):
            while not pending.empty():
                try:
                    pending.get_nowait()
                except queue.Empty:
                    break

    def _post(self, path, value, authenticated=True):
        data = json.dumps(value, ensure_ascii=True, separators=(",", ":")).encode("utf-8")
        if len(data) > MAX_BYTES:
            raise ValueError("交换数据过大")
        headers = {"Content-Type": "application/json", "X-CSW-Protocol": str(PROTOCOL)}
        if authenticated:
            headers.update({"X-CSW-Client": self.client_id, "X-CSW-Token": self.token or ""})
        req = request.Request(f"http://127.0.0.1:{self.port}/v1/{path}", data=data, headers=headers)
        # Ignore system proxy settings: this connection must stay on loopback.
        with request.build_opener(request.ProxyHandler({})).open(req, timeout=1.5) as response:
            payload = response.read(MAX_BYTES + 1)
            if len(payload) > MAX_BYTES:
                raise ValueError("交换数据过大")
            return json.loads(payload)

    def _run(self):
        pending = None
        try:
            while not self.stop_event.is_set():
                try:
                    if not self.token:
                        hello = self._post("connect", {"protocol": PROTOCOL, "clientId": self.client_id}, False)
                        if hello.get("protocol") != PROTOCOL:
                            raise ValueError("请更新两侧为相同版本")
                        self.token = hello["token"]
                        self.connected = True
                        self.message = "已连接游戏，等待发送骨架"
                    if pending is None:
                        try:
                            pending = self.outgoing.get_nowait()
                        except queue.Empty:
                            pass
                    if pending is not None:
                        action, payload = pending
                        try:
                            self._post(action, payload)
                            if action == "return":
                                self.message = "已返回游戏，请在游戏回导并检查"
                            pending = None
                        except error.HTTPError as exc:
                            detail = json.loads(exc.read()).get("error", "返回失败")
                            if exc.code == 401:
                                raise
                            if exc.code == 409 and "正在回导" in detail:
                                self.message = "游戏正在回导，稍后自动重试"
                            else:
                                self.message = detail
                                pending = None
                    polled = self._post("poll", {})
                    project = polled.get("project")
                    if project and project["transferId"] != self.last_transfer and self.incoming.empty():
                        self.incoming.put_nowait(project)
                        self.last_transfer = project["transferId"]
                        self.message = "收到骨架，等待当前操作结束后导入"
                except error.HTTPError as exc:
                    self.connected = False
                    self.token = None
                    self.message = "游戏连接失效，正在重连" if exc.code == 401 else "连接被拒绝，请检查端口、插件版本或其他 Blender 连接"
                    self.stop_event.wait(1.5)
                except (OSError, ValueError, KeyError):
                    self.connected = False
                    self.token = None
                    self.message = "等待游戏插件，本机连接会自动重试"
                    self.stop_event.wait(1.5)
                self.stop_event.wait(0.4)
        finally:
            if self.token:
                try:
                    self._post("disconnect", {})
                except (OSError, ValueError):
                    pass
            self.connected = False
            self.token = None

    def enqueue_return(self, armature, target):
        if not self.connected:
            raise ValueError("尚未连接游戏，请先点击“连接游戏”")
        project_id = json.loads(armature["csw_baseline"])["projectId"]
        revision = armature.get("csw_network_revision")
        if not revision:
            raise ValueError("此骨架未通过连接接收，请在游戏发送当前项目")
        packet = {"projectId": project_id, "revision": revision, "requestId": str(uuid.uuid4()),
                  "files": {name: base64.b64encode((Path(target).parent / name).read_bytes()).decode("ascii") for name in RETURN_NAMES}}
        try:
            self.outgoing.put_nowait(("return", packet))
        except queue.Full:
            raise ValueError("仍有待发送内容，请稍后重试")
        self.message = "正在返回游戏"

    def tick(self, bpy, import_project):
        # Never interrupt edit/sculpt/pose/modal operations, or move to another .blend.
        if bpy.context.object and bpy.context.object.mode != "OBJECT":
            return 0.3
        window = bpy.context.window
        if window and any(op.bl_idname not in {"WM_OT_timer"} for op in getattr(window, "modal_operators", ())):
            return 0.3
        try:
            project = self.incoming.get_nowait()
        except queue.Empty:
            return 0.3
        try:
            project_id = str(uuid.UUID(project["projectId"]))
            revision = project["revision"]
            if len(revision) != 64 or any(c not in "0123456789abcdef" for c in revision):
                raise ValueError("无效骨架版本")
            existing = [obj for obj in bpy.context.scene.objects if obj.type == "ARMATURE" and
                        obj.get("csw_network_revision") == revision and "csw_baseline" in obj and
                        json.loads(obj["csw_baseline"])["projectId"] == project_id]
            if existing:
                if "csw_body_reference" not in existing[0]:
                    from . import anchors
                    anchors.capture_body_reference(existing[0], json.loads(base64.b64decode(project["files"]["reference.ffxiv.json"])))
                bpy.context.scene["csw_active_project_id"] = project_id
                self.message = "当前工程已有此骨架，已保留现有编辑"
            else:
                files = project["files"]
                if set(files) - set(PROJECT_NAMES) or set(PROJECT_NAMES[:7]) - set(files):
                    raise ValueError("骨架交换文件不完整")
                directory = self.cache / hashlib.sha256((project_id + revision).encode()).hexdigest()[:24]
                directory.mkdir(parents=True, exist_ok=True)
                sha = hashlib.sha256()
                decoded = {}
                for name in PROJECT_NAMES:
                    if name not in files:
                        continue
                    data = base64.b64decode(files[name], validate=True)
                    if len(data) > 32 * 1024 * 1024:
                        raise ValueError("骨架文件过大")
                    sha.update(name.encode()); sha.update(struct.pack("<i", len(data))); sha.update(data)
                    decoded[name] = data
                if sha.hexdigest() != revision:
                    raise ValueError("骨架传输校验未通过，请重新发送")
                for prefix in ("skeleton", "reference"):
                    gltf = json.loads(decoded[prefix + ".gltf"])
                    for buffer in gltf.get("buffers", []):
                        uri = buffer.get("uri", "")
                        if uri != prefix + ".bin" and not uri.startswith("data:application/octet-stream;base64,"):
                            raise ValueError("骨架包含外部文件引用")
                    if gltf.get("images"):
                        raise ValueError("骨架交换不支持外部图片")
                    complete = json.loads(decoded[prefix + ".complete.json"])
                    sidecar = json.loads(decoded[prefix + ".ffxiv.json"])
                    if complete["projectId"] != sidecar["projectId"] or (prefix == "skeleton" and sidecar["projectId"] != project_id) or complete["gltfSha256"] != hashlib.sha256(decoded[prefix + ".gltf"]).hexdigest():
                        raise ValueError("骨架交换身份不一致")
                for name, data in decoded.items():
                    (directory / name).write_bytes(data)
                (directory / ".csw-network.json").write_text(json.dumps({"projectId": project_id, "revision": revision}), encoding="utf-8")
                armature = import_project(directory / "skeleton.gltf")
                armature["csw_network_revision"] = revision
                armature["csw_source_file"] = "network"
                self.message = "已接收骨架，在当前工程编辑后点击“返回游戏”"
            self.outgoing.put_nowait(("ack", {"transferId": project["transferId"]}))
        except Exception:
            # Do not expose cache paths or operator tracebacks in the public panel.
            self.message = "骨架导入失败，请结束当前操作后在游戏重新发送；旧版项目需重新创建"
        for screen in bpy.data.screens:
            for area in screen.areas:
                if area.type == "VIEW_3D":
                    area.tag_redraw()
        return 0.3


bridge = Connection()
