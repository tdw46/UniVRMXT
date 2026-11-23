using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UniVRMXT.MaterialsOverride;
using UniVRMXT.Format;
using UniVRMXT.Mtoonxt;
using UniVRMXT.Vfx;

namespace UniVRMXT.Editor.MaterialsOverride
{
    /// <summary>
    /// Soft-detect Extended-UniVRM <c>Vrm10ImportExtensionRegistry</c> and attach
    /// <c>VrmxtMaterialsOverrideInstance</c> (+ remembered textures) when hooks exist and
    /// are enabled in Project Settings/VRM10. Does <b>not</b> auto-Apply overrides —
    /// Editor preview uses Materialize. Stock UniVRM or hooks disabled → no Instance.
    /// </summary>
    [InitializeOnLoad]
    public static class VrmxtMaterialsOverrideImportHookBootstrap
    {
        private const string RegistryTypeName =
            "UniVRM10.Vrm10ImportExtensionRegistry, VRM10.Editor";

        private static readonly Action<object> Handler = OnVrmImported;
        private static bool s_registered;

        static VrmxtMaterialsOverrideImportHookBootstrap()
        {
            TryRegister();
        }

        /// <summary>
        /// True when the materials-override import handler is registered and Project
        /// Settings → VRM10 → Enable VRM Import Extensions is on.
        /// </summary>
        public static bool ImportHooksAvailable
        {
            get
            {
                // Require successful registration — registry type alone is not enough.
                if (!TryRegister())
                {
                    return false;
                }

                var registryType = Type.GetType(RegistryTypeName, throwOnError: false);
                if (registryType == null)
                {
                    return false;
                }

                return ReadIsEnabled(registryType);
            }
        }

        public static bool TryRegister()
        {
            if (s_registered)
            {
                return true;
            }

            var registryType = Type.GetType(RegistryTypeName, throwOnError: false);
            if (registryType == null)
            {
                return false;
            }

            var register = registryType.GetMethod(
                "RegisterHandler",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(Action<object>) },
                modifiers: null
            );
            if (register == null)
            {
                Debug.LogWarning(
                    "UniVRMXT: Vrm10ImportExtensionRegistry found but RegisterHandler(Action<object>) missing."
                );
                return false;
            }

            register.Invoke(null, new object[] { Handler });
            s_registered = true;
            return true;
        }

        private static bool ReadIsEnabled(Type registryType)
        {
            // Extended-UniVRM with project-setting gate. Older builds without IsEnabled → assume on.
            var prop = registryType.GetProperty(
                "IsEnabled",
                BindingFlags.Public | BindingFlags.Static
            );
            if (prop == null || prop.PropertyType != typeof(bool))
            {
                return true;
            }

            try
            {
                return (bool)prop.GetValue(null);
            }
            catch
            {
                return false;
            }
        }

