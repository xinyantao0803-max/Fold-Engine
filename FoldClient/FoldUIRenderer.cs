using System.Collections;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using UnityEngine.Video;

namespace FoldClient
{
    /// <summary>
    /// 全屏渲染器：
    /// 图片 —— UnityWebRequestTexture 下载 → RawImage 全屏显示，数秒后淡出；
    /// 视频 —— VideoPlayer(RenderTexture) → RawImage 全屏渲染，AudioSource 播放原声。
    /// F9 一键停止视频并销毁全屏画布，防卡死。
    /// </summary>
    public sealed class FoldUIRenderer : MonoBehaviour
    {
        /// <summary>全局唯一实例（由 FoldPlugin 挂载）。</summary>
        public static FoldUIRenderer Instance { get; private set; }

        private const float DisplaySeconds = 8f;
        private const float FadeSeconds = 0.5f;

        private RawImage _image;
        private Coroutine _routine;

        private GameObject _videoHost;
        private VideoPlayer _videoPlayer;
        private AudioSource _videoAudio;

        private void Awake()
        {
            Instance = this;
            BuildUi();
        }

        /// <summary>代码构建全屏覆盖 UI（不依赖任何场景对象）。</summary>
        private void BuildUi()
        {
            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32767; // 盖住游戏 HUD
            gameObject.AddComponent<CanvasScaler>();
            gameObject.AddComponent<GraphicRaycaster>();

            var go = new GameObject("FoldImage");
            go.transform.SetParent(transform, false);
            _image = go.AddComponent<RawImage>();
            _image.color = new Color(1f, 1f, 1f, 0f); // 初始全透明
            _image.raycastTarget = false;

            var rect = _image.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        /// <summary>下载并全屏显示一张图片（新的请求会打断旧图）。</summary>
        public void ShowImage(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            if (_routine != null) StopCoroutine(_routine);
            _routine = StartCoroutine(DownloadAndShow(url));
        }

        private IEnumerator DownloadAndShow(string url)
        {
            using (var request = UnityWebRequestTexture.GetTexture(url))
            {
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success) yield break;

                var texture = DownloadHandlerTexture.GetContent(request);
                _image.texture = texture;
                _image.color = Color.white;

                // 全屏显示一段时间
                yield return new WaitForSeconds(DisplaySeconds);

                // 淡出后清理
                float t = 0f;
                while (t < FadeSeconds)
                {
                    t += Time.deltaTime;
                    _image.color = new Color(1f, 1f, 1f, 1f - t / FadeSeconds);
                    yield return null;
                }
                _image.color = new Color(1f, 1f, 1f, 0f);
                _image.texture = null;
            }
        }

        /// <summary>全屏播放视频（WebM/MP4 CDN 链接），最高层级，带原声。</summary>
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

            _videoPlayer = _videoHost.AddComponent<VideoPlayer>();
            _videoPlayer.playOnAwake = false;
            _videoPlayer.renderMode = VideoRenderMode.RenderTexture;
            _videoPlayer.targetTexture = new RenderTexture(Screen.width, Screen.height, 24);
            _videoPlayer.url = url;
            _videoPlayer.isLooping = false;
            _videoPlayer.skipOnDrop = true;
            _videoPlayer.audioOutputMode = VideoAudioOutputMode.AudioSource;
            _videoAudio = _videoHost.AddComponent<AudioSource>();
            _videoPlayer.SetTargetAudioSource(0, _videoAudio);
            image.texture = _videoPlayer.targetTexture;
            _videoPlayer.Play();
        }

        /// <summary>停止视频并销毁全屏画布。</summary>
        public void StopVideo()
        {
            if (_videoPlayer != null)
            {
                _videoPlayer.Stop();
                _videoPlayer.targetTexture = null;
                _videoPlayer = null;
            }
            _videoAudio = null;
            if (_videoHost != null)
            {
                Destroy(_videoHost);
                _videoHost = null;
            }
        }

        private void Update()
        {
            // F9：一键停止视频并销毁全屏画布，防卡死
            if (Input.GetKeyDown(KeyCode.F9))
            {
                StopVideo();
            }
        }

        private void OnDestroy()
        {
            StopVideo();
            if (Instance == this) Instance = null;
        }
    }
}
