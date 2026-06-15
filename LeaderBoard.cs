using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using Photon.Pun;
using Photon.Realtime;
using TMPro;
using Photon.Voice.PUN;
using Photon.Voice.Unity;
using Photon.VR.Player;
using PlayFab;
using PlayFab.ClientModels;
using ExitGames.Client.Photon;
using System.Collections;

using Hashtable = ExitGames.Client.Photon.Hashtable;

[RequireComponent(typeof(PhotonView))]
public class LeaderBoard : MonoBehaviourPunCallbacks
{
    [Header("Display")]
    [SerializeField] private TMP_Text[] displaySpot;
    [SerializeField] private Renderer[] colorSpot;
    [SerializeField] private TMP_Text[] roleSpot;

    [Header("Name Tag Settings")]
    [SerializeField] private TMP_FontAsset nameFont;
    [SerializeField] private bool hideMyTag = false;

    [Header("Webhook Settings")]
    [SerializeField] private string WebHookURL;
    [SerializeField] private string WebhookUsername = "Admin Report";
    [SerializeField] private string WebhookAvatarURL = "";
    [SerializeField] private bool RedWebhookName = true;

    [Header("References")]
    [SerializeField] private Playfablogin playfablogin;

    [Header("Audio Buffer")]
    [SerializeField] private Recorder photonRecorder;
    [SerializeField] private int bufferSeconds = 5;

    private PhotonView _photonView;
    private int _sampleRate;

    private PhotonVRPlayer[] _cachedPVRPlayers = Array.Empty<PhotonVRPlayer>();
    private readonly Dictionary<int, PhotonVRPlayer> _pvrByActor = new Dictionary<int, PhotonVRPlayer>();
    private readonly Dictionary<int, int> _actorToSlot = new Dictionary<int, int>();
    private readonly List<Player> _orderedPlayers = new List<Player>();

    private bool _kickVerifyPending;
    private float _lastKickAttempt = -999f;
    private const float KickCooldown = 5f;

    private float _lastReportTime = -999f;
    private const float ReportCooldown = 30f;

    private float _lastCacheTime = -999f;
    private const float CacheInterval = 60f;
    private bool _cacheDirty = true;
    private bool _rebuildQueued;

   private MaterialPropertyBlock _mpb;

    private string[] _lastNames = Array.Empty<string>();
    private string[] _lastRoles = Array.Empty<string>();
    private string[] _lastColors = Array.Empty<string>();

    private readonly WaitForSeconds _reportDelay = new WaitForSeconds(0.1f);

    [Serializable]
    private class DiscordWebhookPayload
    {
        public List<DiscordEmbed> embeds = new List<DiscordEmbed>();
        public string username;
        public string avatar_url;
    }

    [Serializable]
    private class DiscordEmbed
    {
        public string title;
        public int color;
        public List<DiscordField> fields = new List<DiscordField>();
    }

    [Serializable]
    private class DiscordField
    {
        public string name;
        public string value;
        public bool inline;
    }

    private void Awake()
    {
    _photonView = GetComponent<PhotonView>();

    _mpb = new MaterialPropertyBlock();

    EnsureCacheArrays();
    }

    private void Start()
    {
        _sampleRate = AudioSettings.outputSampleRate;

        if (photonRecorder == null)
            photonRecorder = FindObjectOfType<Recorder>();

        EnsureCacheArrays();

        if (PhotonNetwork.InRoom)
            ForceRebuild();
    }

    private void OnValidate()
    {
        if (displaySpot == null) displaySpot = Array.Empty<TMP_Text>();
        if (colorSpot == null) colorSpot = Array.Empty<Renderer>();
        if (roleSpot == null) roleSpot = Array.Empty<TMP_Text>();
    }

    private void EnsureCacheArrays()
    {
        int slotCount = GetSlotCount();

        if (_lastNames == null || _lastNames.Length != slotCount)
            _lastNames = new string[slotCount];

        if (_lastRoles == null || _lastRoles.Length != slotCount)
            _lastRoles = new string[slotCount];

        if (_lastColors == null || _lastColors.Length != slotCount)
            _lastColors = new string[slotCount];
    }

