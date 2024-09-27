using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditorInternal;
using VRC.Core;
using VRC.SDK3.Avatars;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3A.Editor;
using VRC.SDKBase.Editor;
using VRC.SDKBase.Editor.Api;
using VRC.SDKBase.Editor.BuildPipeline;
using PeanutTools_VRC_Bulk_Upload;

public class VRC_Bulk_Upload : EditorWindow {
    enum State {
        Idle,
        Building,
        Uploading,
        Success,
        Failed
    }

    enum Action {
        None,
        Build,
        BuildAndTest,
        BuildAndUpload
    }

    struct AvatarState {
        public Action action;
        public State state;
        public SdkBuildState? buildState;
        public SdkUploadState? uploadState;
        public System.Exception exception;
    }

    Vector2 scrollPosition;
    static Dictionary<string, AvatarState> avatarStates = new Dictionary<string, AvatarState>();
    static CancellationTokenSource GetAvatarCancellationToken = new CancellationTokenSource();
    static CancellationTokenSource BuildAndUploadCancellationToken = new CancellationTokenSource();
    static VRCAvatarDescriptor currentVrcAvatarDescriptor;

    const string textInNameForQuest = "[Quest]";
    static bool onlyAllowQuestOnAndroid = true;

    static bool showLogs = false;
    Vector2 logsScrollPosition;
    const string logPath = "Assets/PeanutTools/debug.log";
    string lastLogsContents;

    [MenuItem("PeanutTools/VRC Bulk Upload")]
    public static void ShowWindow() {
        var window = GetWindow<VRC_Bulk_Upload>();
        window.titleContent = new GUIContent("VRC Bulk Upload");
        window.minSize = new Vector2(400, 200);
    }

    void OnGUI() {
        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

        CustomGUI.LargeLabel("VRC Bulk Upload");
        CustomGUI.ItalicLabel("Bulk uploads all active VRChat avatars in your scene.");

        if (APIUser.CurrentUser == null) {
            CustomGUI.RenderErrorMessage("You must open the VRC SDK control panel first");
            EditorGUILayout.EndScrollView();
            return;
        }

        CustomGUI.LineGap();

        CustomGUI.LargeLabel("Settings");

        onlyAllowQuestOnAndroid = CustomGUI.Checkbox($"Filter Quest avatars", onlyAllowQuestOnAndroid);
        CustomGUI.ItalicLabel($"Ignore avatars named {textInNameForQuest} on Windows and vice-versa.");
        
        CustomGUI.LineGap();
        
        CustomGUI.LargeLabel("Avatars");

        CustomGUI.LineGap();

        RenderAllAvatarsAndInScene();

        CustomGUI.LineGap();
        
        GUILayout.BeginHorizontal();

        int count = GetVrchatAvatarsToProcess().Length;

        if (CustomGUI.StandardButton($"Build All ({count})")) {
            BuildAllAvatars();
        }

        if (CustomGUI.StandardButton($"Build And Test All ({count})")) {
            BuildAndTestAllAvatars();
        }
        
        GUILayout.EndHorizontal();
        
        CustomGUI.LineGap();

        if (CustomGUI.PrimaryButton($"Build And Upload All ({count})")) {
            BuildAndUploadAllAvatars();
        }

        CustomGUI.LineGap();

        if (CustomGUI.StandardButton("Reset Statuses")) {
            ResetAvatars();
        }

        if (CustomGUI.StandardButton($"Switch To {(EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android ? "Windows" : "Android")}")) {
            SwitchPlatform();
        }

        CustomGUI.LineGap();

        RenderLogs();

        EditorGUILayout.EndScrollView();
    }

    void SwitchPlatform() {
        Debug.Log($"VRC_Bulk_Upload :: Switching platform...");

        RecordLog("Switch platform");

        if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android) {
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows);
        } else {
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
        }
    }

// LOGGING

    void RenderLogs() {
        if (File.Exists(logPath)) {
            string[] logLines = File.ReadAllLines(logPath);
    
            System.Array.Reverse(logLines);
            
            lastLogsContents = string.Join(System.Environment.NewLine, logLines);
        } else {
            lastLogsContents = "";
        }

        EditorGUI.BeginDisabledGroup(true);

        logsScrollPosition = EditorGUILayout.BeginScrollView(logsScrollPosition, GUILayout.Height(300));
        EditorGUILayout.TextArea(lastLogsContents, GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(true), GUILayout.MinHeight(100));
        EditorGUILayout.EndScrollView();
        
        EditorGUI.EndDisabledGroup();
    }

    static void RecordLog(string message) {
        string timestamp = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string logMessage = $"{timestamp} - {message}";

        File.AppendAllText(logPath, logMessage + System.Environment.NewLine);
    }

