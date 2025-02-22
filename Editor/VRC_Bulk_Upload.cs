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
    
    Vector2 scrollPosition;
    Dictionary<string, AvatarState> avatarStates = new Dictionary<string, AvatarState>();
    bool hasInit = false;

    // auth - must be static to persist
    static Task<bool> loginTask;

    // vrc sdk
    VRCAvatarDescriptor currentVrcAvatarDescriptor;
    CancellationTokenSource getAvatarCancellationToken;
    CancellationTokenSource buildAndUploadCancellationToken;

    // quest
    const string textInNameForQuest = "[Quest]";
    bool onlyAllowQuestOnAndroid = true;

    // logging
    Vector2 logsScrollPosition;
    const string logPath = "Temp/PeanutTools/VRC_Bulk_Upload/debug.log";
    string lastLogsContents;

    // questify
    bool autoQuestify = false;

    // auth
    bool isLoggingIn = false;

    const string editorPrefNeedsToQuestifyKey = "NeedsToQuestify";
    bool isAlreadyQuestifying = false;

    [MenuItem("Tools/PeanutTools/VRC Bulk Upload")]
    public static void ShowWindow() {
        var window = GetWindow<VRC_Bulk_Upload>();
        window.titleContent = new GUIContent("VRC Bulk Upload");
        window.minSize = new Vector2(400, 200);
    }

    bool GetIsLoggedIn() {
        return VRC.Core.APIUser.CurrentUser != null;
    }

    void OnEnable() {
        // just do this every time in hope it works
        if (!GetIsLoggedIn()) {
            #pragma warning disable CS4014
            Login();
            #pragma warning restore CS4014
        }

        RegisterSDKCallback();

        Init();
    }

    void OnDisable() {
        UnregisterSDKCallback();
    }

    // this must only happen once the window has "loaded"
    void Init() {
        if (hasInit) {
            return;
        }
        
        hasInit = true;

        Utils.LogMessage("Initializing...");

        if (Utils.GetIsAndroid()) {
            if (EditorPrefs.GetBool(editorPrefNeedsToQuestifyKey) == true) {
                Utils.LogMessage("Editor pref set - need to Questify");

                EditorPrefs.SetBool(editorPrefNeedsToQuestifyKey, false);

                QuestifyActiveSceneAndReturnToWindows();
            } else {
                Utils.LogMessage("Editor pref NOT set - does NOT need to Questify");
            }
        }
    }

    async Task<bool> Login() {
        if (loginTask != null) {
            Utils.LogMessage("Waiting for existing login attempt");
            return await loginTask;
        }

        loginTask = PerformLogin();

        try {
            return await loginTask;
        } finally {
            loginTask = null;
        }
    }
    
    async Task<bool> PerformLogin() {
        Utils.LogMessage("Logging in...");

        if (VRC.Core.APIUser.CurrentUser != null) {
            Utils.LogMessage("Current user already found");
            isLoggingIn = false;
            Utils.LogMessage("Login success");
            return true;
        }

        Utils.LogMessage($"Telling SDK to load credentials (5 sec)...");
        
        isLoggingIn = true;
        var result = VRC.Core.ApiCredentials.Load();

        var cancellationTokenSource = new CancellationTokenSource(5000); // after 5 seconds cancel it all
        var cancellationToken = cancellationTokenSource.Token;

        while (!VRC.Core.API.IsReady()) {
            if (cancellationToken.IsCancellationRequested) {
                Utils.LogMessage("Waiting for API cancelled");
                return false;
            }

            Utils.LogMessage("Waiting for API (500ms)");
            await Task.Delay(500);
        }

        Utils.LogMessage("API ready, telling SDK to fetch user account...");

        var tcs = new TaskCompletionSource<bool>();

        VRC.Core.APIUser.InitialFetchCurrentUser(
            onSuccess: (userContainer) => {
                Utils.LogMessage("User account fetched");
                isLoggingIn = false;
                StaticRepaint();
                Utils.LogMessage("Login success");
                tcs.SetResult(true);
            },
            onError: (errorContainer) => {
                Utils.LogMessage($"Failed to fetch current user: {errorContainer}");
                isLoggingIn = false;
                StaticRepaint();
                tcs.SetResult(false);
            }
        );

        return await tcs.Task;
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

    bool GetIsBusy() {
        return avatarStates.Values.Any(avatarState => 
            avatarState.state == State.Building || avatarState.state == State.Uploading
        );
    }

    void OnGUI() {
        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

        CustomGUI.LargeLabel("VRC Bulk Upload");
        CustomGUI.ItalicLabel("Bulk uploads all active avatars in your scene.");

        CustomGUI.LineGap();

        if (!GetIsLoggedIn()) {
            if (isLoggingIn) {
                CustomGUI.RenderLoading("Waiting for SDK to login...");

                if (CustomGUI.StandardButton("Check Again")) {
                    #pragma warning disable CS4014
                    Login();
                    #pragma warning restore CS4014
                }
            } else {
                CustomGUI.RenderErrorMessage("Failed to login. You must open the VRC SDK control panel first");

                if (CustomGUI.StandardButton("Check Again")) {
                    #pragma warning disable CS4014
                    Login();
                    #pragma warning restore CS4014
                }

                CustomGUI.LineGap();
            }
        }
        
        EditorGUI.BeginDisabledGroup(GetIsBusy());
        
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

        if (Utils.GetIsAndroid()) {
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

        if (CustomGUI.StandardButton($"Switch To {(Utils.GetIsAndroid() ? "Windows" : "Android")}")) {
            SwitchPlatform();
        }

        CustomGUI.LineGap();

        RenderLogs();

        EditorGUILayout.EndScrollView();
        
        EditorGUI.EndDisabledGroup();
    }

    void SwitchPlatform() {
        Utils.LogMessage($"Switching platform...");

        if (Utils.GetIsAndroid()) {
            SwitchToWindows();
        } else {
            SwitchToAndroid();
        }
    }

    void SwitchToWindows() {
        RecordMessage($"Switch platform to Windows");
        EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows);
    }

    void SwitchToAndroid() {
        RecordMessage($"Switch platform to Android");
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

    void RecordMessage(string message) {
        Utils.CreateDirectoryIfNoExist(logPath);
        
        string timestamp = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string logMessage = $"{timestamp} - {message}";

        File.AppendAllText(logPath, logMessage + System.Environment.NewLine);
    }

// HELPERS

    async Task<VRCAvatar> GetVRCAvatarFromDescriptor(VRCAvatarDescriptor vrcAvatarDescriptor) {
        var blueprintId = vrcAvatarDescriptor.GetComponent<PipelineManager>().blueprintId;

        Utils.LogMessage($"Fetching avatar for '{vrcAvatarDescriptor.gameObject.name}' ('{blueprintId}')...");

        try {
            getAvatarCancellationToken = new CancellationTokenSource();

            VRCAvatar avatarData = await VRCApi.GetAvatar(blueprintId, true, cancellationToken: getAvatarCancellationToken.Token);

            Utils.LogMessage($"Fetch for '{vrcAvatarDescriptor.gameObject.name}' ('{blueprintId}') successful: '{avatarData}'");

            return avatarData;
        } catch (ApiErrorException ex) {
            if (ex.StatusCode == System.Net.HttpStatusCode.NotFound) {
                Utils.LogMessage($"Fetch for '{vrcAvatarDescriptor.gameObject.name}' ('{blueprintId}') failed: avatar not found (creating instead)");
            } else {
                Debug.LogError(ex.ErrorMessage);
            }
        }
        
        return new VRCAvatar();
    }

    async Task BuildAllAvatars() {
        var activeVrchatAvatars = GetVrchatAvatarsToProcess();

        Utils.LogMessage($"Building {activeVrchatAvatars.Count} avatars...");

        foreach (var activeVrchatAvatar in activeVrchatAvatars) {
            await BuildAvatar(activeVrchatAvatar);
        }
    }

    async Task BuildAndTestAllAvatars() {
        var activeVrchatAvatars = GetVrchatAvatarsToProcess();

        Utils.LogMessage($"Building and testing {activeVrchatAvatars.Count} avatars...");

        foreach (var activeVrchatAvatar in activeVrchatAvatars) {
            await BuildAndTestAvatar(activeVrchatAvatar);
        }
    }

    async Task BuildAndUploadAllAvatars() {
        var activeVrchatAvatars = GetVrchatAvatarsToProcess();

        Utils.LogMessage($"Building and uploading {activeVrchatAvatars.Count} avatars...");

        foreach (var activeVrchatAvatar in activeVrchatAvatars) {
            await BuildAndUploadAvatar(activeVrchatAvatar);
        }

        if (autoQuestify) {
            Utils.LogMessage("Auto Questify enabled (setting editor pref)");
            
            EditorPrefs.SetBool(editorPrefNeedsToQuestifyKey, true);

            #pragma warning disable CS4014
            SwitchToAndroid();
            #pragma warning restore CS4014
        } else {
            Utils.LogMessage("Auto Questify disabled (not setting editor pref)");
            
            EditorPrefs.SetBool(editorPrefNeedsToQuestifyKey, false);
        }
    }

    public int callbackOrder { get { return 0; } }

    public void OnActiveBuildTargetChanged(BuildTarget previousTarget, BuildTarget newTarget)
    {
        if (newTarget != BuildTarget.Android) {
            return;
        }

        Utils.LogMessage($"Switched to Android");
    }

    async Task QuestifyActiveSceneAndReturnToWindows() {
        Utils.LogMessage($"Questify active scene");

        if (!Utils.GetIsAndroid()) {
            throw new System.Exception("Tried to questify active scene without being in Android");
        }

        if (!await Login()) {
            Utils.LogMessage($"Cannot Questify without being logged in");
            return;
        }

        isAlreadyQuestifying = true;

        var originalScenePath = Questify.GetActiveScenePath();

        Questify.CreateQuestScene();

        await QuestifyAndUploadAllAvatarsInAndroid();

        Utils.LogMessage($"Questify done");

        Utils.LogMessage($"Switching to Windows scene...");

        Questify.SwitchScene(originalScenePath);
        
        Utils.LogMessage($"Switching to Windows...");

        isAlreadyQuestifying = false;

        SwitchToWindows();
    }

    async Task QuestifyAndUploadAllAvatarsInAndroid() {
        Utils.LogMessage($"Questifying all avatars (in Android)...");

        var activeVrchatAvatars = GetVrchatAvatarsToProcess(true);

        Utils.LogMessage($"Found {activeVrchatAvatars.Count} active avatars");
        
        var questifiedVrchatAvatars = new List<VRCAvatarDescriptor>();

        foreach (var activeVrchatAvatar in activeVrchatAvatars) {
            var questifiedVrchatAvatar = Questify.QuestifyAvatar(activeVrchatAvatar);
            questifiedVrchatAvatars.Add(questifiedVrchatAvatar);
        }

        Utils.LogMessage($"{questifiedVrchatAvatars.Count} avatars processed by Questifyer");
        
        Utils.LogMessage($"Building and uploading each Questify'd avatar...");

        foreach (var questifiedVrchatAvatar in questifiedVrchatAvatars) {
            await BuildAndUploadAvatar(questifiedVrchatAvatar);
        }
        
        Utils.LogMessage($"All avatars Questify success");
    }

    async Task BuildAvatar(VRCAvatarDescriptor vrcAvatarDescriptor) {
        Utils.LogMessage($"Building '{vrcAvatarDescriptor.gameObject.name}'...");

        RecordMessage($"Build '{vrcAvatarDescriptor.gameObject.name}'...");
        
        try {
            if (!VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out var builder)) {
                throw new System.Exception("No builder found");
            }

            currentVrcAvatarDescriptor = vrcAvatarDescriptor;
            SetAvatarAction(vrcAvatarDescriptor, Action.Build);

            StaticRepaint();

            string bundlePath = await builder.Build(vrcAvatarDescriptor.gameObject);
            
            Utils.LogMessage($"'{vrcAvatarDescriptor.gameObject.name}' built to '{bundlePath}'");
            
            SetAvatarState(vrcAvatarDescriptor, State.Success);
            
            RecordMessage($"Build '{vrcAvatarDescriptor.gameObject.name}' success");
        } catch (System.Exception e) {
            Debug.LogError(e);
            SetAvatarState(vrcAvatarDescriptor, State.Failed, e);
            RecordMessage($"Build '{vrcAvatarDescriptor.gameObject.name}' failed");

            throw e;
        }
    }

    async Task BuildAndUploadAvatar(VRCAvatarDescriptor vrcAvatarDescriptor) {
        Utils.LogMessage($"Building and uploading '{vrcAvatarDescriptor.gameObject.name}'...");

        RecordMessage($"Build and upload '{vrcAvatarDescriptor.gameObject.name}'...");

        try {
            if (!GetIsLoggedIn()) {
                var result = await Login();

                if (!result) {
                    Utils.LogMessage($"Tried to login and failed");
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

            Utils.LogMessage($"Telling SDK to build and upload '{vrcAvatarDescriptor.gameObject}'...");

            // TODO: Allow user to cancel
            buildAndUploadCancellationToken = new CancellationTokenSource();

            // without this check if something goes wrong the SDK blindly assumes you are logged in without a helpful message
            if (!GetIsLoggedIn()) {
                throw new System.Exception("Not logged in");
            }

            await builder.BuildAndUpload(vrcAvatarDescriptor.gameObject, vrcAvatar, cancellationToken: buildAndUploadCancellationToken.Token);
            
            Utils.LogMessage("SDK build and upload success");

            SetAvatarState(vrcAvatarDescriptor, State.Success);
            
            RecordMessage($"Build and upload '{vrcAvatarDescriptor.gameObject.name}' success");
        } catch (System.Exception e) {
            Debug.LogError(e);
            SetAvatarState(vrcAvatarDescriptor, State.Failed, e);
            RecordMessage($"Build and upload '{vrcAvatarDescriptor.gameObject.name}' failed");
            
            throw e;
        }
    }

    async Task BuildAndTestAvatar(VRCAvatarDescriptor vrcAvatarDescriptor) {
        Utils.LogMessage($"Building and testing '{vrcAvatarDescriptor.gameObject.name}'...");
        
        RecordMessage($"Build and test '{vrcAvatarDescriptor.gameObject.name}'...");

        try {
            if (!VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out var builder)) {
                throw new System.Exception("No builder found");
            }

            currentVrcAvatarDescriptor = vrcAvatarDescriptor;
            SetAvatarAction(vrcAvatarDescriptor, Action.BuildAndTest);

            StaticRepaint();

            await builder.BuildAndTest(vrcAvatarDescriptor.gameObject);

            SetAvatarState(vrcAvatarDescriptor, State.Success);
            
            RecordMessage($"Build and test '{vrcAvatarDescriptor.gameObject.name}' success");
        } catch (System.Exception e) {
            Debug.LogError(e);
            SetAvatarState(vrcAvatarDescriptor, State.Failed, e);
            RecordMessage($"Build and test '{vrcAvatarDescriptor.gameObject.name}' failed");
            
            throw e;
        }
    }

    void ResetAvatars() {
        Utils.LogMessage("Reset avatars");

        foreach (var key in avatarStates.Keys.ToList())
        {
            avatarStates[key] = new AvatarState()
            {
                state = State.Idle
            };
        }

        RecordMessage("Reset avatars");
    }

    static Scene[] GetLoadedScenes() {
        List<Scene> loadedScenes = new List<Scene>(SceneManager.sceneCount);
        for (int sceneIndex = 0; sceneIndex < SceneManager.sceneCount; ++sceneIndex)
        {
            Scene scene = SceneManager.GetSceneAt(sceneIndex);
            if (scene.isLoaded) {
                loadedScenes.Add(scene);
            }
        }

        return loadedScenes.ToArray();
    }

    static GameObject[] GetRootObjectsInAllLoadedScenes() {
        List<GameObject> rootObjects = new List<GameObject>();
        var scenes = GetLoadedScenes();
        
        foreach (var scene in scenes) {
            var sceneRootObjects = scene.GetRootGameObjects();
            rootObjects.AddRange(sceneRootObjects);
        }

        return rootObjects.ToArray();
    }

    List<VRCAvatarDescriptor> GetVrchatAvatarsToProcess(bool ignoreQuestCheck = false) {
        GameObject[] rootObjects = GetRootObjectsInAllLoadedScenes();
        var vrcAvatarDescriptors = new List<VRCAvatarDescriptor>();

        foreach (var rootObject in rootObjects) {
            if (!rootObject.activeSelf) {
                continue;
            }

            VRCAvatarDescriptor vrcAvatarDescriptor = rootObject.GetComponent<VRCAvatarDescriptor>();

            if (!GetCanAvatarBeBuilt(vrcAvatarDescriptor, ignoreQuestCheck)) {
                continue;
            }

            vrcAvatarDescriptors.Add(vrcAvatarDescriptor);
        }

        return vrcAvatarDescriptors;
    }

    bool GetIsAvatarForMyPlatform(VRCAvatarDescriptor vrcAvatarDescriptor) {
        return (
            vrcAvatarDescriptor.gameObject.name.Contains(textInNameForQuest) && EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android
            ||
            !vrcAvatarDescriptor.gameObject.name.Contains(textInNameForQuest) && EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android
        );
    }

    bool GetCanAvatarBeBuilt(VRCAvatarDescriptor vrcAvatarDescriptor, bool ignoreQuestCheck = false) {
        return vrcAvatarDescriptor != null 
            && vrcAvatarDescriptor.gameObject.GetComponent<Animator>() != null 
            && vrcAvatarDescriptor.gameObject.activeSelf 
            && (ignoreQuestCheck || (onlyAllowQuestOnAndroid && GetIsAvatarForMyPlatform(vrcAvatarDescriptor)) || !onlyAllowQuestOnAndroid);
    }

    bool GetCanAvatarBeUploaded(VRCAvatarDescriptor vrcAvatarDescriptor) {
        return GetCanAvatarBeBuilt(vrcAvatarDescriptor) && vrcAvatarDescriptor.gameObject.GetComponent<PipelineManager>().blueprintId != null && vrcAvatarDescriptor.gameObject.activeSelf && ((onlyAllowQuestOnAndroid && GetIsAvatarForMyPlatform(vrcAvatarDescriptor)) || !onlyAllowQuestOnAndroid);
    }

    AvatarState GetAvatarRootState(VRCAvatarDescriptor vrcAvatarDescriptor) {
        if (!avatarStates.ContainsKey(vrcAvatarDescriptor.gameObject.name)) {
            avatarStates[vrcAvatarDescriptor.gameObject.name] = new AvatarState() {
                state = State.Idle
            };
        }
        return avatarStates[vrcAvatarDescriptor.gameObject.name];
    }

    void SetAvatarRootState(VRCAvatarDescriptor vrcAvatarDescriptor, AvatarState newRootState) {
        Debug.Log($"SET {vrcAvatarDescriptor.gameObject.name} => {newRootState.state}");
        avatarStates[vrcAvatarDescriptor.gameObject.name] = newRootState;
    }

    void SetAvatarAction(VRCAvatarDescriptor vrcAvatarDescriptor, Action newAction) {
        var existingState = GetAvatarRootState(vrcAvatarDescriptor);

        Utils.LogMessage($"Action '{currentVrcAvatarDescriptor.gameObject.name}' '{existingState.action}' => '{newAction}'");
        
        avatarStates[vrcAvatarDescriptor.gameObject.name] = new AvatarState() {
            state = existingState.state,
            exception = existingState.exception,
            action = newAction,
            buildState = existingState.buildState,
            uploadState = existingState.uploadState
        };
    }

    void SetAvatarState(VRCAvatarDescriptor vrcAvatarDescriptor, State newState, System.Exception exception = null) {
        var existingState = GetAvatarRootState(vrcAvatarDescriptor);

        Utils.LogMessage($"Internal State '{currentVrcAvatarDescriptor.gameObject.name}' '{existingState.state}' => '{newState}'");

        avatarStates[vrcAvatarDescriptor.gameObject.name] = new AvatarState() {
            state = newState,
            exception = exception,
            action = existingState.action,
            buildState = existingState.buildState,
            uploadState = existingState.uploadState
        };
    }

    void SetAvatarBuildState(VRCAvatarDescriptor vrcAvatarDescriptor, SdkBuildState? newBuildState) {
        var existingState = GetAvatarRootState(vrcAvatarDescriptor);
        
        Utils.LogMessage($"Build State '{currentVrcAvatarDescriptor.gameObject.name}' '{existingState.buildState}' => '{newBuildState}'");

        if (existingState.buildState == newBuildState) {
            return;
        }

        avatarStates[vrcAvatarDescriptor.gameObject.name] = new AvatarState() {
            state = existingState.state,
            exception = existingState.exception,
            action = existingState.action,
            buildState = newBuildState,
            uploadState = existingState.uploadState
        };
    }

    void SetAvatarUploadState(VRCAvatarDescriptor vrcAvatarDescriptor, SdkUploadState? newUploadState) {
        var existingState = GetAvatarRootState(vrcAvatarDescriptor);
        
        Utils.LogMessage($"Upload State '{currentVrcAvatarDescriptor.gameObject.name}' '{existingState.uploadState}' => '{newUploadState}'");

        if (existingState.uploadState == newUploadState) {
            return;
        }

        // NOTE: SDK changes upload state to empty while uploading for some reason

        avatarStates[vrcAvatarDescriptor.gameObject.name] = new AvatarState() {
            state = existingState.state,
            exception = existingState.exception,
            action = existingState.action,
            buildState = existingState.buildState,
            uploadState = newUploadState
        };
    }

// RENDER GUI

    void RenderAllAvatarsAndInScene() {
        GameObject[] rootObjects = GetRootObjectsInAllLoadedScenes();
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
        
        // GUILayout.Label($"B: {avatarState.buildState}  U: {avatarState.uploadState}  I: {avatarState.state}");

        switch (avatarState.state) {
            case State.Idle:
                GUI.contentColor = new Color(1f, 1f, 1f, 0.2f);
                GUILayout.Label("Waiting");
                GUI.contentColor = Color.white;
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

    void RegisterSDKCallback() {
        Utils.LogMessage("Registering SDK callbacks...");

        VRCSdkControlPanel.OnSdkPanelEnable += AddBuildHook;
    }

    void UnregisterSDKCallback() {
        Utils.LogMessage("Unregistering SDK callbacks...");

        VRCSdkControlPanel.OnSdkPanelEnable -= AddBuildHook;

        if (VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out var builder)) {
            Utils.LogMessage("Removing build hooks...");

            builder.OnSdkBuildStart -= OnBuildStarted;
            builder.OnSdkUploadStart -= OnUploadStarted;
            builder.OnSdkBuildStateChange -= OnSdkBuildStateChange;
            builder.OnSdkUploadStateChange -= OnSdkUploadStateChange;
        }
    }

    public void AddBuildHook(object sender, System.EventArgs e) {
        if (VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out var builder)) {
            Utils.LogMessage("Adding build hooks...");

            builder.OnSdkBuildStart += OnBuildStarted;
            builder.OnSdkUploadStart += OnUploadStarted;
            builder.OnSdkBuildStateChange += OnSdkBuildStateChange;
            builder.OnSdkUploadStateChange += OnSdkUploadStateChange;
        }
    }

    public void OnBuildStarted(object sender, object target) {
        var name = ((GameObject)target).name;
        Utils.LogMessage($"SDK.OnBuildStarted :: Building '{name}'...");

        currentVrcAvatarDescriptor = ((GameObject)target).GetComponent<VRCAvatarDescriptor>();

        SetAvatarState(currentVrcAvatarDescriptor, State.Building);
    }

    public void OnSdkBuildStateChange(object sender, SdkBuildState newState) {
        Utils.LogMessage($"SDK.OnSdkBuildStateChange :: Build state for '{currentVrcAvatarDescriptor.gameObject.name}' is now '{newState}'");

        SetAvatarBuildState(currentVrcAvatarDescriptor, newState);
        SetAvatarUploadState(currentVrcAvatarDescriptor, null);
    }

    public void OnUploadStarted(object sender, object target) {
        Utils.LogMessage($"SDK.OnUploadStarted :: Uploading...");

        SetAvatarState(currentVrcAvatarDescriptor, State.Uploading);
    }

    public void OnSdkUploadStateChange(object sender, SdkUploadState newState) {
        // NOTE: This gets called for build state change too for some reason

        Utils.LogMessage($"SDK.OnSdkUploadStateChange :: Upload state for '{currentVrcAvatarDescriptor.gameObject.name}' is now '{newState}'");

        SetAvatarBuildState(currentVrcAvatarDescriptor, null);
        SetAvatarUploadState(currentVrcAvatarDescriptor, newState);
    }

// VRCHAT SDK

    class VRCSDK_Extension {
        [InitializeOnLoad]
        public class PreuploadHook : IVRCSDKPreprocessAvatarCallback {
            // This has to be before -1024 when VRCSDK deletes our components
            public int callbackOrder => -90000;

            public bool OnPreprocessAvatar(GameObject clonedAvatarGameObject) {
                Utils.LogMessage($"SDK.OnPreprocessAvatar :: Process avatar '{clonedAvatarGameObject.name}'");
                return true;
            }
        }
    }

    public class OnBuildRequest : IVRCSDKBuildRequestedCallback {
        public int callbackOrder => -90001;

        public bool OnBuildRequested(VRCSDKRequestedBuildType requestedBuildType) {
            Utils.LogMessage($"SDK.OnBuildRequested :: Build type '{requestedBuildType}'");
            return true;
        }
    }
}