    private int GetSlotCount()
    {
        int d = displaySpot != null ? displaySpot.Length : 0;
        int r = roleSpot != null ? roleSpot.Length : 0;
        int c = colorSpot != null ? colorSpot.Length : 0;
        return Mathf.Max(d, Mathf.Max(r, c));
    }

    public override void OnJoinedRoom()
    {
        RequestRebuild();
    }

    public override void OnPlayerEnteredRoom(Player newPlayer)
    {
        RequestRebuild();
    }

    public override void OnPlayerLeftRoom(Player otherPlayer)
    {
        RequestRebuild();
    }

    public override void OnPlayerPropertiesUpdate(Player targetPlayer, Hashtable changedProps)
    {
        if (targetPlayer == null)
            return;

        UpdateSinglePlayer(targetPlayer);
    }

    private void RequestRebuild()
    {
        _cacheDirty = true;

        if (_rebuildQueued)
            return;

        _rebuildQueued = true;
        StartCoroutine(RebuildNextFrame());
    }

    private IEnumerator RebuildNextFrame()
    {
        yield return null;
        _rebuildQueued = false;

        if (_cacheDirty)
            ForceRebuild();
    }

    public void ForceRefreshCache()
    {
        ForceRebuild();
    }

    public void ForceRebuild()
    {
        EnsureCacheArrays();
        RebuildPlayerCache();
        RefreshAllUI();
        _cacheDirty = false;
        _lastCacheTime = Time.time;
    }

    public void RefreshDisplay()
    {
        RefreshAllUI();
    }

    private void Update()
    {
        if (_cacheDirty && Time.time - _lastCacheTime >= CacheInterval)
            ForceRebuild();
    }

    private void RebuildPlayerCache()
    {
        _orderedPlayers.Clear();
        _actorToSlot.Clear();

        Player[] players = PhotonNetwork.PlayerList;
        if (players == null)
            players = Array.Empty<Player>();

        for (int i = 0; i < players.Length; i++)
        {
            Player p = players[i];
            if (p == null)
                continue;

            _orderedPlayers.Add(p);
            _actorToSlot[p.ActorNumber] = _orderedPlayers.Count - 1;
        }

        RefreshPhotonVRCache();
    }

    private void RefreshPhotonVRCache()
    {
        _cachedPVRPlayers = FindObjectsOfType<PhotonVRPlayer>();
        _pvrByActor.Clear();

        if (_cachedPVRPlayers == null)
            return;

        for (int i = 0; i < _cachedPVRPlayers.Length; i++)
        {
            PhotonVRPlayer pvr = _cachedPVRPlayers[i];
            if (pvr == null)
                continue;

            PhotonView view = pvr.GetComponent<PhotonView>();
            if (view == null || view.Owner == null)
                continue;

            _pvrByActor[view.Owner.ActorNumber] = pvr;
        }
    }

    private void RefreshAllUI()
    {
        EnsureCacheArrays();

        int slotCount = GetSlotCount();
        for (int i = 0; i < slotCount; i++)
        {
            if (i < _orderedPlayers.Count)
                ApplyPlayerToSlot(_orderedPlayers[i], i);
            else
                ClearSlot(i);
        }
    }

    private void UpdateSinglePlayer(Player player)
    {
        if (player == null)
            return;

        if (!_actorToSlot.TryGetValue(player.ActorNumber, out int index))
        {
            RequestRebuild();
            return;
        }

        ApplyPlayerToSlot(player, index);
    }