// HELPERS

    async Task<VRCAvatar> GetVRCAvatarFromDescriptor(VRCAvatarDescriptor vrcAvatarDescriptor) {
        var blueprintId = vrcAvatarDescriptor.GetComponent<PipelineManager>().blueprintId;

        Debug.Log($"VRC_Bulk_Upload :: Fetching avatar for '{vrcAvatarDescriptor.gameObject.name}' ('{blueprintId}')...");

        var avatarData = await VRCApi.GetAvatar(blueprintId, true, cancellationToken: GetAvatarCancellationToken.Token);

        return avatarData;
    }

    async Task BuildAllAvatars() {
        var activeVrchatAvatars = GetVrchatAvatarsToProcess();

        Debug.Log($"VRC_Bulk_Upload :: Building {activeVrchatAvatars.Length} VRChat avatars...");

        foreach (var activeVrchatAvatar in activeVrchatAvatars) {
            await BuildAvatar(activeVrchatAvatar);
        }
    }

    async Task BuildAndTestAllAvatars() {
        var activeVrchatAvatars = GetVrchatAvatarsToProcess();

        Debug.Log($"VRC_Bulk_Upload :: Building and testing {activeVrchatAvatars.Length} VRChat avatars...");

        foreach (var activeVrchatAvatar in activeVrchatAvatars) {
            await BuildAndTestAvatar(activeVrchatAvatar);
        }
    }

    async Task BuildAndUploadAllAvatars() {
        var activeVrchatAvatars = GetVrchatAvatarsToProcess();

        Debug.Log($"VRC_Bulk_Upload :: Building and uploading {activeVrchatAvatars.Length} VRChat avatars...");

        foreach (var activeVrchatAvatar in activeVrchatAvatars) {
            await BuildAndUploadAvatar(activeVrchatAvatar);
        }
    }

    async Task BuildAvatar(VRCAvatarDescriptor vrcAvatarDescriptor) {
        Debug.Log($"VRC_Bulk_Upload :: Building '{vrcAvatarDescriptor.gameObject.name}'...");

        RecordLog($"Build '{vrcAvatarDescriptor.gameObject.name}'...");
        
        try {
            if (!VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out var builder)) {
                throw new System.Exception("No builder found");
            }

            currentVrcAvatarDescriptor = vrcAvatarDescriptor;
            SetAvatarAction(vrcAvatarDescriptor, Action.Build);

            Repaint();

            string bundlePath = await builder.Build(vrcAvatarDescriptor.gameObject);
            
            Debug.Log($"VRC_Bulk_Upload :: '{vrcAvatarDescriptor.gameObject.name}' built to '{bundlePath}'");
            
            SetAvatarState(vrcAvatarDescriptor, State.Success);
            
            RecordLog($"Build '{vrcAvatarDescriptor.gameObject.name}' success");
        } catch (System.Exception e) {
            SetAvatarState(vrcAvatarDescriptor, State.Failed, e);
            Debug.LogError(e);
            RecordLog($"Build '{vrcAvatarDescriptor.gameObject.name}' failed");
        }
    }

    async Task BuildAndUploadAvatar(VRCAvatarDescriptor vrcAvatarDescriptor) {
        Debug.Log($"VRC_Bulk_Upload :: Building and uploading '{vrcAvatarDescriptor.gameObject.name}'...");

        RecordLog($"Build and upload '{vrcAvatarDescriptor.gameObject.name}'...");

        try {
            if (!VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out var builder)) {
                throw new System.Exception("No builder found");
            }

            currentVrcAvatarDescriptor = vrcAvatarDescriptor;
            SetAvatarAction(vrcAvatarDescriptor, Action.BuildAndUpload);

            Repaint();

            var vrcAvatar = await GetVRCAvatarFromDescriptor(vrcAvatarDescriptor);

            await builder.BuildAndUpload(vrcAvatarDescriptor.gameObject, vrcAvatar, cancellationToken: BuildAndUploadCancellationToken.Token);
            
            SetAvatarState(vrcAvatarDescriptor, State.Success);
            
            RecordLog($"Build and upload '{vrcAvatarDescriptor.gameObject.name}' success");
        } catch (System.Exception e) {
            SetAvatarState(vrcAvatarDescriptor, State.Failed, e);
            Debug.LogError(e);
            RecordLog($"Build and upload '{vrcAvatarDescriptor.gameObject.name}' failed");
        }
    }

    async Task BuildAndTestAvatar(VRCAvatarDescriptor vrcAvatarDescriptor) {
        Debug.Log($"VRC_Bulk_Upload :: Building and testing '{vrcAvatarDescriptor.gameObject.name}'...");
        
        RecordLog($"Build and test '{vrcAvatarDescriptor.gameObject.name}'...");

        try {
            if (!VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out var builder)) {
                throw new System.Exception("No builder found");
            }

            currentVrcAvatarDescriptor = vrcAvatarDescriptor;
            SetAvatarAction(vrcAvatarDescriptor, Action.BuildAndTest);

            Repaint();

            await builder.BuildAndTest(vrcAvatarDescriptor.gameObject);

            SetAvatarState(vrcAvatarDescriptor, State.Success);
            
            RecordLog($"Build and test '{vrcAvatarDescriptor.gameObject.name}' success");
        } catch (System.Exception e) {
            SetAvatarState(vrcAvatarDescriptor, State.Failed, e);
            Debug.LogError(e);
            RecordLog($"Build and test '{vrcAvatarDescriptor.gameObject.name}' failed");
        }
    }

    void ResetAvatars() {
        Debug.Log("VRC_Bulk_Upload :: Reset avatars");

        foreach (var key in avatarStates.Keys.ToList())
        {
            avatarStates[key] = new AvatarState()
            {
                state = State.Idle
            };
        }

        RecordLog("Reset avatars");
    }

    VRCAvatarDescriptor[] GetVrchatAvatarsToProcess() {
        GameObject[] rootObjects = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
        
        var vrcAvatarDescriptors = new List<VRCAvatarDescriptor>();

        foreach (var rootObject in rootObjects) {
            VRCAvatarDescriptor vrcAvatarDescriptor = rootObject.GetComponent<VRCAvatarDescriptor>();

            if (!GetCanAvatarBeBuilt(vrcAvatarDescriptor)) {
                continue;
            }

            vrcAvatarDescriptors.Add(vrcAvatarDescriptor);
        }

        return vrcAvatarDescriptors.ToArray();
    }

    bool GetIsAvatarForMyPlatform(VRCAvatarDescriptor vrcAvatarDescriptor) {
        return (
            vrcAvatarDescriptor.gameObject.name.Contains(textInNameForQuest) && EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android
            ||
            !vrcAvatarDescriptor.gameObject.name.Contains(textInNameForQuest) && EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android
        );
    }

    bool GetCanAvatarBeBuilt(VRCAvatarDescriptor vrcAvatarDescriptor) {
        return vrcAvatarDescriptor != null && vrcAvatarDescriptor.gameObject.GetComponent<Animator>() != null && vrcAvatarDescriptor.gameObject.activeSelf && ((onlyAllowQuestOnAndroid && GetIsAvatarForMyPlatform(vrcAvatarDescriptor)) || !onlyAllowQuestOnAndroid);
    }

    bool GetCanAvatarBeUploaded(VRCAvatarDescriptor vrcAvatarDescriptor) {
        return GetCanAvatarBeBuilt(vrcAvatarDescriptor) && vrcAvatarDescriptor.gameObject.GetComponent<PipelineManager>().blueprintId != null && vrcAvatarDescriptor.gameObject.activeSelf && ((onlyAllowQuestOnAndroid && GetIsAvatarForMyPlatform(vrcAvatarDescriptor)) || !onlyAllowQuestOnAndroid);
    }

    static AvatarState GetAvatarRootState(VRCAvatarDescriptor vrcAvatarDescriptor) {
        if (!avatarStates.ContainsKey(vrcAvatarDescriptor.gameObject.name)) {
            Debug.Log("State no exist, creating...");
            avatarStates[vrcAvatarDescriptor.gameObject.name] = new AvatarState() {
                state = State.Idle
            };
        }
        return avatarStates[vrcAvatarDescriptor.gameObject.name];
    }

    static void SetAvatarRootState(VRCAvatarDescriptor vrcAvatarDescriptor, AvatarState newRootState) {
        avatarStates[vrcAvatarDescriptor.gameObject.name] = newRootState;
    }

    static void SetAvatarAction(VRCAvatarDescriptor vrcAvatarDescriptor, Action newAction) {
        var existingState = GetAvatarRootState(vrcAvatarDescriptor);

        Debug.Log($"VRC_Bulk_Upload :: Action '{currentVrcAvatarDescriptor.gameObject.name}' '{existingState.action}' => '{newAction}'");

        existingState.action = newAction;

        SetAvatarRootState(vrcAvatarDescriptor, existingState);
    }

    static void SetAvatarState(VRCAvatarDescriptor vrcAvatarDescriptor, State newState, System.Exception exception = null) {
        var existingState = GetAvatarRootState(vrcAvatarDescriptor);

        Debug.Log($"VRC_Bulk_Upload :: State '{currentVrcAvatarDescriptor.gameObject.name}' '{existingState.state}' => '{newState}'");

        existingState.state = newState;
        existingState.exception = exception;
        
        SetAvatarRootState(vrcAvatarDescriptor, existingState);
    }

    static void SetAvatarBuildState(VRCAvatarDescriptor vrcAvatarDescriptor, SdkBuildState? newBuildState) {
        var existingState = GetAvatarRootState(vrcAvatarDescriptor);

        if (existingState.buildState == newBuildState) {
            return;
        }
        
        Debug.Log($"VRC_Bulk_Upload :: Build State '{currentVrcAvatarDescriptor.gameObject.name}' '{existingState.buildState}' => '{newBuildState}'");

        existingState.buildState = newBuildState;
        
        SetAvatarRootState(vrcAvatarDescriptor, existingState);
    }

    static void SetAvatarUploadState(VRCAvatarDescriptor vrcAvatarDescriptor, SdkUploadState? newUploadState) {
        var existingState = GetAvatarRootState(vrcAvatarDescriptor);

        if (existingState.uploadState == newUploadState) {
            return;
        }

        // NOTE: SDK changes upload state to empty while uploading for some reason

        Debug.Log($"VRC_Bulk_Upload :: Upload State '{currentVrcAvatarDescriptor.gameObject.name}' '{existingState.uploadState}' => '{newUploadState}'");

        existingState.uploadState = newUploadState;
        
        SetAvatarRootState(vrcAvatarDescriptor, existingState);
    }

