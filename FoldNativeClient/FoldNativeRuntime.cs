using System.Text;
using UnityEngine;

namespace FoldNative
{
    /// <summary>
    /// Entry point for the native Unity integration.
    ///
    /// This is a native Unity integration module. NW can copy these scripts
    /// directly into their client source code. It does not require any external
    /// launchers, injected DLLs, or BepInEx.
    ///
    /// Integration:
    ///   1. Call FoldNativeRuntime.Initialize() once after the client boots.
    ///   2. Call FoldNativeRuntime.LoadPinnedKey() (StreamingAssets/FoldClient.pub).
    ///   3. In your Mirror packet handler, call FoldNativeRuntime.HandleMarker()
    ///      with the received [FOLD_*] envelope.
    /// </summary>
    public static class FoldNativeRuntime
    {
        private static FoldNativeUI _ui;
        private static FoldCommandReceiver _receiver;
        private static bool _initialized;

        /// <summary>The active native renderer, or null before Initialize().</summary>
        public static FoldNativeUI UI
        {
            get { return _ui; }
        }

        /// <summary>Must be called from the Unity main thread during client boot.</summary>
        public static bool Initialize()
        {
            if (_initialized)
                return true;

            var root = new GameObject("FoldNativeClient");
            Object.DontDestroyOnLoad(root);
            _ui = root.AddComponent<FoldNativeUI>();
            _receiver = new FoldCommandReceiver(_ui);
            _initialized = true;

            Debug.Log("[FoldNative] runtime initialized");
            return true;
        }

        /// <summary>
        /// Verifies and executes a signed [FOLD_*] envelope. Safe to call from the
        /// Mirror command handler; render calls are queued to the main thread.
        /// </summary>
        public static bool HandleMarker(string marker)
        {
            if (!_initialized)
                Initialize();

            return _receiver != null && _receiver.Receive(marker);
        }

        /// <summary>UTF-8 payload convenience overload for Mirror packet handlers.</summary>
        public static bool HandlePacket(byte[] utf8Payload)
        {
            if (utf8Payload == null || utf8Payload.Length == 0 || utf8Payload.Length > 4096)
                return false;

            return HandleMarker(Encoding.UTF8.GetString(utf8Payload));
        }

        /// <summary>
        /// Loads the pinned public key from StreamingAssets (desktop platforms).
        /// A native build may embed the key instead; both paths must fail closed.
        /// </summary>
        public static bool LoadPinnedKey(string fileName = "FoldClient.pub")
        {
            string path = System.IO.Path.Combine(Application.streamingAssetsPath, fileName);
            return SignedMediaCommand.LoadPinnedKeyFromFile(path);
        }

        /// <summary>Destroys the native runtime and any active UI.</summary>
        public static void Shutdown()
        {
            if (_ui != null)
                Object.Destroy(_ui.gameObject);

            _ui = null;
            _receiver = null;
            _initialized = false;
        }
    }
}