    private void ApplyPlayerToSlot(Player player, int index)
    {
        if (player == null || index < 0)
            return;

        string role = GetProp(player, "Role", "Player");
        string prefix = GetRolePrefix(role, player.IsLocal);

        string name = prefix + player.NickName;
        if (index < _lastNames.Length && _lastNames[index] != name)
        {
            _lastNames[index] = name;

            if (displaySpot != null && index < displaySpot.Length && displaySpot[index] != null)
            {
                TMP_Text nameText = displaySpot[index];
                nameText.text = name;

                if (nameFont != null && nameText.font != nameFont)
                    nameText.font = nameFont;
            }
        }

        if (index < _lastRoles.Length && _lastRoles[index] != role)
        {
            _lastRoles[index] = role;

            if (roleSpot != null && index < roleSpot.Length && roleSpot[index] != null)
            {
                roleSpot[index].text = role;
                roleSpot[index].color = GetRoleColor(role);
            }
        }

        string colHex = GetProp(player, "Colour", "#FFFFFF");
        if (index < _lastColors.Length && _lastColors[index] != colHex)
        {
            _lastColors[index] = colHex;

            if (colorSpot != null && index < colorSpot.Length && colorSpot[index] != null)
            {
                if (ColorUtility.TryParseHtmlString(colHex, out Color c))
                    ApplyRendererColor(colorSpot[index], c);
                else
                    ApplyRendererColor(colorSpot[index], Color.white);
            }
        }
    }

    private void ClearSlot(int index)
    {
        if (displaySpot != null && index < displaySpot.Length && displaySpot[index] != null)
            displaySpot[index].text = string.Empty;

        if (roleSpot != null && index < roleSpot.Length && roleSpot[index] != null)
            roleSpot[index].text = string.Empty;

        if (colorSpot != null && index < colorSpot.Length && colorSpot[index] != null)
            ApplyRendererColor(colorSpot[index], Color.white);

        if (_lastNames != null && index < _lastNames.Length) _lastNames[index] = null;
        if (_lastRoles != null && index < _lastRoles.Length) _lastRoles[index] = null;
        if (_lastColors != null && index < _lastColors.Length) _lastColors[index] = null;
    }

    private void ApplyRendererColor(Renderer r, Color c)
    {
        if (r == null)
            return;

        _mpb.Clear();
        _mpb.SetColor("_Color", c);
        _mpb.SetColor("_BaseColor", c);
        r.SetPropertyBlock(_mpb);
    }

    private string GetProp(Player p, string key, string fallback)
    {
        if (p != null &&
            p.CustomProperties != null &&
            p.CustomProperties.TryGetValue(key, out object v) &&
            v != null)
        {
            return v.ToString();
        }

        return fallback;
    }

    private string GetRolePrefix(string role, bool isLocal)
    {
        if (isLocal && hideMyTag)
            return string.Empty;

        switch (role)
        {
            case "Owner": return "<color=red><b>[OWNER]</b></color> ";
            case "Admin": return "<color=green><b>[ADMIN]</b></color> ";
            case "Manager": return "<color=orange><b>[MANAGER]</b></color> ";
            case "Mod": return "<color=#4FC3F7><b>[MOD]</b></color> ";
            case "YT": return "<color=red><b>[YT]</b></color> ";
            default: return string.Empty;
        }
    }

    private Color GetRoleColor(string role)
    {
        switch (role)
        {
            case "Owner": return new Color(1f, 0.84f, 0f);
            case "Admin": return new Color(0.18f, 0.8f, 0.44f);
            case "Manager": return new Color(1f, 0.65f, 0f);
            case "Mod": return new Color(0.31f, 0.76f, 0.97f);
            case "YT": return Color.red;
            default: return Color.white;
        }
    }

    public void ToggleUndercoverMode()
    {
        hideMyTag = !hideMyTag;
        RefreshAllUI();
    }

    public bool IsUndercover()
    {
        return hideMyTag;
    }

    public void MutePress(int index)
    {
        Player[] players = PhotonNetwork.PlayerList;
        if (players == null || index < 0 || index >= players.Length)
            return;

        Player target = players[index];
        if (target == null)
            return;

        if (_pvrByActor.TryGetValue(target.ActorNumber, out PhotonVRPlayer pvrp) && pvrp != null)
        {
            PhotonVoiceView voice = pvrp.GetComponent<PhotonVoiceView>();
            if (voice != null && voice.SpeakerInUse != null)
            {
                AudioSource audio = voice.SpeakerInUse.GetComponent<AudioSource>();
                if (audio != null)
                    audio.mute = !audio.mute;
            }
        }
    }