// RENDER GUI

    void RenderAllAvatarsAndInScene() {
        GameObject[] rootObjects = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
        var hasRenderedAtLeastOne = false;

        foreach (var rootObject in rootObjects) {
            VRCAvatarDescriptor vrcAvatarDescriptor = rootObject.GetComponent<VRCAvatarDescriptor>();

            if (vrcAvatarDescriptor != null) {
                if (hasRenderedAtLeastOne) {
                    CustomGUI.LineGap();
                } else {
                    hasRenderedAtLeastOne = true;
                }

                if (!GetCanAvatarBeBuilt(vrcAvatarDescriptor)) {
                    CustomGUI.DisabledLabel(rootObject.name);
                } else {
                    GUILayout.Label(rootObject.name);
                }

                GUILayout.BeginHorizontal();

                if (CustomGUI.TinyButton("View")) {
                    Utils.FocusGameObject(rootObject);
                }

                EditorGUI.BeginDisabledGroup(!GetCanAvatarBeBuilt(vrcAvatarDescriptor));
                if (CustomGUI.TinyButton("Build")) {
                    BuildAvatar(vrcAvatarDescriptor);
                }

                if (CustomGUI.TinyButton("Build & Test")) {
                    BuildAndTestAvatar(vrcAvatarDescriptor);
                }
                EditorGUI.EndDisabledGroup();

                EditorGUI.BeginDisabledGroup(!GetCanAvatarBeUploaded(vrcAvatarDescriptor));
                if (CustomGUI.TinyButton("Build & Upload")) {
                    BuildAndUploadAvatar(vrcAvatarDescriptor);
                }
                EditorGUI.EndDisabledGroup();

                RenderAvatarState(vrcAvatarDescriptor);

                GUILayout.EndHorizontal();
            }
        }
    }

    string GetMessageForException(System.Exception e) {
        if (e is ApiErrorException) {
            return $"{(e as ApiErrorException).StatusCode}: {(e as ApiErrorException).ErrorMessage}";
        } else {
            return e.Message;
        }
    }

    string GetSuccessLabel(AvatarState avatarState) {
        switch (avatarState.action) {
            case Action.Build:
                return "Avatar Built";
            case Action.BuildAndTest:
                return "Test Avatar Built";
            case Action.BuildAndUpload:
                return "Avatar Uploaded";
            default:
                return "Unknown Success";
        }
    }

    void RenderAvatarState(VRCAvatarDescriptor vrcAvatarDescriptor) {
        AvatarState avatarState = GetAvatarRootState(vrcAvatarDescriptor);

        switch (avatarState.state) {
            case State.Idle:
                GUI.contentColor = Color.white;
                GUILayout.Label("");
                break;
            case State.Building:
                GUI.contentColor = new Color(0.8f, 0.8f, 1f, 1);
                GUILayout.Label("Building...");
                GUI.contentColor = Color.white;
                break;
            case State.Uploading:
                GUI.contentColor = new Color(0.8f, 0.8f, 1f, 1);
                GUILayout.Label("Uploading...");
                GUI.contentColor = Color.white;
                break;
            case State.Success:
                GUI.contentColor = new Color(0.8f, 1f, 0.8f, 1);
                GUILayout.Label(GetSuccessLabel(avatarState));
                GUI.contentColor = Color.white;
                break;
            case State.Failed:
                GUI.contentColor = new Color(1f, 0.8f, 0.8f, 1);
                GUILayout.Label($"{(avatarState.exception != null ? GetMessageForException(avatarState.exception) : "Failed (see console)") }");
                GUI.contentColor = Color.white;
                break;
            default:
                throw new System.Exception($"Unknown state {avatarState.state}");
        }
    }

