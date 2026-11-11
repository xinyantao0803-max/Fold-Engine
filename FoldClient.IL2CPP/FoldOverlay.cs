// This plugin is strictly a passive UI renderer. It does not access game entity
// coordinates or gameplay data. All media and text are explicitly provided by the server.
using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using UnityEngine.Video;

namespace FoldClient.IL2CPP
{
    /// <summary>
    /// IL2CPP 全屏渲染器（ClassInjector 注入的 MonoBehaviour）。
    /// 图片：HttpClient 后台下载 → 主线程队列 → Texture2D → RawImage；
    /// 视频：VideoPlayer + RenderTexture + RawImage + AudioSource 原声；
    /// F9：停止视频并销毁全屏画布（优先 legacy Input，失败时反射新 Input System）。
    /// 注意：托管 MonoBehaviour 必须有 (IntPtr) 构造函数，这是 Il2CppInterop 的硬性要求。
    /// </summary>
    public class FoldOverlay : MonoBehaviour
    {
        public static FoldOverlay Instance { get; private set; }

        private static readonly HttpClient Http = new HttpClient();
        private readonly ConcurrentQueue<Action> _mainThread = new ConcurrentQueue<Action>();

        private RawImage _image;
        private Texture2D _texture;
        private Color _imageColor = new Color(1f, 1f, 1f, 0f);
        private float _hideAt;
        private bool _showing;

        private GameObject _videoHost;
        private VideoPlayer _videoPlayer;
        private AudioSource _videoAudio;
        private RenderTexture _videoTarget;

        private TextMeshProUGUI _subtitle;
        private float _subtitleHideAt;
        private bool _subtitleVisible;

        private GameObject _audioHost;
        private AudioSource _audioSource;
        private UnityWebRequest _audioRequest;
        private float _audioVolume = 1f;

        private bool _legacyInputBroken;

        public FoldOverlay(IntPtr ptr) : base(ptr) { }

        private void Awake()
        {
            Instance = this;
            BuildImageCanvas();
        }

        private void BuildImageCanvas()
        {
            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32760;
            gameObject.AddComponent<CanvasScaler>();
            gameObject.AddComponent<GraphicRaycaster>();

            var go = new GameObject("FoldImage");
            go.transform.SetParent(transform, false);
            _image = go.AddComponent<RawImage>();
            _image.color = _imageColor;
            _image.raycastTarget = false;

            var rect = _image.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            var subtitleGo = new GameObject("FoldSubtitle");
            subtitleGo.transform.SetParent(transform, false);
            _subtitle = subtitleGo.AddComponent<TextMeshProUGUI>();
            _subtitle.alignment = TextAlignmentOptions.Bottom;
            _subtitle.fontSize = 34f;
            _subtitle.color = Color.white;
            _subtitle.raycastTarget = false;
            _subtitle.text = string.Empty;
            var subtitleRect = _subtitle.rectTransform;
            subtitleRect.anchorMin = new Vector2(0.08f, 0.04f);
            subtitleRect.anchorMax = new Vector2(0.92f, 0.22f);
            subtitleRect.offsetMin = Vector2.zero;
            subtitleRect.offsetMax = Vector2.zero;
        }

        // ---------------- 图片 ----------------

