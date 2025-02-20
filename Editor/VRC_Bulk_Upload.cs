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
using UnityEditor.Build;

public class VRC_Bulk_Upload : EditorWindow, IActiveBuildTargetChanged {
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
    
    // gui
    Vector2 scrollPosition;
    static Dictionary<string, AvatarState> avatarStates = new Dictionary<string, AvatarState>();

    // vrc sdk
    static bool isRegisteredWithSdk = false;
    static VRCAvatarDescriptor currentVrcAvatarDescriptor;
    static CancellationTokenSource GetAvatarCancellationToken = new CancellationTokenSource();
    static CancellationTokenSource BuildAndUploadCancellationToken = new CancellationTokenSource();

    // quest
    const string textInNameForQuest = "[Quest]";
    static bool onlyAllowQuestOnAndroid = true;

    // logging
    Vector2 logsScrollPosition;
    const string logPath = "Temp/PeanutTools/BulkUploader/debug.log";
    string lastLogsContents;

    // questify
    static bool autoQuestify = false;

    // auth
    static bool hasTriedToLogin = false;
    static bool isLoggedIn = false;
    static bool isLoggingIn = false;

    const string editorPrefNeedsToQuestifyKey = "NeedsToQuestify";
    static bool isAlreadyQuestifying = false;

    [MenuItem("Tools/PeanutTools/VRC Bulk Upload")]
    public static void ShowWindow() {
        var window = GetWindow<VRC_Bulk_Upload>();
        window.titleContent = new GUIContent("VRC Bulk Upload");
        window.minSize = new Vector2(400, 200);
    }

    void OnEnable() { 
        if (GetIsAndroid() && EditorPrefs.GetBool(editorPrefNeedsToQuestifyKey) == true) {
            Debug.Log("VRC_Bulk_Upload :: OnEnable needs to questify");

            EditorPrefs.SetBool(editorPrefNeedsToQuestifyKey, false);

            QuestifyActiveSceneAndReturnToWindows();
        } else {
            if (!isLoggedIn && !hasTriedToLogin) {
                Debug.Log("VRC_Bulk_Upload :: OnEnable login");
                hasTriedToLogin = true;

                #pragma warning disable CS4014
                Login();
                #pragma warning restore CS4014
            }
        }
    }

    static async Task<bool> Login() {
        Debug.Log($"VRC_Bulk_Upload :: Logging in with SDK...");

        if (VRC.Core.APIUser.CurrentUser != null) {
            Debug.Log("VRC_Bulk_Upload :: Current user already found");
            isLoggingIn = false;
            isLoggedIn = true;
            return true;
        }
        
        isLoggingIn = true;
        var result = VRC.Core.ApiCredentials.Load();

        var cancellationTokenSource = new CancellationTokenSource(5000); // after 5 seconds cancel it all
        var cancellationToken = cancellationTokenSource.Token;

        while (!VRC.Core.API.IsReady()) {
            if (cancellationToken.IsCancellationRequested) {
                Debug.Log("VRC_Bulk_Upload :: Waiting for API cancelled");
                return false;
            }

            Debug.Log("VRC_Bulk_Upload :: Waiting for API");
            await Task.Delay(500);
        }

        Debug.Log("VRC_Bulk_Upload :: API ready");

        var tcs = new TaskCompletionSource<bool>();

        VRC.Core.APIUser.InitialFetchCurrentUser(
            onSuccess: (userContainer) => {
                Debug.Log("VRC_Bulk_Upload :: Current user found");
                isLoggingIn = false;
                isLoggedIn = true;
                StaticRepaint();
                tcs.SetResult(true);
            },
            onError: (errorContainer) => {
                Debug.Log("VRC_Bulk_Upload :: Failed to get current user");
                isLoggingIn = false;
                StaticRepaint();
                tcs.SetResult(false);
            }
        );

        return await tcs.Task;
    }

    static async Task WaitForRegisteredWithSdk() {
        var cancellationTokenSource = new CancellationTokenSource(5000); // after 5 seconds cancel it all
        var cancellationToken = cancellationTokenSource.Token;

        while (!isRegisteredWithSdk) {
            if (cancellationToken.IsCancellationRequested) {
                Debug.Log("VRC_Bulk_Upload :: Waiting for register with SDK cancelled");
                return;
            }

            Debug.Log("VRC_Bulk_Upload :: Waiting to register with SDK");
            await Task.Delay(500);
        }

        Debug.Log("VRC_Bulk_Upload :: Registered with SDK");
    }