        private static void OnVrmImported(object contextObj)
        {
            if (contextObj == null || !ImportHooksAvailable)
            {
                return;
            }

            try
            {
                AttachMaterialsOverrideStore(contextObj);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        private static void AttachMaterialsOverrideStore(object contextObj)
        {
            var type = contextObj.GetType();
            var root = type.GetProperty("Root")?.GetValue(contextObj) as GameObject;
            var json = type.GetProperty("Json")?.GetValue(contextObj) as string;
            var assetPath = type.GetProperty("AssetPath")?.GetValue(contextObj) as string;
            var addObject = type.GetMethod(
                "AddObjectToAsset",
                BindingFlags.Instance | BindingFlags.Public,
                binder: null,
                types: new[] { typeof(string), typeof(UnityEngine.Object) },
                modifiers: null
            );

            if (root == null || string.IsNullOrEmpty(json))
            {
                return;
            }

            // Authoring shell for materials-override (stock MToon until Materialize).
            // MToonXT Apply uses packaged VRMXT/MToonXT10 (Shader.Find).
            VrmxtMaterialsMtoonxtRuntime.TryAttachFromGltfJson(root, json, out _);
            VrmxtMaterialsMtoonxtApplier.Apply(root, json, ResolvePackageShader);
            // Persist only the portable authoring graph and original mesh/material slots.
            // Generated native layers are DontSave resources; the store's OnEnable
            // reconstructs them on an instantiated avatar, including player builds.
            root.GetComponent<VrmxtMaterialsMtoonxtAuxiliaryRenderer>()?.Configure(null, null);
            root.GetComponent<VrmxtStencilGraphRenderer>()?.Configure(null);
            if (root.GetComponent<VrmxtMaterialsMtoonxtInstance>()) PrepareOutlineDirections(root, assetPath);

            if (!VrmxtMaterialsOverrideRuntime.TryAttachFromGltfJson(root, json, out var store))
            {
                return;
            }

            VrmxtVfxGlbTextures glbTextures = null;

            try
            {
                // Drop prior-import images before pairing with this file's JSON so
                // Materialize's Instance texture resolve cannot hit stale indices.
                store.ClearImportedTextures();

                if (
                    !string.IsNullOrEmpty(assetPath)
                    && TryLoadGlbTextures(assetPath, out glbTextures)
                )
                {
                    store.RememberTexturesFromPairs(glbTextures.AsResolver(), json);
                    PersistImportedTextures(store, contextObj, addObject);
                    glbTextures.ReleaseOwnership();
                }
            }
            finally
            {
                glbTextures?.Dispose();
            }
        }

        static Shader ResolvePackageShader(string name)
        {
            const string shaders = "Packages/com.vrmxt.univrmxt/Runtime/Shaders/";
            string path = name switch
            {
                VrmxtMaterialsMtoonxt.BuiltinShaderName => "MToonxt/Builtin/vrmxt_materials_mtoonxt.shader",
                VrmxtMaterialsMtoonxt.UrpShaderName => "MToonxt/Urp/vrmxt_materials_mtoonxt_urp.shader",
                "Hidden/UniVRMXT/StencilCoverageMask" => "VrmxtStencilCoverageMask.shader",
                _ => null
            };
            return path == null ? Shader.Find(name) : AssetDatabase.LoadAssetAtPath<Shader>(shaders + path);
        }

        static void PrepareOutlineDirections(GameObject root, string assetPath)
        {
            var prepared = new System.Collections.Generic.HashSet<Mesh>();
            var usable = new System.Collections.Generic.HashSet<Material>();
            var excluded = new System.Collections.Generic.HashSet<Material>();
            bool Local(UnityEngine.Object asset)
            {
                var path = AssetDatabase.GetAssetPath(asset);
                return string.IsNullOrEmpty(path) || path == assetPath;
            }
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var mesh = renderer is SkinnedMeshRenderer skin ? skin.sharedMesh :
                    renderer.GetComponent<MeshFilter>()?.sharedMesh;
                if (!mesh) continue;
                foreach (var mat in renderer.sharedMaterials)
                {
                    if (!mat || !mat.HasProperty("_VRMXTGeometricOutline") || !Local(mat) ||
                        mat.GetFloat("_OutlineWidthMode") == 0 || mat.GetFloat("_OutlineWidth") <= 0) continue;
                    if (!prepared.Contains(mesh))
                    {
                        // Authored UV8 and external mesh assets remain owned by the artist.
                        if (!Local(mesh) || mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord7) ||
                            !mesh.isReadable || mesh.normals.Length != mesh.vertexCount ||
                            (!mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Tangent) &&
                             !mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord0)))
                        { excluded.Add(mat); continue; }
                        UniVRMXT.Mtoonxt.Editor.VrmxtOutlineDirections.Bake(mesh, mesh.subMeshCount);
                        prepared.Add(mesh);
                    }
                    usable.Add(mat);
                }
            }
            usable.ExceptWith(excluded);
            foreach (var mat in usable) mat.SetFloat("_VRMXTGeometricOutline", 1);
        }

        private static bool TryLoadGlbTextures(
            string assetPath,
            out VrmxtVfxGlbTextures glbTextures
        )
        {
            glbTextures = null;
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(assetPath);
            }
            catch (IOException)
            {
                return false;
            }

            return VrmxtVfxGlbTextures.TryCreate(bytes, out glbTextures);
        }

        private static void PersistImportedTextures(
            VrmxtMaterialsOverrideInstance store,
            object contextObj,
            MethodInfo addObject
        )
        {
            if (store == null || addObject == null)
            {
                return;
            }

            var textures = store.ImportedTextures;
            for (var i = 0; i < textures.Count; i++)
            {
                var entry = textures[i];
                if (entry?.Texture == null)
                {
                    continue;
                }

                entry.Texture.name = "VRMXT_mo_tex_" + entry.GltfIndex;
                addObject.Invoke(
                    contextObj,
                    new object[] { "vrmxt_mo_tex_" + entry.GltfIndex, entry.Texture }
                );
            }
        }
    }
}