        public void ShowImage(string url)
        {
            if (string.IsNullOrEmpty(url)) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    var bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
                    _mainThread.Enqueue(() => ApplyImage(bytes));
                }
                catch (Exception ex)
                {
                    FoldPlugin.LogSource?.LogWarning($"[FoldClient] 图片下载失败 ({url}): {ex.Message}");
                }
            });
        }

        private void ApplyImage(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return;

            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!ImageConversion.LoadImage(texture, bytes))
            {
                Destroy(texture);
                FoldPlugin.LogSource?.LogWarning("[FoldClient] 图片解码失败");
                return;
            }

            if (_texture != null) Destroy(_texture);
            _texture = texture;
            _image.texture = texture;
            _imageColor = Color.white;
            _image.color = _imageColor;
            _showing = true;
            _hideAt = Time.time + 8f;
        }

        // ---------------- 视频 ----------------

        public void PlayVideo(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            StopVideo();

            _videoHost = new GameObject("FoldVideoHost");
            _videoHost.transform.SetParent(transform, false);

            var canvas = _videoHost.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32767;
            _videoHost.AddComponent<CanvasScaler>();
            _videoHost.AddComponent<GraphicRaycaster>();

            var surface = new GameObject("FoldVideoSurface");
            surface.transform.SetParent(_videoHost.transform, false);
            var image = surface.AddComponent<RawImage>();
            image.color = Color.white;
            image.raycastTarget = false;
            var rect = image.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            _videoTarget = new RenderTexture(Screen.width, Screen.height, 24);

            _videoPlayer = _videoHost.AddComponent<VideoPlayer>();
            _videoPlayer.playOnAwake = false;
            _videoPlayer.renderMode = VideoRenderMode.RenderTexture;
            _videoPlayer.targetTexture = _videoTarget;
            _videoPlayer.url = url;
            _videoPlayer.isLooping = false;
            _videoPlayer.skipOnDrop = true;
            _videoPlayer.audioOutputMode = VideoAudioOutputMode.AudioSource;

            _videoAudio = _videoHost.AddComponent<AudioSource>();
            _videoAudio.playOnAwake = false;
            _videoPlayer.SetTargetAudioSource(0, _videoAudio);

            image.texture = _videoTarget;
            _videoPlayer.Play();

            FoldPlugin.LogSource?.LogInfo($"[FoldClient] 开始播放视频: {url}");
        }

        public void StopVideo()
        {
            if (_videoPlayer != null)
            {
                try
                {
                    _videoPlayer.Stop();
                    _videoPlayer.targetTexture = null;
                }
                catch
                {
                    // 对象可能已被 Unity 销毁
                }
                _videoPlayer = null;
            }

            _videoAudio = null;

            if (_videoTarget != null)
            {
                Destroy(_videoTarget);
                _videoTarget = null;
            }

            if (_videoHost != null)
            {
                Destroy(_videoHost);
                _videoHost = null;
            }
        }

        // ---------------- 音频（2D，ogg / mp3） ----------------

        public void PlayAudio(string url, float volume)
        {
            if (string.IsNullOrEmpty(url)) return;
            StopAudio();

            _audioHost = new GameObject("FoldAudio");
            _audioHost.transform.SetParent(transform, false);
            _audioSource = _audioHost.AddComponent<AudioSource>();
            _audioSource.playOnAwake = false;
            _audioSource.spatialBlend = 0f; // 纯 2D
            _audioVolume = Mathf.Clamp01(volume);

            var audioType = url.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
                ? AudioType.OGGVORBIS
                : AudioType.MPEG;

            _audioRequest = UnityWebRequestMultimedia.GetAudioClip(url, audioType);
            _audioRequest.SendWebRequest();
            FoldPlugin.LogSource?.LogInfo($"[FoldClient] 开始下载音频: {url}");
        }

        public void StopAudio()
        {
            if (_audioRequest != null)
            {
                try
                {
                    _audioRequest.Abort();
                    _audioRequest.Dispose();
                }
                catch
                {
                    // 请求可能已结束
                }
                _audioRequest = null;
            }

            if (_audioHost != null)
            {
                Destroy(_audioHost);
                _audioHost = null;
            }
            _audioSource = null;
        }

        private void PollAudio()
        {
            if (_audioRequest == null || !_audioRequest.isDone) return;

            var request = _audioRequest;
            _audioRequest = null;

            if (request.result != UnityWebRequest.Result.Success)
            {
                FoldPlugin.LogSource?.LogWarning($"[FoldClient] 音频下载失败: {request.error}");
                request.Dispose();
                return;
            }

            var clip = DownloadHandlerAudioClip.GetContent(request);
            request.Dispose();

            if (clip == null || _audioSource == null)
            {
                FoldPlugin.LogSource?.LogWarning("[FoldClient] 音频解码失败");
                return;
            }

            _audioSource.clip = clip;
            _audioSource.volume = _audioVolume;
            _audioSource.Play();
        }

        // ---------------- 字幕 ----------------

        public void ShowSubtitle(string text, float duration)
        {
            if (_subtitle == null) return;
            _subtitle.text = text ?? string.Empty;
            _subtitleVisible = !string.IsNullOrEmpty(_subtitle.text);
            _subtitleHideAt = Time.time + Mathf.Max(0.5f, duration);
        }

        // ---------------- 生命周期 ----------------

        private void Update()
        {
            while (_mainThread.TryDequeue(out var action)) action();

            PollAudio();

            if (_subtitleVisible && Time.time > _subtitleHideAt)
            {
                _subtitleVisible = false;
                if (_subtitle != null) _subtitle.text = string.Empty;
            }

            if (_showing)
            {
                float remain = _hideAt - Time.time;
                if (remain <= 0f)
                {
                    float alpha = _imageColor.a - Time.deltaTime / 0.5f;
                    if (alpha <= 0f)
                    {
                        alpha = 0f;
                        _showing = false;
                        _image.texture = null;
                        if (_texture != null)
                        {
                            Destroy(_texture);
                            _texture = null;
                        }
                    }
                    _imageColor = new Color(1f, 1f, 1f, alpha);
                    _image.color = _imageColor;
                }
            }

            if (WasStopPressed()) StopVideo();
        }

        private void OnDestroy()
        {
            StopAudio();
            StopVideo();
            if (_texture != null)
            {
                Destroy(_texture);
                _texture = null;
            }
            if (Instance == this) Instance = null;
        }

        // ---------------- F9 ----------------

        private bool WasStopPressed()
        {
            if (!_legacyInputBroken)
            {
                try
                {
                    return Input.GetKeyDown(KeyCode.F9);
                }
                catch
                {
                    // 游戏可能只启用了新 Input System，legacy Input 会抛异常
                    _legacyInputBroken = true;
                }
            }
            return InputSystemStopPressed();
        }

        private static bool _inputSystemResolved;
        private static PropertyInfo _currentProperty;
        private static PropertyInfo _f9Property;
        private static PropertyInfo _pressedProperty;

        /// <summary>反射读取新 Input System 的 F9，避免编译期强依赖 Unity.InputSystem。</summary>
        private static bool InputSystemStopPressed()
        {
            if (!_inputSystemResolved)
            {
                _inputSystemResolved = true;
                Type keyboardType = Type.GetType("UnityEngine.InputSystem.Keyboard, Unity.InputSystem");
                if (keyboardType == null)
                {
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        keyboardType = assembly.GetType("UnityEngine.InputSystem.Keyboard");
                        if (keyboardType != null) break;
                    }
                }

                if (keyboardType != null)
                {
                    _currentProperty = keyboardType.GetProperty("current",
                        BindingFlags.Public | BindingFlags.Static);
                    _f9Property = keyboardType.GetProperty("f9Key",
                        BindingFlags.Public | BindingFlags.Instance);
                    var keyType = _f9Property?.PropertyType;
                    _pressedProperty = keyType?.GetProperty("wasPressedThisFrame",
                        BindingFlags.Public | BindingFlags.Instance);
                }
            }

            if (_currentProperty == null || _f9Property == null || _pressedProperty == null) return false;

            var keyboard = _currentProperty.GetValue(null);
            if (keyboard == null) return false;
            var key = _f9Property.GetValue(keyboard);
            if (key == null) return false;
            return (bool)_pressedProperty.GetValue(key);
        }
    }
}
