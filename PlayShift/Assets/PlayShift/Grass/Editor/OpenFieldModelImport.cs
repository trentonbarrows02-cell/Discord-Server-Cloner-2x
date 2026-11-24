using UnityEditor;

namespace PlayShift.Grass.Editor
{
    /// <summary>
    /// Import rules for the Open Field scenery models (Resources/OpenField: fence panel, trees, bushes). The field
    /// builder merges hundreds of copies into a few meshes at runtime, so the meshes must be readable; materials are
    /// made in code (one textured lit material per model), so none are imported.
    /// </summary>
    internal sealed class OpenFieldModelImport : AssetPostprocessor
    {
        private const string Folder = "Assets/PlayShift/Grass/Resources/OpenField/";

        private void OnPreprocessModel()
        {
            if (!assetPath.StartsWith(Folder)) return;
            var mi = (ModelImporter)assetImporter;
            mi.isReadable = true;
            mi.materialImportMode = ModelImporterMaterialImportMode.None;
            mi.importAnimation = false;
            mi.importCameras = false;
            mi.importLights = false;
            mi.globalScale = 1f;
            mi.useFileScale = false;
            mi.meshCompression = ModelImporterMeshCompression.Off;
            mi.importNormals = ModelImporterNormals.Import;
        }

        private void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(Folder)) return;
            var ti = (TextureImporter)assetImporter;
            ti.maxTextureSize = 1024;
            ti.mipmapEnabled = true;
            ti.anisoLevel = 4;
        }
    }
}