    public void KickPress(int index)
    {
        if (playfablogin == null)
            return;

        if (!Playfablogin.AntiCheatReady)
            return;

        if (!Playfablogin.CanKick())
            return;

        Player[] players = PhotonNetwork.PlayerList;
        if (players == null || index < 0 || index >= players.Length)
            return;

        Player target = players[index];
        if (target == null || target.IsLocal)
            return;

        string targetRole = GetProp(target, "Role", "Player");
        if (!Playfablogin.CanKickRole(targetRole))
        {
            Debug.LogWarning("[KICK] Blocked — target role equal or higher: " + targetRole);
            return;
        }

        string targetPlayFabId = GetProp(target, "PlayfabID", string.Empty);
        if (string.IsNullOrEmpty(targetPlayFabId))
        {
            Debug.LogWarning("[KICK] Target has no PlayFab ID in properties — cannot verify.");
            return;
        }

        if (!_pvrByActor.TryGetValue(target.ActorNumber, out PhotonVRPlayer pvr) || pvr == null)
            return;

        PhotonView view = pvr.GetComponent<PhotonView>();
        if (view == null || view.Owner == null)
            return;

        _photonView.RPC(nameof(KickPlayer), view.Owner, playfablogin.MyPlayFabID, targetPlayFabId);
    }

    [PunRPC]
    private void KickPlayer(string senderPlayfabId, string targetPlayfabId, PhotonMessageInfo info)
    {
        if (Time.time - _lastKickAttempt < KickCooldown)
            return;

        _lastKickAttempt = Time.time;

        if (playfablogin == null || !Playfablogin.AntiCheatReady)
            return;

        string actualSenderPlayfabId = string.Empty;
        if (info.Sender != null &&
            info.Sender.CustomProperties != null &&
            info.Sender.CustomProperties.TryGetValue("PlayfabID", out object sid) &&
            sid != null)
        {
            actualSenderPlayfabId = sid.ToString();
        }

        if (actualSenderPlayfabId != senderPlayfabId)
        {
            Debug.LogWarning("[KICK RPC] Spoofed sender PlayFab ID — rejected.");
            return;
        }

        if (_kickVerifyPending)
            return;

        _kickVerifyPending = true;

        PlayFabClientAPI.ExecuteCloudScript(new ExecuteCloudScriptRequest
        {
            FunctionName = "VerifyKick",
            FunctionParameter = new
            {
                kickerPlayFabId = senderPlayfabId,
                targetPlayFabId = targetPlayfabId
            },
            GeneratePlayStreamEvent = true
        },
        result =>
        {
            _kickVerifyPending = false;

            if (result.FunctionResult == null)
            {
                Debug.LogWarning("[KICK RPC] Cloud Script returned null.");
                return;
            }

            string json = result.FunctionResult.ToString();
            Debug.Log("[KICK RPC] Cloud Script result: " + json);

            if (json.Contains("\"approved\":true"))
            {
                Debug.Log("[KICK RPC] Server approved kick — disconnecting.");
                if (PhotonNetwork.IsConnected)
                    PhotonNetwork.Disconnect();
            }
            else
            {
                Debug.LogWarning("[KICK RPC] Server rejected kick: " + json);
            }
        },
        error =>
        {
            _kickVerifyPending = false;
            Debug.LogError("[KICK RPC] Cloud Script error: " + error.GenerateErrorReport());
        });
    }

