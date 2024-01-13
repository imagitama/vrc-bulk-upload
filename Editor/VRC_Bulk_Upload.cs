using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
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
        Test,
        BuildAndUpload
    }

    struct AvatarState {
        public Action action;
        public State state;
        public string successfulBuildTime;
        public SdkBuildState? buildState;
        public SdkUploadState? uploadState;
        public System.Exception exception;
    }

    Vector2 scrollPosition;
    static Dictionary<string, AvatarState> avatarStates = new Dictionary<string, AvatarState>();
    static CancellationTokenSource GetAvatarCancellationToken = new CancellationTokenSource();
    static CancellationTokenSource BuildAndUploadCancellationToken = new CancellationTokenSource();
    static VRCAvatarDescriptor currentVrcAvatarDescriptor;

    [MenuItem("Tools/VRC Bulk Upload")]
    public static void ShowWindow() {
        var window = GetWindow<VRC_Bulk_Upload>();
        window.titleContent = new GUIContent("VRC Bulk Upload");
        window.minSize = new Vector2(400, 200);
    }

    void OnGUI() {
        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

        CustomGUI.LargeLabel("VRC Bulk Upload");
        CustomGUI.ItalicLabel("Bulks and uploads all active VRChat avatars in your scenes.");

        CustomGUI.LineGap();
        
        CustomGUI.LargeLabel("Avatars In Scenes");

        CustomGUI.LineGap();

        RenderAllAvatarsAndInScene();

        CustomGUI.LineGap();

        int count = GetActiveVrchatAvatars().Length;

        if (CustomGUI.PrimaryButton($"Build And Upload All ({count})")) {
            // if (EditorUtility.DisplayDialog("Confirm", $"Are you sure you want to build and upload {count.ToString()} VRChat avatars?", "Yes", "No")) {
                BuildAndUploadAllAvatars();
            // }
        }

        EditorGUILayout.EndScrollView();
    }