    static void StaticRepaint() {
        // GetWindow spawns a new window every time for some reason
        var objects = Resources.FindObjectsOfTypeAll(typeof(VRC_Bulk_Upload));
        var window = objects[0] as VRC_Bulk_Upload;

        if (window != null) {
            window.Focus();
            window.Repaint();
        }
    }

    bool GetIsQuestifyerAvailable() {
        System.Type questifyerType = System.Type.GetType("VRC_Questifyer");
        return questifyerType != null;
    }

    void OnGUI() {
        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

        CustomGUI.LargeLabel("VRC Bulk Upload");
        CustomGUI.ItalicLabel("Bulk uploads all active avatars in your scene.");

        CustomGUI.LineGap();

        if (!isLoggedIn) {
            if (isLoggingIn) {
                CustomGUI.RenderLoading("Waiting for SDK to login...");

                if (CustomGUI.StandardButton("Try Again")) {
                    #pragma warning disable CS4014
                    Login();
                    #pragma warning restore CS4014
                }
            } else {
                CustomGUI.RenderErrorMessage("Failed to login. You must open the VRC SDK control panel first");

                if (CustomGUI.StandardButton("Try Again")) {
                    #pragma warning disable CS4014
                    Login();
                    #pragma warning restore CS4014
                }
            }
        }
        
        CustomGUI.LargeLabel("Avatars");

        CustomGUI.LineGap();

        RenderAllAvatarsAndInScene();

        CustomGUI.LineGap();
        
        GUILayout.BeginHorizontal();

        int count = GetVrchatAvatarsToProcess().Count;

        if (CustomGUI.StandardButton($"Build All ({count})")) {
            #pragma warning disable CS4014
            BuildAllAvatars();
            #pragma warning restore CS4014
        }

        if (CustomGUI.StandardButton($"Build And Test All ({count})")) {
            #pragma warning disable CS4014
            BuildAndTestAllAvatars();
            #pragma warning restore CS4014
        }
        
        GUILayout.EndHorizontal();
        
        CustomGUI.LineGap();

        if (CustomGUI.PrimaryButton($"Build And Upload All ({count})")) {
            #pragma warning disable CS4014
            BuildAndUploadAllAvatars();
            #pragma warning restore CS4014
        }

        if (GetIsAndroid()) {
            CustomGUI.RenderWarningMessage("Warning: You are in Android mode");
        }

        CustomGUI.LineGap();

        if (CustomGUI.StandardButton("Reset Statuses")) {
            ResetAvatars();
        }
        
        CustomGUI.LineGap();

        CustomGUI.MediumLabel("Quest Settings");

        CustomGUI.LineGap();

#if VRC_QUESTIFYER_INSTALLED
        autoQuestify = CustomGUI.Checkbox($"Auto-Questify", autoQuestify);
        CustomGUI.ItalicLabel(@"After uploading your PC avatars this tool will:
1. Switch to Android
2. Duplicate current scene and add [Quest] to the name
3. Questify all active avatars
4. Build and upload each one
5. Switch back to Windows and original scene

It assumes your avatars are already ready for Questify.
If something goes wrong just delete the Quest scene and start again.");

        CustomGUI.LineGap();
#endif

        onlyAllowQuestOnAndroid = CustomGUI.Checkbox($"Filter Quest avatars", onlyAllowQuestOnAndroid);
        CustomGUI.ItalicLabel($"Ignore avatars named {textInNameForQuest} on Windows and vice-versa.");

        CustomGUI.LineGap();

        if (CustomGUI.StandardButton($"Switch To {(GetIsAndroid() ? "Windows" : "Android")}")) {
            SwitchPlatform();
        }

        CustomGUI.LineGap();

        RenderLogs();

        EditorGUILayout.EndScrollView();
    }

    static void SwitchPlatform() {
        Debug.Log($"VRC_Bulk_Upload :: Switching platform...");

        if (GetIsAndroid()) {
            SwitchToWindows();
        } else {
            SwitchToAndroid();
        }
    }

    static bool GetIsAndroid() {
        return EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android;
    }

    static void SwitchToWindows() {
        RecordLog($"Switch platform to Windows");
        EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows);
    }

