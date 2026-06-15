using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using PlayFab;
using PlayFab.ClientModels;
using TMPro;
using Photon.Pun;
using Photon.Realtime;
using ExitGames.Client.Photon;
using Hashtable = ExitGames.Client.Photon.Hashtable;

public class Playfablogin : MonoBehaviourPunCallbacks
{
    public static Playfablogin instance;

    [Header("IDENTITY")]
    public string MyPlayFabID;
    public string CatalogName;

    [Header("UI ITEMS")]
    public List<GameObject> specialitems = new List<GameObject>();
    public List<GameObject> disableitems = new List<GameObject>();

    [Header("CURRENCY")]
    public string CurrencyName = "NM";
    public TMP_Text currencyText;
    public int coins;

    [Header("MOTD")]
    public TMP_Text MOTDText;

    [Header("BANNED")]
    public string bannedscenename;

    [Header("VERSION")]
    [SerializeField] private bool blockOldQuestVersions = true;

    [Header("WHITELIST RULES")]
    [Tooltip("OFF = unknown cosmetics allowed. ON = block everything not in the list.")]
    public bool strictWhitelist = false;
    public List<CosmeticRule> cosmeticRules = new List<CosmeticRule>();

    [Header("COSMETIC SCANNER")]
    [Tooltip("Exact names of cosmetic parent GameObjects inside the player prefab")]
    public List<string> cosmeticRootNames = new List<string> { "HeadCosmetics", "FaceCosmetics", "BodyCosmetics" };
    public List<CosmeticScanRule> scanRules = new List<CosmeticScanRule>();

    [Header("SCAN PERFORMANCE")]
    [Tooltip("How often the big scan runs, in seconds.")]
    [SerializeField] private float periodicScanInterval = 60f;

    [Tooltip("How many child objects to process before yielding during a scan.")]
    [SerializeField] private int childrenPerYield = 16;

    public string violationWebhookURL;

    public static bool IsMod { get; private set; }
    public static bool IsAdmin { get; private set; }
    public static bool IsOwner { get; private set; }
    public static bool IsManager { get; private set; }
    public static bool IsYT { get; private set; }
    public static string LocalRole { get; private set; } = "Player";

    public static Dictionary<string, string> EquippedCosmetics = new Dictionary<string, string>();
    public static HashSet<string> GrantedItems = new HashSet<string>();
    public static bool AntiCheatReady { get; private set; }

    [Serializable]
    public class CosmeticRule
    {
        public string cosmeticName;
        public string requiredInventoryItem;
    }

    [Serializable]
    public class CosmeticScanRule
    {
        [Tooltip("Exact name of the child GameObject under the cosmetic root")]
        public string gameObjectName;

        [Tooltip("Exact PlayFab catalog item ID required to own this cosmetic")]
        public string requiredPlayFabItemID;
    }

    [Serializable]
    private class DiscordWebhookPayload
    {
        public List<DiscordEmbed> embeds = new List<DiscordEmbed>();
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

    private readonly List<Transform> _roots = new List<Transform>();
    private readonly Dictionary<string, CosmeticScanRule> _scanLookup = new Dictionary<string, CosmeticScanRule>();
    private readonly Dictionary<string, CosmeticRule> _cosmeticLookup = new Dictionary<string, CosmeticRule>();

    private GetUserInventoryResult _cachedInventory;
    private GetAccountInfoResult _cachedAccount;

    private bool _snapshotReady;
    private bool _inventoryRequestInProgress;
    private bool _currencyRequestInProgress;
    private bool _scanRequested;
    private Coroutine _scanLoopRoutine;

    private void Awake()
    {
        instance = this;
        BuildLookups();
    }

    private void Start()
    {
        CheckQuestVersion();
        Login();
    }

    private void OnDisable()
    {
        StopScanLoop();
    }

    private void OnDestroy()
    {
        StopScanLoop();
    }

    public override void OnJoinedRoom()
    {
        if (_snapshotReady)
            BroadcastRoleToPhoton();
    }

    private void Login()
    {
        PlayFabClientAPI.LoginWithCustomID(
            new LoginWithCustomIDRequest
            {
                CustomId = SystemInfo.deviceUniqueIdentifier,
                CreateAccount = true
            },
            OnLoginSuccess,
            OnError
        );
    }

    private void OnLoginSuccess(LoginResult result)
    {
        MyPlayFabID = result.PlayFabId;
        PlayFabClientAPI.GetAccountInfo(new GetAccountInfoRequest(), OnAccountInfo, OnError);
        GetMOTD();
    }

