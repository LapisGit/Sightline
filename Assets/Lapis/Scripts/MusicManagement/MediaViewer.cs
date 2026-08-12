using UnityEngine;
using TMPro;
using UnityEngine.UI;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using DesktopPortal.Wristboard;
using Newtonsoft.Json.Linq;

using Debug = UnityEngine.Debug;

public class MediaViewer : MonoBehaviour
{
    [Header("UI References")]
    public TextMeshProUGUI mediaTitle;
    public Image mediaImage;
    public SongTextScroller songTextScroller;

    [Header("Media Manager")]
    public string mediaManagerFolder = "MediaOverlay";
    
    // MediaInfo provided by developer9998/DubyaDude: https://github.com/developer9998/WindowsMediaController/
    public string mediaManagerExecutable = "MediaInfo.exe";

    private Process mediaManagerProcess;

    private readonly ConcurrentQueue<string> pendingMessages = new ConcurrentQueue<string>();
    private readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>();
    private readonly Dictionary<string, Texture2D> thumbnailCache = new Dictionary<string, Texture2D>();

    private string focusedSessionId;

    private void Start()
    {
        StartMediaManager();
    }

    private void Update()
    {
        while (pendingMessages.TryDequeue(out string data))
        {
            ProcessMediaMessage(data);
        }
    }