    public void Report(int index)
    {
        if (playfablogin == null)
            return;

        if (Time.time - _lastReportTime < ReportCooldown)
        {
            Debug.LogWarning("[REPORT] Cooldown active — wait before reporting again.");
            return;
        }

        Player[] players = PhotonNetwork.PlayerList;
        if (players == null || index < 0 || index >= players.Length)
            return;

        Player target = players[index];
        if (target == null)
            return;

        if (_pvrByActor.TryGetValue(target.ActorNumber, out PhotonVRPlayer pvrp) && pvrp != null)
        {
            PhotonView view = pvrp.GetComponent<PhotonView>();
            if (view == null || view.Owner == null)
                return;

            var props = view.Owner.CustomProperties;

            string reportedName = target.NickName;
            string reportedPlayfab = props.TryGetValue("PlayfabID", out object pid) ? pid?.ToString() ?? "Unknown" : "Unknown";
            string reportedColor = props.TryGetValue("Colour", out object col) ? col?.ToString() ?? "Unknown" : "Unknown";
            string reportedCosmetic = props.TryGetValue("Cosmetic", out object cos) ? cos?.ToString() ?? "Unknown" : "Unknown";
            string reportedRole = props.TryGetValue("Role", out object ro) ? ro?.ToString() ?? "Unknown" : "Unknown";

            string reporterName = PlayerPrefs.GetString("Username", "Unknown");
            string reporterPlayfab = playfablogin.MyPlayFabID;

            if (reportedPlayfab != "Unknown")
                IncrementReportCount(reportedPlayfab, reportedName);

            _lastReportTime = Time.time;

            StartCoroutine(CaptureAndReport(
                reportedName, reportedPlayfab, reportedColor,
                reportedCosmetic, reportedRole,
                reporterName, reporterPlayfab));
        }
    }

    private void IncrementReportCount(string targetPlayfabId, string targetName)
    {
        PlayFabClientAPI.ExecuteCloudScript(new ExecuteCloudScriptRequest
        {
            FunctionName = "IncrementReportAndAutoBan",
            FunctionParameter = new
            {
                targetId = targetPlayfabId,
                targetName = targetName,
                reporterId = playfablogin != null ? playfablogin.MyPlayFabID : string.Empty
            },
            GeneratePlayStreamEvent = true
        },
        result =>
        {
            if (result.FunctionResult == null)
                return;

            string json = result.FunctionResult.ToString();
            Debug.Log("[REPORT] Cloud Script result: " + json);

            if (json.Contains("\"banned\":true"))
                Debug.Log("[REPORT] Target auto-banned by server.");
        },
        error => Debug.LogError("[REPORT] Cloud Script error: " + error.GenerateErrorReport()));
    }

    private IEnumerator CaptureAndReport(
        string reportedName, string reportedPlayfab,
        string reportedColor, string reportedCosmetic, string reportedRole,
        string reporterName, string reporterPlayfab)
    {
        float[] audioData = Array.Empty<float>();

        if (photonRecorder != null && photonRecorder.MicrophoneDevice != null)
        {
            string micDevice = photonRecorder.MicrophoneDevice.Name;
            AudioClip snapClip = Microphone.Start(micDevice, false, bufferSeconds, _sampleRate);

            yield return _reportDelay;

            if (snapClip != null)
            {
                audioData = new float[snapClip.samples];
                snapClip.GetData(audioData, 0);
            }

            Microphone.End(micDevice);
        }

        yield return StartCoroutine(PostReportToDiscord(
            reportedName, reportedPlayfab, reportedColor,
            reportedCosmetic, reportedRole,
            reporterName, reporterPlayfab,
            audioData));
    }