// HELPERS

    async Task<VRCAvatar> GetVRCAvatarFromDescriptor(VRCAvatarDescriptor vrcAvatarDescriptor) {
        var blueprintId = vrcAvatarDescriptor.GetComponent<PipelineManager>().blueprintId;

        Debug.Log($"VRC_Bulk_Upload :: Fetching avatar for '{vrcAvatarDescriptor.gameObject.name}' ('{blueprintId}')...");

        var avatarData = await VRCApi.GetAvatar(blueprintId, true, cancellationToken: GetAvatarCancellationToken.Token);

        return avatarData;
    }

    async Task BuildAndUploadAllAvatars() {
        var activeVrchatAvatars = GetActiveVrchatAvatars();

        foreach (var activeVrchatAvatar in activeVrchatAvatars) {
            SetAvatarState(activeVrchatAvatar, State.Idle);
        Debug.Log($"VRC_Bulk_Upload :: Building and uploading {activeVrchatAvatars.Length} VRChat avatars...");

        foreach (var activeVrchatAvatar in activeVrchatAvatars) {
            await BuildAndUploadAvatar(activeVrchatAvatar);
        }
    }

    async Task BuildAvatar(VRCAvatarDescriptor vrcAvatarDescriptor) {
        Debug.Log($"VRC_Bulk_Upload :: Building '{vrcAvatarDescriptor.gameObject.name}'...");
        
        try {
            if (!VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out var builder)) {
                throw new System.Exception("No builder found");
            }

            currentVrcAvatarDescriptor = vrcAvatarDescriptor;
            SetAvatarAction(vrcAvatarDescriptor, Action.Build);

            string bundlePath = await builder.Build(vrcAvatarDescriptor.gameObject);
            
            Debug.Log($"VRC_Bulk_Upload :: '{vrcAvatarDescriptor.gameObject.name}' built to '{bundlePath}'");

            SetAvatarState(vrcAvatarDescriptor, State.Success);
        } catch (System.Exception e) {
            SetAvatarState(vrcAvatarDescriptor, State.Failed, e);
            Debug.LogError(e);
        }
    }

    async Task BuildAndUploadAvatar(VRCAvatarDescriptor vrcAvatarDescriptor) {
        Debug.Log($"VRC_Bulk_Upload :: Building and uploading '{vrcAvatarDescriptor.gameObject.name}'...");

        try {
            if (!VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out var builder)) {
                throw new System.Exception("No builder found");
            }

            currentVrcAvatarDescriptor = vrcAvatarDescriptor;
            SetAvatarAction(vrcAvatarDescriptor, Action.BuildAndUpload);

            var vrcAvatar = await GetVRCAvatarFromDescriptor(vrcAvatarDescriptor);

            // TODO: Support thumbnail image upload?
            await builder.BuildAndUpload(vrcAvatarDescriptor.gameObject, vrcAvatar, cancellationToken: BuildAndUploadCancellationToken.Token);
        
            SetAvatarState(vrcAvatarDescriptor, State.Success);
        } catch (System.Exception e) {
            SetAvatarState(vrcAvatarDescriptor, State.Failed, e);
            Debug.LogError(e);
        }
    }


    async Task BuildAndTestAvatar(VRCAvatarDescriptor vrcAvatarDescriptor) {
        Debug.Log($"VRC_Bulk_Upload :: Building and testing '{vrcAvatarDescriptor.gameObject.name}'...");

        if (!VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out var builder)) {
            throw new System.Exception("No builder found");
        }

        try {
            await builder.BuildAndTest(vrcAvatarDescriptor.gameObject);
        } catch (System.Exception e) {
            SetAvatarState(vrcAvatarDescriptor, State.Failed, e);
            Debug.LogError(e);
        }
    }

    GameObject[] GetRootObjects() {
        int countLoaded = SceneManager.sceneCount;
        Scene[] scenes = new Scene[countLoaded];
 
        for (int i = 0; i < countLoaded; i++)
        {
            scenes[i] = SceneManager.GetSceneAt(i);
        }

        GameObject[] rootObjects = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();

        foreach (var scene in scenes) {
            if (scene.isLoaded && scene != UnityEngine.SceneManagement.SceneManager.GetActiveScene()) {
                rootObjects = rootObjects.Concat(scene.GetRootGameObjects()).ToArray();
            }
        }

        return rootObjects.ToArray();
    }

    VRCAvatarDescriptor[] GetActiveVrchatAvatars() {
        GameObject[] rootObjects = GetRootObjects();
        
        var vrcAvatarDescriptors = new List<VRCAvatarDescriptor>();

        foreach (var rootObject in rootObjects) {
            VRCAvatarDescriptor vrcAvatarDescriptor = rootObject.GetComponent<VRCAvatarDescriptor>();
            bool isActive = rootObject.activeInHierarchy;

            if (isActive && vrcAvatarDescriptor != null) {
                vrcAvatarDescriptors.Add(vrcAvatarDescriptor);
            }
        }

        return vrcAvatarDescriptors.ToArray();
    }

    bool GetCanAvatarBeBuilt(VRCAvatarDescriptor vrcAvatarDescriptor) {
        return vrcAvatarDescriptor != null && vrcAvatarDescriptor.gameObject.GetComponent<Animator>() != null;
    }

    bool GetCanAvatarBeUploaded(VRCAvatarDescriptor vrcAvatarDescriptor) {
        return GetCanAvatarBeBuilt(vrcAvatarDescriptor) && vrcAvatarDescriptor.gameObject.GetComponent<PipelineManager>().blueprintId != null;
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

        if (newState == State.Success) {
            existingState.successfulBuildTime = DateTime.Now.ToString("h:mm:ss tt");
        }
        else {
            existingState.successfulBuildTime = null;
        }
        
        SetAvatarRootState(vrcAvatarDescriptor, existingState);
    }

    static void SetAvatarBuildState(VRCAvatarDescriptor vrcAvatarDescriptor, SdkBuildState? newBuildState) {
        var existingState = GetAvatarRootState(vrcAvatarDescriptor);

        Debug.Log($"VRC_Bulk_Upload :: Build State '{currentVrcAvatarDescriptor.gameObject.name}' '{existingState.buildState}' => '{newBuildState}'");

        existingState.buildState = newBuildState;
        
        SetAvatarRootState(vrcAvatarDescriptor, existingState);
    }

    static void SetAvatarUploadState(VRCAvatarDescriptor vrcAvatarDescriptor, SdkUploadState? newUploadState) {
        var existingState = GetAvatarRootState(vrcAvatarDescriptor);
        
        Debug.Log($"VRC_Bulk_Upload :: Upload State '{currentVrcAvatarDescriptor.gameObject.name}' '{existingState.uploadState}' => '{newUploadState}'");

        existingState.uploadState = newUploadState;
        
        SetAvatarRootState(vrcAvatarDescriptor, existingState);
    }

    //     void SetAvatarState(VRCAvatarDescriptor vrcAvatarDescriptor, State newState, SdkBuildState? newBuildState, SdkUploadState? newUploadState, System.Exception exception = null) {
    //     var existingState = avatarStates[vrcAvatarDescriptor];
        
    //     avatarStates[vrcAvatarDescriptor] = new AvatarState() {
    //         state = ((existingState != null && newState == null) ? existingState.state : newState),
    //         buildState = newBuildState,
    //         uploadState = newUploadState,
    //         exception = exception
    //     };
    // }

