using BepInEx;
using HS2Manager.Protocol;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;

namespace HS2Manager.Bridge
{
    [BepInPlugin("local.hs2manager.preview", "HS2 Manager Preview", "1.0.0")]
    [BepInProcess("StudioNEOV2.exe")]
    public sealed class PreviewBridge : BaseUnityPlugin
    {
        private string session;
        private NamedPipeClientStream pipe;
        private MemoryMappedFile map;
        private MemoryMappedViewAccessor view;
        private Mutex frameLock;
        private readonly object sendLock = new object();
        private readonly ConcurrentQueue<BridgeMessage> commands = new ConcurrentQueue<BridgeMessage>();
        private volatile bool connected, disconnected;
        private bool ready, loading, quitting;
        private int pendingReads;
        private Camera camera;
        private RenderTexture target;
        private Texture2D fallback;
        private BridgeMessage cameraState = new BridgeMessage();
        private BridgeMessage defaultCamera = new BridgeMessage();
        private BridgeMessage pendingLoad;
        private byte[] pixels = new byte[BridgeWire.FrameBytes];
        private byte[] rawPixels = new byte[BridgeWire.FrameBytes];
        private long sequence;
        private float lastFrame;
        private long lastContact;
        private Light[] lights;
        private float[] originalLight;
        private bool asyncRead;