    private void OnAccountInfo(GetAccountInfoResult result)
    {
        _cachedAccount = result;

        if (result?.AccountInfo != null)
            MyPlayFabID = result.AccountInfo.PlayFabId;

        if (_inventoryRequestInProgress)
            return;

        _inventoryRequestInProgress = true;
        PlayFabClientAPI.GetUserInventory(
            new GetUserInventoryRequest(),
            inv =>
            {
                _inventoryRequestInProgress = false;
                OnInventorySnapshot(inv);
            },
            error =>
            {
                _inventoryRequestInProgress = false;
                OnError(error);
            }
        );
    }

    private void OnInventorySnapshot(GetUserInventoryResult result)
    {
        _cachedInventory = result;
        GrantedItems.Clear();
        ResetRoles();

        if (result?.Inventory != null)
        {
            foreach (var item in result.Inventory)
            {
                if (item.CatalogVersion != CatalogName)
                    continue;

                GrantedItems.Add(item.ItemId);

                switch (item.ItemId)
                {
                    case "Mod":
                        IsMod = true;
                        break;
                    case "Admin":
                        IsAdmin = true;
                        break;
                    case "Owner":
                        IsOwner = true;
                        break;
                    case "Manager":
                        IsManager = true;
                        break;
                    case "YT":
                        IsYT = true;
                        break;
                }
            }
        }

        LocalRole = IsOwner ? "Owner" :
                    IsAdmin ? "Admin" :
                    IsManager ? "Manager" :
                    IsMod ? "Mod" :
                    IsYT ? "YT" : "Player";

        if (result?.VirtualCurrency != null && result.VirtualCurrency.ContainsKey("NM"))
        {
            coins = result.VirtualCurrency["NM"];
            if (currencyText != null)
                currencyText.text = coins + " " + CurrencyName;
        }

        ApplySpecialItems();
        BroadcastRoleToPhoton();
        FindCosmeticRootsOnce();
        BuildLookups();

        AntiCheatReady = true;
        _snapshotReady = true;

        RequestScan();
        StartScanLoop();
    }

    private void ResetRoles()
    {
        IsMod = false;
        IsAdmin = false;
        IsOwner = false;
        IsManager = false;
        IsYT = false;
    }

    private void ApplySpecialItems()
    {
        for (int i = 0; i < specialitems.Count; i++)
        {
            GameObject item = specialitems[i];
            if (item == null) continue;

            bool shouldBeActive = GrantedItems.Contains(item.name);
            if (item.activeSelf != shouldBeActive)
                item.SetActive(shouldBeActive);
        }

        for (int i = 0; i < disableitems.Count; i++)
        {
            GameObject item = disableitems[i];
            if (item == null) continue;

            if (item.activeSelf)
                item.SetActive(false);
        }
    }

    private void BroadcastRoleToPhoton()
    {
        if (!PhotonNetwork.IsConnected || PhotonNetwork.LocalPlayer == null)
            return;

        Hashtable props = new Hashtable
        {
            { "PlayfabID", MyPlayFabID },
            { "Role", LocalRole }
        };

        PhotonNetwork.LocalPlayer.SetCustomProperties(props);
    }

    private void FindCosmeticRootsOnce()
    {
        _roots.Clear();

        for (int i = 0; i < cosmeticRootNames.Count; i++)
        {
            string rootName = cosmeticRootNames[i];
            if (string.IsNullOrEmpty(rootName))
                continue;

            GameObject found = GameObject.Find(rootName);
            if (found == null)
                continue;

            _roots.Add(found.transform);

            CosmeticHierarchyWatcher watcher = found.GetComponent<CosmeticHierarchyWatcher>();
            if (watcher == null)
                watcher = found.AddComponent<CosmeticHierarchyWatcher>();

            watcher.manager = this;
        }
    }

    private void BuildLookups()
    {
        _scanLookup.Clear();
        _cosmeticLookup.Clear();

        foreach (var r in scanRules)
        {
            if (r == null || string.IsNullOrEmpty(r.gameObjectName))
                continue;

            _scanLookup[r.gameObjectName] = r;
        }

        foreach (var r in cosmeticRules)
        {
            if (r == null || string.IsNullOrEmpty(r.cosmeticName))
                continue;

            _cosmeticLookup[r.cosmeticName] = r;
        }
    }

    public void RequestScan()
    {
        if (!AntiCheatReady || !_snapshotReady)
            return;

        _scanRequested = true;
    }

