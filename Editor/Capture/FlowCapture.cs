using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityFlow.Editor.Capture
{
    /// <summary>
    /// Screenshot capture for flow artifacts.
    ///
    /// This does NOT reuse the pipeline package's screenshot/capture_game_view commands, and the
    /// reason is concrete: those render a single camera with Camera.Render(), so a ScreenSpaceOverlay
    /// canvas — which is most of a game's UI — never appears in the image. A UI test whose failure
    /// screenshot omits the UI is worse than no screenshot, because it looks authoritative.
    ///
    /// Instead this grabs the actual backbuffer with ScreenCapture, which is what the player is
    /// really showing, overlay canvases included.
    /// </summary>
    public static class FlowCapture
    {
        /// <summary>
        /// Whether capture can work at all right now. In batch mode with no graphics device there
        /// is no backbuffer, and capturing anyway silently writes a blank PNG that reads as
        /// "the screen was empty" rather than "capture was impossible".
        /// </summary>
        public static bool IsAvailable(out string reason)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                reason = "no graphics device (the editor is running headless, -nographics); " +
                         "a screenshot here would be a blank image, not a picture of the screen";
                return false;
            }

            if (!Application.isPlaying)
            {
                reason = "not in play mode; there is no rendered game frame to capture";
                return false;
            }

            reason = null;
            return true;
        }

        /// <summary>
        /// Capture the current frame to a PNG.
        ///
        /// Must be called at end of frame — the caller (a step coroutine) is responsible for having
        /// let rendering happen, because ScreenCapture reads the backbuffer as it stands.
        /// </summary>
        public static bool TryCapture(string absolutePath, out string error)
        {
            if (!IsAvailable(out error))
                return false;

            try
            {
                var directory = Path.GetDirectoryName(absolutePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                var texture = ScreenCapture.CaptureScreenshotAsTexture();
                try
                {
                    var png = texture.EncodeToPNG();
                    if (png == null || png.Length == 0)
                    {
                        error = "the frame encoded to an empty PNG; the backbuffer was not readable this frame";
                        return false;
                    }

                    File.WriteAllBytes(absolutePath, png);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                }

                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        /// <summary>Turn a flow-supplied screenshot name into a safe file name.</summary>
        public static string SanitizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("A screenshot name is required.", nameof(name));

            var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
            var chars = new char[name.Length];
            for (var i = 0; i < name.Length; i++)
                chars[i] = invalid.Contains(name[i]) ? '_' : name[i];

            var safe = new string(chars);
            return safe.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? safe : safe + ".png";
        }
    }
}
