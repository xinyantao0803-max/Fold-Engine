using System;
using System.Collections;
using System.Collections.Concurrent;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using UnityEngine.Video;

namespace FoldNative
{
    /// <summary>
    /// Native Unity renderer for Fold commands.
    ///
    /// This is a native Unity integration module. NW can copy these scripts
    /// directly into their client source code. It does not require any external
    /// launchers.
    ///
    /// Rendering contract:
    ///   - Creates a ScreenSpaceOverlay Canvas with sortingOrder 9999.
    ///   - Adds a full-screen black backdrop and a RawImage surface.
    ///   - Plays video with Unity's VideoPlayer into a RenderTexture.
    ///   - F9 stops playback; Stop() destroys the whole Canvas.
    /// </summary>
    public sealed class FoldNativeUI : MonoBehaviour
    {
        private readonly ConcurrentQueue<Action> _actions = new ConcurrentQueue<Action>();

        private GameObject _canvasObject;
        private Canvas _canvas;
        private RawImage _screen;
        private AspectRatioFitter _fitter;
        private VideoPlayer _video;
        private RenderTexture _target;
        private AudioSource _audio;
        private Coroutine _imageRoutine;
        private Transform _canvasParent;

        /// <summary>
        /// Optional. Parents the generated Canvas under an existing UI root so the
        /// game's UI stack owns its lifetime. Call before the first PlayVideo or
        /// PlayImage. When a parent is set, overrideSorting keeps sortingOrder 9999
        /// effective inside that hierarchy.
        /// </summary>
        public void SetCanvasParent(Transform parent)
        {
            _canvasParent = parent;
        }

        /// <summary>Queues a UI action; Update drains it on the Unity main thread.</summary>
        public void Enqueue(Action action)
        {
            if (action != null)
                _actions.Enqueue(action);
        }

        /// <summary>
        /// Native video playback. The URL is expected to be an HTTP(S) media URL
        /// already authorized by the signed Fold command.
        /// </summary>
        public void PlayVideo(string url)
        {
            if (string.IsNullOrEmpty(url))
                return;

            Stop();
            EnsureCanvas();

            _video = gameObject.AddComponent<VideoPlayer>();
            _video.playOnAwake = false;
            _video.source = VideoSource.Url;
            _video.url = url;
            _video.renderMode = VideoRenderMode.RenderTexture;
            _video.audioOutputMode = VideoAudioOutputMode.AudioSource;

            _audio = gameObject.AddComponent<AudioSource>();
            _audio.playOnAwake = false;
            _video.SetTargetAudioSource(0, _audio);

            _video.prepareCompleted += OnPrepared;
            _video.errorReceived += OnError;
            _video.Prepare();

            Debug.Log("[FoldNative] preparing video: " + url);
        }

        /// <summary>Native image playback (PNG/JPG) via UnityWebRequestTexture.</summary>
        public void PlayImage(string url)
        {
            if (string.IsNullOrEmpty(url))
                return;

            Stop();
            EnsureCanvas();
            _imageRoutine = StartCoroutine(LoadImage(url));
        }

        /// <summary>Stops playback and destroys the generated Canvas and textures.</summary>
        public void Stop()
        {
            if (_imageRoutine != null)
            {
                StopCoroutine(_imageRoutine);
                _imageRoutine = null;
            }

            if (_video != null)
            {
                _video.prepareCompleted -= OnPrepared;
                _video.errorReceived -= OnError;
                _video.Stop();
                Destroy(_video);
                _video = null;
            }

            if (_audio != null)
            {
                _audio.Stop();
                Destroy(_audio);
                _audio = null;
            }

            if (_target != null)
            {
                _target.Release();
                Destroy(_target);
                _target = null;
            }

            if (_canvasObject != null)
            {
                Destroy(_canvasObject);
                _canvasObject = null;
                _canvas = null;
                _screen = null;
                _fitter = null;
            }
        }

        private void Update()
        {
            while (_actions.TryDequeue(out Action action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Debug.LogError("[FoldNative] UI action failed: " + ex);
                }
            }

            if (_video != null && Input.GetKeyDown(KeyCode.F9))
                Stop();
        }

        private void EnsureCanvas()
        {
            if (_canvasObject != null)
                return;

            _canvasObject = new GameObject("FoldNativeCanvas");
            _canvas = _canvasObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 9999;

            if (_canvasParent != null)
            {
                _canvasObject.transform.SetParent(_canvasParent, false);
                _canvas.overrideSorting = true;
            }

            var scaler = _canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            _canvasObject.AddComponent<GraphicRaycaster>();

            var backdropObject = new GameObject("FoldNativeBackdrop");
            backdropObject.transform.SetParent(_canvasObject.transform, false);
            var backdrop = backdropObject.AddComponent<Image>();
            backdrop.color = Color.black;
            backdrop.raycastTarget = false;
            StretchFull(backdrop.rectTransform);

            var surfaceObject = new GameObject("FoldNativeSurface");
            surfaceObject.transform.SetParent(_canvasObject.transform, false);
            _screen = surfaceObject.AddComponent<RawImage>();
            _screen.raycastTarget = false;
            _screen.color = Color.white;
            StretchFull(_screen.rectTransform);

            _fitter = surfaceObject.AddComponent<AspectRatioFitter>();
            _fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            _fitter.aspectRatio = 16f / 9f;
        }

        private static void StretchFull(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private void OnPrepared(VideoPlayer player)
        {
            if (_screen == null)
                return;

            int width = Mathf.Max(1, (int)player.width);
            int height = Mathf.Max(1, (int)player.height);

            _target = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
            _target.Create();
            player.targetTexture = _target;
            _screen.texture = _target;

            ApplyAspect(width, height);
            player.Play();
            Debug.Log("[FoldNative] video started: " + width + "x" + height);
        }

        private void OnError(VideoPlayer source, string message)
        {
            Debug.LogError("[FoldNative] video error: " + message);
            Stop();
        }

        private IEnumerator LoadImage(string url)
        {
            using (UnityWebRequest request = UnityWebRequestTexture.GetTexture(url))
            {
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError("[FoldNative] image error: " + request.error);
                    Stop();
                    yield break;
                }

                Texture2D texture = DownloadHandlerTexture.GetContent(request);
                if (_screen == null || texture == null)
                    yield break;

                _screen.texture = texture;
                ApplyAspect(texture.width, texture.height);
                Debug.Log("[FoldNative] image started: " + texture.width + "x" + texture.height);
            }
        }

        private void ApplyAspect(float width, float height)
        {
            if (_fitter != null)
                _fitter.aspectRatio = width / Mathf.Max(1f, height);
        }

        private void OnDestroy()
        {
            Stop();
        }
    }
}
