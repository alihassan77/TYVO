using System.IO;
using UnityEditor;
using UnityEngine;

namespace Mindstorm.Tyvo.Editor
{
    public static class SFPaths
    {
        public const string PackageName = "app.tyvo.skill";
        public const string DefaultOutputRoot = "Assets/StylizedFonts";
        public const string CustomAtlasShader = "TextMeshPro/Bitmap Custom Atlas";

        static string s_PackageRoot;

        /// <summary>Absolute path of this package on disk (embedded, local file: or git package).</summary>
        public static string PackageRoot
        {
            get
            {
                if (s_PackageRoot != null) return s_PackageRoot;
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(SFPaths).Assembly);
                s_PackageRoot = info != null ? info.resolvedPath : Path.GetFullPath("Packages/" + PackageName);
                s_PackageRoot = s_PackageRoot.Replace('\\', '/');
                return s_PackageRoot;
            }
        }

        public static string PhotoshopFolder => PackageRoot + "/Photoshop~";
        public static string PackageStylesFolder => PackageRoot + "/Styles~";

        public static string ProjectRoot => Path.GetDirectoryName(Application.dataPath).Replace('\\', '/');

        /// <summary>Working files (raw atlas, job, layered PSD, reports). Outside Assets so they never ship.</summary>
        public static string WorkFolder(string assetName)
        {
            string dir = ProjectRoot + "/StylizedFontsWork/" + Sanitize(assetName);
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static string Abs(string assetPath) => Path.GetFullPath(assetPath).Replace('\\', '/');

        public static string ToAssetPath(string absolute)
        {
            absolute = Path.GetFullPath(absolute).Replace('\\', '/');
            string root = ProjectRoot + "/";
            return absolute.StartsWith(root) ? absolute.Substring(root.Length) : absolute;
        }

        public static string Sanitize(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Trim();
        }

        public static void EnsureAssetFolder(string assetFolder)
        {
            assetFolder = assetFolder.Replace('\\', '/').TrimEnd('/');
            if (AssetDatabase.IsValidFolder(assetFolder)) return;
            string parent = Path.GetDirectoryName(assetFolder).Replace('\\', '/');
            EnsureAssetFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(assetFolder));
        }
    }
}