    private IEnumerator PostReportToDiscord(
        string reportedName, string reportedPlayfab,
        string reportedColor, string reportedCosmetic, string reportedRole,
        string reporterName, string reporterPlayfab,
        float[] audioData)
    {
        byte[] wavBytes = audioData != null && audioData.Length > 0
            ? FloatArrayToWav(audioData, _sampleRate)
            : null;

        int embedColor = RedWebhookName ? 16711680 : 0;

        DiscordWebhookPayload payload = new DiscordWebhookPayload
        {
            username = WebhookUsername,
            avatar_url = WebhookAvatarURL
        };

        DiscordEmbed embed = new DiscordEmbed
        {
            title = "Player Report",
            color = embedColor
        };

        embed.fields.Add(new DiscordField { name = "Reported", value = Escape(reportedName), inline = true });
        embed.fields.Add(new DiscordField { name = "Role", value = Escape(reportedRole), inline = true });
        embed.fields.Add(new DiscordField { name = "PlayFab ID", value = Escape(reportedPlayfab), inline = false });
        embed.fields.Add(new DiscordField { name = "Color", value = Escape(reportedColor), inline = true });
        embed.fields.Add(new DiscordField { name = "Cosmetic", value = Escape(reportedCosmetic), inline = true });
        embed.fields.Add(new DiscordField { name = "Reporter", value = Escape(reporterName), inline = true });
        embed.fields.Add(new DiscordField { name = "Reporter ID", value = Escape(reporterPlayfab), inline = true });

        payload.embeds.Add(embed);

        string embedJson = JsonUtility.ToJson(payload);

        if (wavBytes != null && wavBytes.Length > 0)
        {
            List<IMultipartFormSection> form = new List<IMultipartFormSection>
            {
                new MultipartFormDataSection("payload_json", embedJson, "application/json"),
                new MultipartFormFileSection("files[0]", wavBytes, "audio_report.wav", "audio/wav")
            };

            using (UnityWebRequest req = UnityWebRequest.Post(WebHookURL, form))
            {
                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                    Debug.LogError("[WEBHOOK] Audio upload failed: " + req.error);
            }
        }
        else
        {
            using (UnityWebRequest req = new UnityWebRequest(WebHookURL, "POST"))
            {
                byte[] body = System.Text.Encoding.UTF8.GetBytes(embedJson);
                req.uploadHandler = new UploadHandlerRaw(body);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");

                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                    Debug.LogError("[WEBHOOK] Report failed: " + req.error);
            }
        }
    }

    private byte[] FloatArrayToWav(float[] samples, int sampleRate)
    {
        int channels = 1;
        int bitDepth = 16;
        int byteRate = sampleRate * channels * (bitDepth / 8);
        int dataSize = samples.Length * (bitDepth / 8);
        int totalSize = 44 + dataSize;

        byte[] wav = new byte[totalSize];
        int offset = 0;

        WriteString(wav, offset, "RIFF"); offset += 4;
        WriteInt32(wav, offset, totalSize - 8); offset += 4;
        WriteString(wav, offset, "WAVE"); offset += 4;
        WriteString(wav, offset, "fmt "); offset += 4;
        WriteInt32(wav, offset, 16); offset += 4;
        WriteInt16(wav, offset, 1); offset += 2;
        WriteInt16(wav, offset, (short)channels); offset += 2;
        WriteInt32(wav, offset, sampleRate); offset += 4;
        WriteInt32(wav, offset, byteRate); offset += 4;
        WriteInt16(wav, offset, (short)(channels * bitDepth / 8)); offset += 2;
        WriteInt16(wav, offset, (short)bitDepth); offset += 2;
        WriteString(wav, offset, "data"); offset += 4;
        WriteInt32(wav, offset, dataSize); offset += 4;

        for (int i = 0; i < samples.Length; i++)
        {
            short val = (short)(Mathf.Clamp(samples[i], -1f, 1f) * short.MaxValue);
            wav[offset++] = (byte)(val & 0xFF);
            wav[offset++] = (byte)((val >> 8) & 0xFF);
        }

        return wav;
    }

    private void WriteString(byte[] buf, int offset, string s)
    {
        for (int i = 0; i < s.Length; i++)
            buf[offset + i] = (byte)s[i];
    }

    private void WriteInt32(byte[] buf, int offset, int val)
    {
        buf[offset] = (byte)(val);
        buf[offset + 1] = (byte)(val >> 8);
        buf[offset + 2] = (byte)(val >> 16);
        buf[offset + 3] = (byte)(val >> 24);
    }

    private void WriteInt16(byte[] buf, int offset, short val)
    {
        buf[offset] = (byte)(val);
        buf[offset + 1] = (byte)(val >> 8);
    }

    private string Escape(string s)
    {
        return string.IsNullOrEmpty(s)
            ? ""
            : s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
    }
}