    private void StartMediaManager()
    {
        try
        {
            string executablePath = Path.Combine(
                Application.streamingAssetsPath,
                mediaManagerFolder,
                mediaManagerExecutable
            );

            Debug.Log($"MediaManager executable path: {executablePath}");

            if (!File.Exists(executablePath))
            {
                Debug.LogError($"MediaManager executable not found: {executablePath}");
                return;
            }

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = Path.GetDirectoryName(executablePath),

                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,

                UseShellExecute = false,
                CreateNoWindow = true
            };

            mediaManagerProcess = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

            mediaManagerProcess.OutputDataReceived += OnOutputDataReceived;
            mediaManagerProcess.ErrorDataReceived += OnErrorDataReceived;

            mediaManagerProcess.Exited += OnProcessExited;

            bool started = mediaManagerProcess.Start();

            if (!started)
            {
                Debug.LogError("Failed to start MediaManager.");
                return;
            }

            Debug.Log($"Started MediaManager process with ID: {mediaManagerProcess.Id}");

            mediaManagerProcess.BeginOutputReadLine();
            mediaManagerProcess.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to start MediaManager: {ex}");
        }
    }

    private void OnOutputDataReceived(object sender, DataReceivedEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.Data))
            return;

        pendingMessages.Enqueue(args.Data);
    }

    private void OnErrorDataReceived(object sender, DataReceivedEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.Data))
            return;

        Debug.LogWarning($"[MediaManager] {args.Data}");
    }

    private void OnProcessExited(object sender, EventArgs args)
    {
        Debug.LogWarning("MediaManager process exited.");
    }

    private void ProcessMediaMessage(string data)
    {
        try
        {
            JObject obj = JObject.Parse(data);

            string eventName =
                (string)obj["EventName"];

            string sessionId =
                (string)obj["SessionId"];

            if (string.IsNullOrEmpty(eventName))
            {
                Debug.LogWarning("MediaManager message has no EventName.");
                return;
            }

            switch (eventName)
            {
                case "AddSession":
                    HandleAddSession(sessionId);
                    break;

                case "RemoveSession":
                    HandleRemoveSession(sessionId);
                    break;

                case "SessionFocusChanged":
                    HandleSessionFocusChanged(sessionId);
                    break;

                case "PlaybackStateChanged":
                    HandlePlaybackStateChanged(obj, sessionId);
                    break;

                case "MediaPropertyChanged":
                    HandleMediaPropertyChanged(obj, sessionId);
                    break;

                case "TimelinePropertyChanged":
                    HandleTimelinePropertyChanged(obj, sessionId);
                    break;

                default:
                    Debug.Log($"Unknown MediaManager event: {eventName}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.LogError(
                $"Error processing MediaManager message:\n{data}\n\n{ex}"
            );
        }
    }

    private void HandleAddSession(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return;

        if (sessions.ContainsKey(sessionId))
            return;

        Session session = new Session
        {
            Id = sessionId
        };

        sessions.Add(sessionId, session);

        Debug.Log($"Added media session: {sessionId}");

        if (string.IsNullOrEmpty(focusedSessionId))
        {
            focusedSessionId = sessionId;

            Debug.Log($"Focused media session: {sessionId}");

            UpdateUI(session);
        }
    }

    private void HandleRemoveSession(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return;

        if (!sessions.Remove(sessionId))
            return;

        Debug.Log($"Removed media session: {sessionId}");

        if (focusedSessionId == sessionId)
        {
            focusedSessionId = null;

            ClearUI();

            foreach (string remainingSessionId in sessions.Keys)
            {
                focusedSessionId = remainingSessionId;
                UpdateUI(sessions[remainingSessionId]);
                break;
            }
        }
    }

    private void HandleSessionFocusChanged(string sessionId)
    {
        focusedSessionId = sessionId;

        Debug.Log($"Media session focus changed: {sessionId}");

        if (!string.IsNullOrEmpty(sessionId) &&
            sessions.TryGetValue(sessionId, out Session session))
        {
            UpdateUI(session);
        }
        else
        {
            ClearUI();
        }
    }

    private void HandlePlaybackStateChanged(JObject obj, string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return;

        Session session = GetOrCreateSession(sessionId);

        session.PlaybackStatus =
            (string)obj["PlaybackStatus"];

        Debug.Log(
            $"Playback state changed: {session.PlaybackStatus}"
        );

        if (sessionId == focusedSessionId)
        {
            UpdateUI(session);
        }
    }

    private void HandleMediaPropertyChanged(JObject obj, string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return;

        Session session = GetOrCreateSession(sessionId);

        session.Title =
            (string)obj["Title"];

        session.Artist =
            (string)obj["Artist"];

        session.AlbumTitle =
            (string)obj["AlbumTitle"];

        string base64Thumbnail =
            (string)obj["Thumbnail"];

        Debug.Log(
            $"Media changed: {session.Title} - {session.Artist}"
        );

        if (!string.IsNullOrEmpty(base64Thumbnail))
        {
            session.Thumbnail =
                CreateThumbnail(base64Thumbnail);
        }

        if (sessionId == focusedSessionId)
        {
            UpdateUI(session);
        }
    }

    private void HandleTimelinePropertyChanged(JObject obj, string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return;

        Session session = GetOrCreateSession(sessionId);

        session.Position =
            (double?)obj["Position"] ?? 0;

        session.StartTime =
            (double?)obj["StartTime"] ?? 0;

        session.EndTime =
            (double?)obj["EndTime"] ?? 0;
    }

    private Session GetOrCreateSession(string sessionId)
    {
        if (!sessions.TryGetValue(sessionId, out Session session))
        {
            session = new Session
            {
                Id = sessionId
            };

            sessions.Add(sessionId, session);

            Debug.Log($"Created missing session: {sessionId}");
        }

        return session;
    }

    private Sprite CreateThumbnail(string base64String)
    {
        try
        {
            if (thumbnailCache.TryGetValue(
                base64String,
                out Texture2D cachedTexture))
            {
                return CreateSprite(cachedTexture);
            }

            byte[] imageBytes =
                Convert.FromBase64String(base64String);

            Texture2D texture = new Texture2D(2, 2)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };

            if (!texture.LoadImage(imageBytes))
            {
                Destroy(texture);
                return null;
            }

            thumbnailCache.Add(
                base64String,
                texture
            );

            return CreateSprite(texture);
        }
        catch (Exception ex)
        {
            Debug.LogWarning(
                $"Failed to load media thumbnail: {ex.Message}"
            );

            return null;
        }
    }

    private Sprite CreateSprite(Texture2D texture)
    {
        return Sprite.Create(
            texture,
            new Rect(
                0,
                0,
                texture.width,
                texture.height
            ),
            new Vector2(0.5f, 0.5f)
        );
    }

    private void UpdateUI(Session session)
    {
        if (session == null)
        {
            ClearUI();
            return;
        }

        string title = session.Title;
        string artist = session.Artist;

        if (!string.IsNullOrEmpty(title) &&
            !string.IsNullOrEmpty(artist))
        {
            mediaTitle.text =
                $"{title} - {artist}";
            songTextScroller.Stop();
            songTextScroller.Setup();
        }
        else if (!string.IsNullOrEmpty(title))
        {
            mediaTitle.text = title;
            songTextScroller.Stop();
            songTextScroller.Setup();
        }
        else
        {
            mediaTitle.text = "No Media";
        }

        if (mediaImage != null &&
            session.Thumbnail != null)
        {
            mediaImage.sprite =
                session.Thumbnail;
        }
    }

    private void ClearUI()
    {
        if (mediaTitle != null)
            mediaTitle.text = "No Media";

        if (mediaImage != null)
            mediaImage.sprite = null;
    }

    private void OnApplicationQuit()
    {
        QuitMediaManager();
    }

    private void OnDestroy()
    {
        QuitMediaManager();

        foreach (Texture2D texture in thumbnailCache.Values)
        {
            if (texture != null)
                Destroy(texture);
        }

        thumbnailCache.Clear();
    }

    private void QuitMediaManager()
    {
        if (mediaManagerProcess == null)
            return;

        try
        {
            if (!mediaManagerProcess.HasExited)
            {
                Debug.Log("Stopping MediaManager...");

                mediaManagerProcess.StandardInput.WriteLine("quit");
                mediaManagerProcess.StandardInput.Flush();

                if (!mediaManagerProcess.WaitForExit(2500))
                {
                    Debug.LogWarning(
                        "MediaManager did not exit normally. Killing process."
                    );

                    mediaManagerProcess.Kill();
                    mediaManagerProcess.WaitForExit();
                }

                Debug.Log("MediaManager stopped.");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError(
                $"Could not stop MediaManager: {ex}"
            );
        }
        finally
        {
            mediaManagerProcess.Dispose();
            mediaManagerProcess = null;
        }
    }

    [Serializable]
    private class Session
    {
        public string Id;

        public string Title;
        public string Artist;
        public string AlbumTitle;

        public string PlaybackStatus;

        public double Position;
        public double StartTime;
        public double EndTime;

        public Sprite Thumbnail;
    }
}