        private void Awake()
        {
            session = Environment.GetEnvironmentVariable("HS2_MANAGER_SESSION");
            if (string.IsNullOrEmpty(session)) { enabled = false; return; }
            Application.runInBackground = true;
            Application.targetFrameRate = 30;
            lastContact = DateTime.UtcNow.Ticks;
            new Thread(Connect) { IsBackground = true, Name = "HS2Manager IPC" }.Start();
            Logger.LogInfo("Manager preview session " + session);
        }
        private void Connect()
        {
            try
            {
                pipe = new NamedPipeClientStream(".", "HS2Manager." + session, PipeDirection.InOut);
                pipe.Connect(300000);
                map = MemoryMappedFile.OpenExisting("Local\\HS2Manager.Frames." + session);
                view = map.CreateViewAccessor();
                frameLock = Mutex.OpenExisting("Local\\HS2Manager.Lock." + session);
                connected = true;
                Send(new BridgeMessage { Kind = "Connected", Text = "正在等待 Studio 场景" });
                while (connected) { var message = BridgeWire.Read(pipe); Interlocked.Exchange(ref lastContact, DateTime.UtcNow.Ticks); commands.Enqueue(message); }
            }
            catch (Exception ex) { Logger.LogWarning(ex.ToString()); disconnected = true; }
        }
        private void Send(BridgeMessage message)
        {
            try { lock (sendLock) if (pipe != null && pipe.IsConnected) BridgeWire.Write(pipe, message); }
            catch { disconnected = true; }
        }
        private void Update()
        {
            if (disconnected || (connected && DateTime.UtcNow.Ticks - Interlocked.Read(ref lastContact) > TimeSpan.FromSeconds(30).Ticks)) { Quit(); return; }
            BridgeMessage cmd;
            while (commands.TryDequeue(out cmd))
            {
                try
                {
                    switch (cmd.Kind)
                    {
                        case "Ping": break;
                        case "Shutdown": Quit(); return;
                        case "LoadCard": pendingLoad = cmd; break;
                        case "Camera": cameraState = cmd; break;
                        case "Frame": cameraState = defaultCamera; SendCameraState(); break;
                        case "Environment":
                            if (camera != null) camera.backgroundColor = new Color(cmd.R, cmd.G, cmd.B, 1);
                            if (lights != null) for (int i = 0; i < lights.Length; i++) if (lights[i]) lights[i].intensity = originalLight[i] * Mathf.Clamp(cmd.Light, .1f, 3);
                            break;
                    }
                }
                catch (Exception ex) { Send(new BridgeMessage { Kind = "Error", Id = cmd.Id, Text = ex.Message }); }
            }
            if (!connected) return;
            if (!ready)
            {
                try
                {
                    if (Manager.Scene.NowSceneNames.Contains("Studio") && Studio.Studio.Instance != null && Studio.Studio.Instance.cameraCtrl != null)
                    {
                        SetupCamera(); ready = true;
                        Send(new BridgeMessage { Kind = "Ready", Text = asyncRead ? "720p · 异步 GPU 读回" : "720p · 兼容读回（10 fps）" });
                    }
                }
                catch (Exception ex) { Logger.LogDebug(ex.Message); }
            }
            if (ready && !loading && pendingLoad != null)
            {
                var load = pendingLoad; pendingLoad = null;
                StartCoroutine(Load(load));
            }
        }
        private void SetupCamera()
        {
            var ctrl = Studio.Studio.Instance.cameraCtrl;
            camera = ctrl.mainCmaera != null ? ctrl.mainCmaera : Camera.main;
            if (camera == null) throw new InvalidOperationException("Studio camera unavailable");
            ctrl.enabled = false;
            camera.enabled = false;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(.16f, .17f, .19f, 1);
            camera.fieldOfView = 35;
            target = new RenderTexture(BridgeWire.Width, BridgeWire.Height, 24, RenderTextureFormat.ARGB32);
            target.Create(); camera.targetTexture = target;
            asyncRead = SystemInfo.supportsAsyncGPUReadback;
            fallback = new Texture2D(BridgeWire.Width, BridgeWire.Height, TextureFormat.RGBA32, false);
            lights = FindObjectsOfType<Light>(); originalLight = lights.Select(x => x.intensity).ToArray();
            HideInterface();
        }
        private void HideInterface()
        {
            foreach (var canvas in FindObjectsOfType<Canvas>()) if (canvas.renderMode != RenderMode.WorldSpace) canvas.enabled = false;
            foreach (var renderer in FindObjectsOfType<Renderer>())
                if (renderer.gameObject.layer == LayerMask.NameToLayer("Studio/Select") || renderer.gameObject.name == "Grid") renderer.enabled = false;
        }
        private IEnumerator Load(BridgeMessage request)
        {
            loading = true;
            Send(new BridgeMessage { Kind = "Loading", Id = request.Id, Text = "正在加载人物与 MOD…" });
            Exception failure = null;
            try
            {
                if (!File.Exists(request.Path)) throw new FileNotFoundException("人物卡不可访问", request.Path);
                foreach (var character in Studio.Studio.Instance.dicObjectCtrl.Values.OfType<Studio.OCIChar>().ToArray())
                    Studio.Studio.Instance.treeNodeCtrl.DeleteNode(character.treeNodeObject);
            }
            catch (Exception ex) { failure = ex; }
            if (failure == null)
            {
                try
                {
                    if (request.Sex == 0) Studio.Studio.Instance.AddMale(request.Path);
                    else Studio.Studio.Instance.AddFemale(request.Path);
                }
                catch (Exception ex) { failure = ex; }
            }
            yield return null;
            if (failure == null)
            {
                try
                {
                    var chars = Studio.Studio.Instance.dicObjectCtrl.Values.OfType<Studio.OCIChar>().ToArray();
                    if (chars.Length == 0) throw new InvalidDataException("Studio 未创建人物，请检查插件日志");
                    HideInterface(); cameraState = new BridgeMessage();
                    var renderers = chars[0].charInfo.GetComponentsInChildren<Renderer>(true);
                    var visible = renderers.Where(r => r.enabled && r.gameObject.activeInHierarchy && r.bounds.size.sqrMagnitude > .0001f).ToArray();
                    if (visible.Length > 0)
                    {
                        var bounds = ActualBounds(visible[0]);
                        foreach (var r in visible.Skip(1)) bounds.Encapsulate(ActualBounds(r));
                        cameraState.PanX = bounds.center.x; cameraState.PanY = bounds.center.y; cameraState.PanZ = bounds.center.z;
                        cameraState.Distance = Mathf.Max(bounds.size.y, bounds.size.x / camera.aspect) * 2.05f;
                    }
                    defaultCamera = cameraState;
                    SendCameraState(request.Id);
                    Send(new BridgeMessage { Kind = "Loaded", Id = request.Id, Text = "人物已加载" });
                }
                catch (Exception ex) { failure = ex; }
            }
            if (failure != null) { Logger.LogError(failure); Send(new BridgeMessage { Kind = "Error", Id = request.Id, Text = failure.Message }); }
            loading = false;
        }
        private void LateUpdate()
        {
            if (!ready || quitting || !camera || pendingReads >= 3 || loading) return;
            if (Time.realtimeSinceStartup - lastFrame < (asyncRead ? 1f / 30 : .1f)) return;
            lastFrame = Time.realtimeSinceStartup;
            try
            {
                var q = Quaternion.Euler(Mathf.Clamp(cameraState.Pitch, -80, 80), cameraState.Yaw + 180, 0);
                var center = new Vector3(cameraState.PanX, cameraState.PanY, cameraState.PanZ);
                camera.transform.position = center + q * new Vector3(0, 0, -Mathf.Clamp(cameraState.Distance, .1f, 100));
                camera.transform.LookAt(center);
                camera.Render();
                if (asyncRead)
                {
                    pendingReads++;
                    AsyncGPUReadback.Request(target, 0, TextureFormat.BGRA32, result =>
                    {
                        try
                        {
                            if (quitting) return;
                            if (result.hasError) { asyncRead = false; Send(new BridgeMessage { Kind = "Status", Text = "GPU 读回失败，切换兼容模式" }); }
                            else { result.GetData<byte>().CopyTo(rawPixels); Publish(rawPixels, true); }
                        }
                        catch (Exception ex) { Logger.LogWarning(ex.Message); asyncRead = false; }
                        finally { pendingReads--; }
                    });
                }
                else
                {
                    var old = RenderTexture.active; RenderTexture.active = target;
                    try { fallback.ReadPixels(new Rect(0, 0, BridgeWire.Width, BridgeWire.Height), 0, 0, false); Publish(fallback.GetRawTextureData(), false); }
                    finally { RenderTexture.active = old; }
                }
            }
            catch (Exception ex) { Logger.LogWarning(ex.Message); }
        }
        private Bounds ActualBounds(Renderer renderer)
        {
            var skin = renderer as SkinnedMeshRenderer;
            if (!skin) return renderer.bounds;
            var mesh = new Mesh();
            try
            {
                skin.BakeMesh(mesh); var bounds = mesh.bounds;
                var center = renderer.transform.TransformPoint(bounds.center);
                var result = new Bounds(center, Vector3.zero);
                for (int mask = 0; mask < 8; mask++)
                    result.Encapsulate(renderer.transform.TransformPoint(bounds.center + Vector3.Scale(bounds.extents, new Vector3((mask & 1) == 0 ? -1 : 1, (mask & 2) == 0 ? -1 : 1, (mask & 4) == 0 ? -1 : 1))));
                return result;
            }
            finally { Destroy(mesh); }
        }
        private void SendCameraState(string requestId = "")
        {
            Send(new BridgeMessage { Kind = "CameraState", Id = requestId, Yaw = cameraState.Yaw, Pitch = cameraState.Pitch, Distance = cameraState.Distance, PanX = cameraState.PanX, PanY = cameraState.PanY, PanZ = cameraState.PanZ });
        }
        private unsafe void Publish(byte[] rgba, bool bgra)
        {
            if (rgba.Length < pixels.Length || view == null) return;
            for (int y = 0; y < BridgeWire.Height; y++)
            {
                if (bgra) { Buffer.BlockCopy(rgba, (BridgeWire.Height - 1 - y) * BridgeWire.Width * 4, pixels, y * BridgeWire.Width * 4, BridgeWire.Width * 4); continue; }
                for (int x = 0; x < BridgeWire.Width; x++)
                {
                    int src = ((BridgeWire.Height - 1 - y) * BridgeWire.Width + x) * 4;
                    int dst = (y * BridgeWire.Width + x) * 4;
                    pixels[dst] = rgba[src + 2]; pixels[dst + 1] = rgba[src + 1]; pixels[dst + 2] = rgba[src]; pixels[dst + 3] = 255;
                }
            }
            bool held = false;
            try
            {
                try { held = frameLock.WaitOne(0); } catch (AbandonedMutexException) { held = true; }
                if (!held) return;
                int slot = (int)((sequence + 1) % 2);
                byte* pointer = null;
                view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
                try { System.Runtime.InteropServices.Marshal.Copy(pixels, 0, (IntPtr)(pointer + BridgeWire.Header + (long)slot * BridgeWire.FrameBytes), pixels.Length); }
                finally { view.SafeMemoryMappedViewHandle.ReleasePointer(); }
                view.Write(8, slot); view.Write(0, ++sequence);
            }
            finally { if (held) frameLock.ReleaseMutex(); }
        }
        private void Quit() { if (quitting) return; quitting = true; Application.Quit(); }
        private void OnDestroy()
        {
            quitting = true; connected = false;
            if (pipe != null) pipe.Dispose();
            if (view != null) view.Dispose(); if (map != null) map.Dispose(); if (frameLock != null) frameLock.Dispose();
            if (target) { target.Release(); Destroy(target); } if (fallback) Destroy(fallback);
        }
    }
}