    static void SwitchToAndroid() {
        RecordLog($"Switch platform to Android");
        EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
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

    static void CreateFileIfDoesNotExist(string filePath) {
        if (File.Exists(filePath))
            return;

        var fileName = Path.GetFileName(filePath);
        var fileDirectoryPath = filePath.Replace(fileName, "");
        Directory.CreateDirectory(fileDirectoryPath);
    }

    static void RecordLog(string message) {
        CreateFileIfDoesNotExist(logPath);

        string timestamp = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string logMessage = $"{timestamp} - {message}";

        File.AppendAllText(logPath, logMessage + System.Environment.NewLine);
    }

// HELPERS

    static async Task<VRCAvatar> GetVRCAvatarFromDescriptor(VRCAvatarDescriptor vrcAvatarDescriptor) {
        var blueprintId = vrcAvatarDescriptor.GetComponent<PipelineManager>().blueprintId;

        Debug.Log($"VRC_Bulk_Upload :: Fetching avatar for '{vrcAvatarDescriptor.gameObject.name}' ('{blueprintId}')...");

        try {
            VRCAvatar avatarData = await VRCApi.GetAvatar(blueprintId, true, cancellationToken: GetAvatarCancellationToken.Token);

            Debug.Log($"VRC_Bulk_Upload :: Avatar found");

            return avatarData;
        } catch (ApiErrorException ex) {
            if (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                Debug.Log("VRC_Bulk_Upload :: Avatar not found (creating instead)");
            }
            else
            {
                Debug.LogError(ex.ErrorMessage);
            }
        }
        
        return new VRCAvatar();
    }

    async Task BuildAllAvatars() {
        var activeVrchatAvatars = GetVrchatAvatarsToProcess();

        Debug.Log($"VRC_Bulk_Upload :: Building {activeVrchatAvatars.Count} avatars...");

        foreach (var activeVrchatAvatar in activeVrchatAvatars) {
            await BuildAvatar(activeVrchatAvatar);
        }
    }

    async Task BuildAndTestAllAvatars() {
        var activeVrchatAvatars = GetVrchatAvatarsToProcess();

        Debug.Log($"VRC_Bulk_Upload :: Building and testing {activeVrchatAvatars.Count} avatars...");

        foreach (var activeVrchatAvatar in activeVrchatAvatars) {
            await BuildAndTestAvatar(activeVrchatAvatar);
        }
    }

    async Task BuildAndUploadAllAvatars() {
        var activeVrchatAvatars = GetVrchatAvatarsToProcess();

        Debug.Log($"VRC_Bulk_Upload :: Building and uploading {activeVrchatAvatars.Count} avatars...");

        foreach (var activeVrchatAvatar in activeVrchatAvatars) {
            await BuildAndUploadAvatar(activeVrchatAvatar);
        }

        if (autoQuestify) {
            Debug.Log("VRC_Bulk_Upload :: Auto Questify enabled");
            
            EditorPrefs.SetBool(editorPrefNeedsToQuestifyKey, true);

            #pragma warning disable CS4014
            SwitchToAndroid();
            #pragma warning restore CS4014
        } else {
            Debug.Log("VRC_Bulk_Upload :: Auto Questify disabled");
            
            EditorPrefs.SetBool(editorPrefNeedsToQuestifyKey, false);
        }
    }

    public int callbackOrder { get { return 0; } }

    public void OnActiveBuildTargetChanged(BuildTarget previousTarget, BuildTarget newTarget)
    {
        if (newTarget != BuildTarget.Android) {
            return;
        }

        Debug.Log($"VRC_Bulk_Upload :: Switched to Android");
    }

    static async Task QuestifyActiveSceneAndReturnToWindows() {
        Debug.Log($"VRC_Bulk_Upload :: Questify active scene");

        if (!await Login()) {
            Debug.Log($"VRC_Bulk_Upload :: Cannot Questify without being logged in");
            return;
        }

        isAlreadyQuestifying = true;

        var originalScenePath = Questify.GetActiveScenePath();

        Questify.CreateQuestScene();

        await QuestifyAndUploadAllAvatarsInAndroid();

        Debug.Log($"VRC_Bulk_Upload :: Questify done");

        Debug.Log($"VRC_Bulk_Upload :: Switching to Windows scene...");

        Questify.SwitchScene(originalScenePath);
        
        Debug.Log($"VRC_Bulk_Upload :: Switching to Windows...");

        isAlreadyQuestifying = false;

        SwitchToWindows();
    }

    static async Task QuestifyAndUploadAllAvatarsInAndroid() {
        Debug.Log($"VRC_Bulk_Upload :: Questifying all avatars (in Android)...");

        var activeVrchatAvatars = GetVrchatAvatarsToProcess(true);

        Debug.Log($"VRC_Bulk_Upload :: Found {activeVrchatAvatars.Count} active avatars");
        
        var questifiedVrchatAvatars = new List<VRCAvatarDescriptor>();

        foreach (var activeVrchatAvatar in activeVrchatAvatars) {
            var questifiedVrchatAvatar = Questify.QuestifyAvatar(activeVrchatAvatar);
            questifiedVrchatAvatars.Add(questifiedVrchatAvatar);
        }

        Debug.Log($"VRC_Bulk_Upload :: {questifiedVrchatAvatars.Count} avatars processed by Questifyer");

        await WaitForRegisteredWithSdk();
        
        Debug.Log($"VRC_Bulk_Upload :: Building and uploading each Questify'd avatar...");

        foreach (var questifiedVrchatAvatar in questifiedVrchatAvatars) {
            await BuildAndUploadAvatar(questifiedVrchatAvatar);
        }
        
        Debug.Log($"VRC_Bulk_Upload :: All avatars Questify success");
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

            StaticRepaint();

            string bundlePath = await builder.Build(vrcAvatarDescriptor.gameObject);
            
            Debug.Log($"VRC_Bulk_Upload :: '{vrcAvatarDescriptor.gameObject.name}' built to '{bundlePath}'");
            
            SetAvatarState(vrcAvatarDescriptor, State.Success);
            
            RecordLog($"Build '{vrcAvatarDescriptor.gameObject.name}' success");
        } catch (System.Exception e) {
            Debug.LogError(e);
            SetAvatarState(vrcAvatarDescriptor, State.Failed, e);
            RecordLog($"Build '{vrcAvatarDescriptor.gameObject.name}' failed");
        }
    }