    private void StartScanLoop()
    {
        if (_scanLoopRoutine != null)
            return;

        _scanLoopRoutine = StartCoroutine(ScanLoop());
    }

    private void StopScanLoop()
    {
        if (_scanLoopRoutine != null)
        {
            StopCoroutine(_scanLoopRoutine);
            _scanLoopRoutine = null;
        }
    }

    private IEnumerator ScanLoop()
    {
        while (_snapshotReady)
        {
            yield return RunFullValidationScan();

            _scanRequested = false;

            float elapsed = 0f;
            while (elapsed < periodicScanInterval && !_scanRequested && _snapshotReady)
            {
                elapsed += 1f;
                yield return new WaitForSecondsRealtime(1f);
            }
        }

        _scanLoopRoutine = null;
    }

    private IEnumerator RunFullValidationScan()
    {
        ValidateCosmetics();
        yield return ScanHierarchy();
    }

    private void ValidateCosmetics()
    {
        foreach (var kvp in EquippedCosmetics)
        {
            string cosmetic = kvp.Value;
            if (string.IsNullOrEmpty(cosmetic))
                continue;

            if (!_cosmeticLookup.TryGetValue(cosmetic, out CosmeticRule rule))
            {
                if (strictWhitelist)
                {
                    AntiCheatFail("Unknown cosmetic: " + cosmetic);
                    return;
                }

                continue;
            }

            if (!string.IsNullOrEmpty(rule.requiredInventoryItem) && !GrantedItems.Contains(rule.requiredInventoryItem))
            {
                AntiCheatFail("Blocked cosmetic: " + cosmetic);
                return;
            }
        }
    }

    private IEnumerator ScanHierarchy()
    {
        int processed = 0;

        for (int r = 0; r < _roots.Count; r++)
        {
            Transform root = _roots[r];
            if (root == null)
                continue;

            int childCount = root.childCount;

            for (int i = 0; i < childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (child == null || !child.gameObject.activeSelf)
                    continue;

                if (_scanLookup.TryGetValue(child.name, out CosmeticScanRule rule))
                {
                    if (!GrantedItems.Contains(rule.requiredPlayFabItemID))
                    {
                        string playerName = PlayerPrefs.GetString("Username", "Unknown");
                        Debug.LogError("[SCANNER] VIOLATION — " + child.name + " active but '" + rule.requiredPlayFabItemID + "' not owned by " + MyPlayFabID);

                        if (!string.IsNullOrEmpty(violationWebhookURL))
                        {
                            yield return StartCoroutine(SendViolationWebhook(
                                playerName,
                                MyPlayFabID,
                                child.name,
                                rule.requiredPlayFabItemID
                            ));
                        }

                        AntiCheatFail("Cosmetic violation: " + child.name);
                        yield break;
                    }
                }

                processed++;
                if (processed >= childrenPerYield)
                {
                    processed = 0;
                    yield return null;
                }
            }
        }
    }

    public void SetEquippedCosmetic(string category, string cosmetic)
    {
        EquippedCosmetics[category] = cosmetic;
        RequestScan();
    }

    public bool IsCosmeticAllowed(string cosmeticName)
    {
        if (!AntiCheatReady)
            return false;

        if (_cosmeticLookup.TryGetValue(cosmeticName, out CosmeticRule rule))
        {
            if (string.IsNullOrEmpty(rule.requiredInventoryItem))
                return true;

            return GrantedItems.Contains(rule.requiredInventoryItem);
        }

        return !strictWhitelist;
    }

    public void RefreshRole()
    {
        RefreshSnapshot();
    }

    public void RefreshSnapshot()
    {
        if (_inventoryRequestInProgress)
            return;

        _inventoryRequestInProgress = true;
        PlayFabClientAPI.GetUserInventory(
            new GetUserInventoryRequest(),
            inv =>
            {
                _inventoryRequestInProgress = false;
                OnInventorySnapshot(inv);
            },
            error =>
            {
                _inventoryRequestInProgress = false;
                OnError(error);
            }
        );
    }

    public void GetVirtualCurrencies()
    {
        if (_currencyRequestInProgress)
            return;

        _currencyRequestInProgress = true;
        PlayFabClientAPI.GetUserInventory(
            new GetUserInventoryRequest(),
            result =>
            {
                _currencyRequestInProgress = false;
                OnCurrency(result);
            },
            error =>
            {
                _currencyRequestInProgress = false;
                OnError(error);
            }
        );
    }