// VRCHAT SDK

    class VRCSDK_Extension {
        [InitializeOnLoadMethod]
        public static void RegisterSDKCallback() {
            Debug.Log("VRC_Bulk_Upload :: SDK.RegisterSDKCallback");
            VRCSdkControlPanel.OnSdkPanelEnable += AddBuildHook;
        }

        private static void AddBuildHook(object sender, System.EventArgs e) {
            if (VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out var builder)) {
                builder.OnSdkBuildStart += OnBuildStarted;
                builder.OnSdkUploadStart += OnUploadStarted;
                builder.OnSdkBuildStateChange += OnSdkBuildStateChange;
                builder.OnSdkUploadStateChange += OnSdkUploadStateChange;
            }
        }

        public int callbackOrder => 0;

        private static void OnBuildStarted(object sender, object target) {
            var name = ((GameObject)target).name;
            Debug.Log($"VRC_Bulk_Upload :: SDK.OnBuildStarted :: Building '{name}'...");

            currentVrcAvatarDescriptor = ((GameObject)target).GetComponent<VRCAvatarDescriptor>();

            SetAvatarState(currentVrcAvatarDescriptor, State.Building);
            SetAvatarBuildState(currentVrcAvatarDescriptor, null);
            SetAvatarUploadState(currentVrcAvatarDescriptor, null);
        }

        private static void OnSdkBuildStateChange(object sender, SdkBuildState newState) {
            Debug.Log($"VRC_Bulk_Upload :: SDK.OnSdkBuildStateChange :: Build state for '{currentVrcAvatarDescriptor.gameObject.name}' is now '{newState}'");

            SetAvatarBuildState(currentVrcAvatarDescriptor, newState);
            SetAvatarUploadState(currentVrcAvatarDescriptor, null);
        }

        private static void OnUploadStarted(object sender, object target) {
            Debug.Log($"VRC_Bulk_Upload :: SDK.OnUploadStarted :: Uploading...");

            SetAvatarState(currentVrcAvatarDescriptor, State.Uploading);
            SetAvatarBuildState(currentVrcAvatarDescriptor, null);
            SetAvatarUploadState(currentVrcAvatarDescriptor, null);
        }

        private static void OnSdkUploadStateChange(object sender, SdkUploadState newState) {
            // NOTE: This gets called for build state change too for some reason

            if (newState == null) {
                return;
            }

            Debug.Log($"VRC_Bulk_Upload :: SDK.OnSdkUploadStateChange :: Upload state for '{currentVrcAvatarDescriptor.gameObject.name}' is now '{newState}'");

            SetAvatarBuildState(currentVrcAvatarDescriptor, null);
            SetAvatarUploadState(currentVrcAvatarDescriptor, newState);
        }

        [InitializeOnLoad]
        public class PreuploadHook : IVRCSDKPreprocessAvatarCallback {
            // This has to be before -1024 when VRCSDK deletes our components
            public int callbackOrder => -90000;

            public bool OnPreprocessAvatar(GameObject clonedAvatarGameObject) {
                Debug.Log($"VRC_Bulk_Upload :: SDK.OnPreprocessAvatar :: '{clonedAvatarGameObject.name}'");
                return true;
            }
        }
    }

    public class OnBuildRequest : IVRCSDKBuildRequestedCallback {
        public int callbackOrder => -90001;

        public bool OnBuildRequested(VRCSDKRequestedBuildType requestedBuildType) {
            Debug.Log($"VRC_Bulk_Upload :: SDK.OnBuildRequested :: '{requestedBuildType}'");
            return true;
        }
    }
}