    static async Task BuildAndUploadAvatar(VRCAvatarDescriptor vrcAvatarDescriptor) {
        Debug.Log($"VRC_Bulk_Upload :: Building and uploading '{vrcAvatarDescriptor.gameObject.name}'...");

        RecordLog($"Build and upload '{vrcAvatarDescriptor.gameObject.name}'...");

        try {
            if (!isLoggedIn) {
                var result = await Login();

                if (!result) {
                    Debug.Log($"VRC_Bulk_Upload :: Tried to login and failed");
                    return;
                }
            }

            if (!VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out var builder)) {
                throw new System.Exception("No builder found");
            }

            currentVrcAvatarDescriptor = vrcAvatarDescriptor;
            SetAvatarAction(vrcAvatarDescriptor, Action.BuildAndUpload);

            StaticRepaint();

            var vrcAvatar = await GetVRCAvatarFromDescriptor(vrcAvatarDescriptor);

            Debug.Log("VRC_Bulk_Upload :: SDK build and upload");

            await builder.BuildAndUpload(vrcAvatarDescriptor.gameObject, vrcAvatar, cancellationToken: BuildAndUploadCancellationToken.Token);
            
            Debug.Log("VRC_Bulk_Upload :: SDK build and upload success");

            SetAvatarState(vrcAvatarDescriptor, State.Success);
            
            RecordLog($"Build and upload '{vrcAvatarDescriptor.gameObject.name}' success");
        } catch (System.Exception e) {
            Debug.LogError(e);
            SetAvatarState(vrcAvatarDescriptor, State.Failed, e);
            RecordLog($"Build and upload '{vrcAvatarDescriptor.gameObject.name}' failed");
        }
    }

    static async Task BuildAndTestAvatar(VRCAvatarDescriptor vrcAvatarDescriptor) {
        Debug.Log($"VRC_Bulk_Upload :: Building and testing '{vrcAvatarDescriptor.gameObject.name}'...");
        
        RecordLog($"Build and test '{vrcAvatarDescriptor.gameObject.name}'...");

        try {
            if (!VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out var builder)) {
                throw new System.Exception("No builder found");
            }

            currentVrcAvatarDescriptor = vrcAvatarDescriptor;
            SetAvatarAction(vrcAvatarDescriptor, Action.BuildAndTest);

            StaticRepaint();

            await builder.BuildAndTest(vrcAvatarDescriptor.gameObject);

            SetAvatarState(vrcAvatarDescriptor, State.Success);
            
            RecordLog($"Build and test '{vrcAvatarDescriptor.gameObject.name}' success");
        } catch (System.Exception e) {
            Debug.LogError(e);
            SetAvatarState(vrcAvatarDescriptor, State.Failed, e);
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

    static List<VRCAvatarDescriptor> GetVrchatAvatarsToProcess(bool ignoreQuestCheck = false) {
        GameObject[] rootObjects = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
        
        var vrcAvatarDescriptors = new List<VRCAvatarDescriptor>();

        foreach (var rootObject in rootObjects) {
            VRCAvatarDescriptor vrcAvatarDescriptor = rootObject.GetComponent<VRCAvatarDescriptor>();

            if (!GetCanAvatarBeBuilt(vrcAvatarDescriptor, ignoreQuestCheck)) {
                continue;
            }

            vrcAvatarDescriptors.Add(vrcAvatarDescriptor);
        }

        return vrcAvatarDescriptors;
    }

    static bool GetIsAvatarForMyPlatform(VRCAvatarDescriptor vrcAvatarDescriptor) {
        return (
            vrcAvatarDescriptor.gameObject.name.Contains(textInNameForQuest) && EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android
            ||
            !vrcAvatarDescriptor.gameObject.name.Contains(textInNameForQuest) && EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android
        );
    }

    static bool GetCanAvatarBeBuilt(VRCAvatarDescriptor vrcAvatarDescriptor, bool ignoreQuestCheck = false) {
        return vrcAvatarDescriptor != null 
            && vrcAvatarDescriptor.gameObject.GetComponent<Animator>() != null 
            && vrcAvatarDescriptor.gameObject.activeSelf 
            && (ignoreQuestCheck || (onlyAllowQuestOnAndroid && GetIsAvatarForMyPlatform(vrcAvatarDescriptor)) || !onlyAllowQuestOnAndroid);
    }

    bool GetCanAvatarBeUploaded(VRCAvatarDescriptor vrcAvatarDescriptor) {
        return GetCanAvatarBeBuilt(vrcAvatarDescriptor) && vrcAvatarDescriptor.gameObject.GetComponent<PipelineManager>().blueprintId != null && vrcAvatarDescriptor.gameObject.activeSelf && ((onlyAllowQuestOnAndroid && GetIsAvatarForMyPlatform(vrcAvatarDescriptor)) || !onlyAllowQuestOnAndroid);
    }

    static AvatarState GetAvatarRootState(VRCAvatarDescriptor vrcAvatarDescriptor) {
        if (!avatarStates.ContainsKey(vrcAvatarDescriptor.gameObject.name)) {
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
                    #pragma warning disable CS4014
                    BuildAvatar(vrcAvatarDescriptor);
                    #pragma warning restore CS4014
                }

                if (CustomGUI.TinyButton("Build & Test")) {
                    #pragma warning disable CS4014
                    BuildAndTestAvatar(vrcAvatarDescriptor);
                    #pragma warning restore CS4014
                }
                EditorGUI.EndDisabledGroup();

                EditorGUI.BeginDisabledGroup(!GetCanAvatarBeUploaded(vrcAvatarDescriptor));
                if (CustomGUI.TinyButton("Build & Upload")) {
                    #pragma warning disable CS4014
                    BuildAndUploadAvatar(vrcAvatarDescriptor);
                    #pragma warning restore CS4014
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

            VRC_Bulk_Upload.isRegisteredWithSdk = true;
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