// RENDER GUI

    void RenderAllAvatarsAndInScene() {
        GameObject[] rootObjects = GetRootObjects();

        var hasRenderedAtLeastOne = false;

        foreach (var rootObject in rootObjects) {
            VRCAvatarDescriptor vrcAvatarDescriptor = rootObject.GetComponent<VRCAvatarDescriptor>();
            bool isActive = rootObject.activeInHierarchy;

            if (isActive && vrcAvatarDescriptor != null) {
                if (hasRenderedAtLeastOne) {
                    CustomGUI.LineGap();
                } else {
                    hasRenderedAtLeastOne = true;
                }

                CustomGUI.MediumLabel($"{rootObject.name}");

                GUILayout.BeginHorizontal();

                if (CustomGUI.TinyButton("View")) {
                    Utils.FocusGameObject(rootObject);
                }

                EditorGUI.BeginDisabledGroup(!GetCanAvatarBeBuilt(vrcAvatarDescriptor));
                if (CustomGUI.TinyButton("Build")) {
                    BuildAvatar(vrcAvatarDescriptor);
                }

                if (CustomGUI.TinyButton("Test")) {
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

    void RenderAvatarState(VRCAvatarDescriptor vrcAvatarDescriptor) {
        AvatarState avatarState = GetAvatarRootState(vrcAvatarDescriptor);

        // Debug.Log($"Rendering '{vrcAvatarDescriptor.gameObject.name}' action '{avatarState.action}' state '{avatarState.state}'");

        switch (avatarState.state) {
            case State.Idle:
                GUI.contentColor = Color.white;
                GUILayout.Label("");
                break;
            case State.Building:
                GUI.contentColor = new Color(0.8f, 0.8f, 1f, 1);
                GUILayout.Label("Building");
                GUI.contentColor = Color.white;
                break;
            case State.Uploading:
                GUI.contentColor = new Color(0.8f, 0.8f, 1f, 1);
                GUILayout.Label("Uploading");
                GUI.contentColor = Color.white;
                break;
            case State.Success:
                GUI.contentColor = new Color(0.8f, 1f, 0.8f, 1);
                GUILayout.Label("Success" + (avatarState.successfulBuildTime != null ? $" ~ {avatarState.successfulBuildTime}" : ""));
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

        //                 // Build Events
        // event EventHandler<object> OnSdkBuildStart;
        // event EventHandler<string> OnSdkBuildProgress;
        // event EventHandler<string> OnSdkBuildFinish;
        // event EventHandler<string> OnSdkBuildSuccess;
        // event EventHandler<string> OnSdkBuildError;

        // event EventHandler<SdkBuildState> OnSdkBuildStateChange;
        // SdkBuildState BuildState { get; }

        // // Upload Events
        // event EventHandler OnSdkUploadStart;
        // event EventHandler<(string status, float percentage)> OnSdkUploadProgress;
        // event EventHandler<string> OnSdkUploadFinish;
        // event EventHandler<string> OnSdkUploadSuccess;
        // event EventHandler<string> OnSdkUploadError;

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

            if (newState == SdkBuildState.Success && GetAvatarRootState(currentVrcAvatarDescriptor).action == Action.Build) {
                SetAvatarState(currentVrcAvatarDescriptor, State.Success);
            }
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

            if (newState == SdkUploadState.Success) {
                SetAvatarState(currentVrcAvatarDescriptor, State.Success);
            }
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
