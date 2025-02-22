using UnityEngine;
using UnityEditor;
using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using VRC.SDK3.Dynamics.Contact.Components;

namespace PeanutTools_VRC_Bulk_Upload {
    public class Utils {
        public static bool GetIsAndroid() {
            return EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android;
        }

        public static void LogMessage(string message) {
            Debug.Log($"VRCBulkUpload{(GetIsAndroid() ? " (A)" : "")} :: {message}", null);
        }

        public static void FocusGameObject(GameObject obj) {
            EditorGUIUtility.PingObject(obj);
        }

        public static void CreateDirectoryIfNoExist(string filePath) {
            if (File.Exists(filePath)) {
                return;
            }

            var fileName = Path.GetFileName(filePath);
            var fileDirectoryPath = filePath.Replace(fileName, "");
            Directory.CreateDirectory(fileDirectoryPath);
        }
    }
}