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
        public static void FocusGameObject(GameObject obj) {
            EditorGUIUtility.PingObject(obj);
        }
    }
}