    private void OnCurrency(GetUserInventoryResult result)
    {
        if (result?.VirtualCurrency != null && result.VirtualCurrency.ContainsKey("NM"))
        {
            coins = result.VirtualCurrency["NM"];
            if (currencyText != null)
                currencyText.text = coins + " " + CurrencyName;
        }
    }

    public void GetMOTD()
    {
        PlayFabClientAPI.GetTitleData(new GetTitleDataRequest(), OnMOTD, OnError);
    }

    public void OnMOTD(GetTitleDataResult result)
    {
        if (result == null || result.Data == null || !result.Data.ContainsKey("MOTD"))
            return;

        if (MOTDText != null)
            MOTDText.text = result.Data["MOTD"];
    }

    public void AntiCheatFail(string reason)
    {
        Debug.LogError("[ANTICHEAT FAIL] " + reason);

#if UNITY_EDITOR
        UnityEditor.EditorApplication.ExitPlaymode();
#else
        UnityEngine.Diagnostics.Utils.ForceCrash(UnityEngine.Diagnostics.ForcedCrashCategory.FatalError);
#endif
    }

    private IEnumerator SendViolationWebhook(string playerName, string playFabID, string cosmeticName, string requiredItem)
    {
        if (string.IsNullOrEmpty(violationWebhookURL))
            yield break;

        DiscordWebhookPayload payload = new DiscordWebhookPayload();
        DiscordEmbed embed = new DiscordEmbed
        {
            title = "Cosmetic Violation",
            color = 16711680
        };

        embed.fields.Add(new DiscordField { name = "Player", value = Escape(playerName), inline = true });
        embed.fields.Add(new DiscordField { name = "PlayFab ID", value = Escape(playFabID), inline = true });
        embed.fields.Add(new DiscordField { name = "Illegal Cosmetic", value = Escape(cosmeticName), inline = true });
        embed.fields.Add(new DiscordField { name = "Required Item", value = Escape(requiredItem), inline = true });
        embed.fields.Add(new DiscordField { name = "Time", value = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + " UTC", inline = false });

        payload.embeds.Add(embed);

        string json = JsonUtility.ToJson(payload);

        using (UnityWebRequest req = new UnityWebRequest(violationWebhookURL, "POST"))
        {
            byte[] body = System.Text.Encoding.UTF8.GetBytes(json);
            req.uploadHandler = new UploadHandlerRaw(body);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");

            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
                Debug.LogError("[SCANNER] Webhook failed: " + req.error);
        }
    }

    private void CheckQuestVersion()
    {
        if (!blockOldQuestVersions)
            return;

        string osVersion = SystemInfo.operatingSystem;
        Debug.Log("[VERSION CHECK] OS: " + osVersion);

        if (osVersion.Contains("v78") || osVersion.Contains("v79") || osVersion.Contains("/78.") || osVersion.Contains("/79."))
        {
            Debug.LogError("[SECURITY] Blocked Quest version: " + osVersion);

#if UNITY_EDITOR
            UnityEditor.EditorApplication.ExitPlaymode();
#else
            Application.Quit();
#endif
        }
    }

    public static bool IsStaff()
    {
        return IsMod || IsAdmin || IsOwner || IsManager;
    }

    public static bool CanKick()
    {
        return IsMod || IsAdmin || IsOwner || IsManager;
    }

    public static bool CanKickRole(string targetRole)
    {
        return RoleLevel(LocalRole) > RoleLevel(targetRole);
    }

    public static int RoleLevel(string role)
    {
        switch (role)
        {
            case "Owner": return 5;
            case "Admin": return 4;
            case "Manager": return 3;
            case "Mod": return 2;
            case "YT": return 1;
            default: return 0;
        }
    }

    public void OnError(PlayFabError error)
    {
        Debug.LogError("[PlayFab] Error: " + error.GenerateErrorReport());

        if (error.Error == PlayFabErrorCode.AccountBanned && !string.IsNullOrEmpty(bannedscenename))
            SceneManager.LoadScene(bannedscenename);
    }

    private string Escape(string s)
    {
        return string.IsNullOrEmpty(s) ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}

public class CosmeticHierarchyWatcher : MonoBehaviour
{
    public Playfablogin manager;

    private void OnTransformChildrenChanged()
    {
        if (manager != null)
            manager.RequestScan();
    }

    private void OnTransformParentChanged()
    {
        if (manager != null)
            manager.RequestScan();
    }

    private void OnEnable()
    {
        if (manager != null)
            manager.RequestScan();
    }
}