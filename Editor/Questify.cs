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
using UnityEditor.SceneManagement;

namespace PeanutTools_VRC_Bulk_Upload {
    public static class Questify {
        public static VRCAvatarDescriptor QuestifyAvatar(VRCAvatarDescriptor vrcAvatarDescriptor) {
            Utils.LogMessage($"Questifying '{vrcAvatarDescriptor.gameObject.name}'...");

#if VRC_QUESTIFYER_INSTALLED
            Transform newVrcAvatar = PeanutTools_VRCQuestifyer.Questifyer.CloneAndQuestifyAvatar(vrcAvatarDescriptor);
            return newVrcAvatar.GetComponent<VRCAvatarDescriptor>();
#else
            Utils.LogMessage($"Questifyer not installed");
            return null;
#endif
        }

        public static string GetActiveScenePath() {
            var scene = EditorSceneManager.GetActiveScene();
            string scenePath = scene.path;
            return scenePath;
        }

        public static void CreateQuestScene() {
            Utils.LogMessage($"Create Quest scene");

            var originalScene = EditorSceneManager.GetActiveScene();
            string originalScenePath = originalScene.path;
            string originalSceneName = originalScene.name;

            if (originalSceneName.Contains("[Quest]")) {
                Debug.LogError("VRC_Bulk_Upload :: Scene is already Quest");
                return;
            }

            string questSceneName = $"{originalSceneName} [Quest]";
            string questScenePath = Path.Combine(Path.GetDirectoryName(originalScenePath), $"{questSceneName}.unity");

            if (File.Exists(questScenePath)) {
                Utils.LogMessage("Quest version already exists. Deleting existing quest scene...");
                AssetDatabase.DeleteAsset(questScenePath);
            } else {
                Utils.LogMessage("Scene does not exist, creating...");
            }

            Utils.LogMessage("Duplicating current scene...");
            AssetDatabase.CopyAsset(originalScenePath, questScenePath);

            Utils.LogMessage("Loading quest scene...");
            EditorSceneManager.OpenScene(questScenePath, OpenSceneMode.Single);
        }

        public static void SwitchScene(string scenePath) {
            Utils.LogMessage("Saving scene...");

            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());

            Utils.LogMessage("Reloading original scene...");

            EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
        }
    }
}