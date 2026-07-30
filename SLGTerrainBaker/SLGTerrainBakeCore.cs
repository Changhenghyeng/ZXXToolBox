using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace SLGTerrainBakerTool.Editor
{
    public enum SLGTerrainBakeCompression
    {
        Uncompressed,
        MobileAstc6x6
    }

    [Serializable]
    public sealed class SLGTerrainBakeSettings
    {
        public Material SourceMaterial;
        public int TerrainResolution = 1024;
        public int NoiseResolution = 1024;
        public int FarBasemapResolution = 1024;
        public string OutputFolder = "Assets/SLGTerrainMobile";
        public SLGTerrainBakeCompression Compression =
            SLGTerrainBakeCompression.MobileAstc6x6;
    }

    public sealed class SLGTerrainBakeResult
    {
        public Material RuntimeMaterial;
        public Texture2DArray TerrainArray;
        public Texture2DArray NoiseArray;
        public Texture2D FarBasemap;
        public string MaterialPath;
        public List<string> Warnings = new List<string>();
    }

    public static class SLGTerrainBakeCore
    {
        public const string SourceShaderName = "Custom/Terrain Blend Lite";
        public const string RuntimeShaderName = "Custom/Terrain Blend Mobile";
        public const string BakeShaderName =
            "Hidden/SLG/Terrain Mobile Baker";

        private const string RuntimeShaderAssetSearchName =
            "SLG_TerrainBlend_Baked";
        private const int TerrainTypeCount = 4;
        private const int TerrainArrayDepth = 8;
        private const int NoiseLodCount = 3;

        private static readonly string[] RequiredSourceProperties =
        {
            "_ControlMap",
            "_BottomBase",
            "_Terrain0Base",
            "_Terrain1Base",
            "_Terrain2Base",
            "_Terrain3Base"
        };

        private static readonly string[] BaseProperties =
        {
            "_Terrain0Base",
            "_Terrain1Base",
            "_Terrain2Base",
            "_Terrain3Base"
        };

        private static readonly string[] VariantProperties =
        {
            "_Terrain0Variant",
            "_Terrain1Variant",
            "_Terrain2Variant",
            "_Terrain3Variant"
        };

        private static readonly string[] TintProperties =
        {
            "_Terrain0Tint",
            "_Terrain1Tint",
            "_Terrain2Tint",
            "_Terrain3Tint"
        };

        private static readonly string[] RatioProperties =
        {
            "_Terrain0VariantRatio",
            "_Terrain1VariantRatio",
            "_Terrain2VariantRatio",
            "_Terrain3VariantRatio"
        };

        private static readonly string[] BaseStProperties =
        {
            "_BaseST0",
            "_BaseST1",
            "_BaseST2",
            "_BaseST3"
        };

        private static readonly string[] VariantStProperties =
        {
            "_VariantST0",
            "_VariantST1",
            "_VariantST2",
            "_VariantST3"
        };

        private static readonly string[] TypeNames =
        {
            "Type A",
            "Type B",
            "Type C",
            "Type D"
        };

        public static bool IsSupportedSource(Material material)
        {
            if (material == null || material.shader == null)
            {
                return false;
            }

            foreach (string property in RequiredSourceProperties)
            {
                if (!material.HasProperty(property))
                {
                    return false;
                }
            }

            return true;
        }

        public static bool Validate(
            SLGTerrainBakeSettings settings,
            out List<string> errors,
            out List<string> warnings)
        {
            errors = new List<string>();
            warnings = new List<string>();

            if (settings == null)
            {
                errors.Add("烘焙设置为空。");
                return false;
            }

            Material source = settings.SourceMaterial;
            if (source == null)
            {
                errors.Add("请选择制作版地面材质。");
                return false;
            }

            if (!IsSupportedSource(source))
            {
                string shaderName = source.shader != null
                    ? source.shader.name
                    : "无 Shader";
                errors.Add(
                    $"当前 Shader（{shaderName}）缺少地形烘焙所需属性，无法作为制作材质。");
            }

            ValidateResolution(
                settings.TerrainResolution,
                256,
                2048,
                "地貌纹理数组分辨率",
                errors);
            ValidateResolution(
                settings.NoiseResolution,
                128,
                1024,
                "Noise分辨率",
                errors);
            ValidateResolution(
                settings.FarBasemapResolution,
                256,
                4096,
                "远景图分辨率",
                errors);

            string outputFolder =
                NormalizeAssetPath(settings.OutputFolder);
            if (!IsProjectFolder(outputFolder))
            {
                errors.Add(
                    "输出目录必须位于当前项目的 Assets 文件夹内。");
            }

            ValidateMaterialTextures(source, errors, warnings);
            ValidateExistingOutputs(settings, errors);

            if (Shader.Find(BakeShaderName) == null)
            {
                errors.Add(
                    $"找不到烘焙 Shader：{BakeShaderName}。请等待Unity编译完成。");
            }

            if (FindRuntimeShader() == null)
            {
                errors.Add(
                    $"找不到运行时地形 Shader（当前名称：{RuntimeShaderName}）。请等待Unity编译完成。");
            }

            warnings.Add(
                "手机材质会保留整张地图的四种地貌，但每个像素只混合权重最高的两种；" +
                "三种以上地貌交汇处会舍弃较低权重。");
            return errors.Count == 0;
        }

        public static string GetDefaultOutputFolder(Material source)
        {
            if (source == null)
            {
                return "Assets/SLGTerrainMobile";
            }

            string sourcePath = AssetDatabase.GetAssetPath(source);
            string sourceFolder = Path.GetDirectoryName(sourcePath);
            if (string.IsNullOrEmpty(sourceFolder))
            {
                return "Assets/SLGTerrainMobile";
            }

            return NormalizeAssetPath(
                $"{sourceFolder}/BakedTerrain/" +
                SanitizeFileName(source.name));
        }

        public static string[] GetExpectedOutputPaths(
            SLGTerrainBakeSettings settings)
        {
            if (settings == null || settings.SourceMaterial == null)
            {
                return Array.Empty<string>();
            }

            string folder = NormalizeAssetPath(settings.OutputFolder);
            string safeName =
                SanitizeFileName(settings.SourceMaterial.name);
            return new[]
            {
                $"{folder}/{safeName}_TerrainArray.asset",
                $"{folder}/{safeName}_PackedNoise.asset",
                $"{folder}/{safeName}_FarBasemap.asset",
                $"{folder}/{safeName}_Mobile.mat"
            };
        }

        public static SLGTerrainBakeResult Bake(
            SLGTerrainBakeSettings settings,
            Func<string, float, bool> cancelProgress,
            out string error)
        {
            error = null;
            if (!Validate(
                settings,
                out List<string> errors,
                out List<string> warnings))
            {
                error = string.Join("\n", errors);
                return null;
            }

            Material source = settings.SourceMaterial;
            Shader runtimeShader = FindRuntimeShader();
            Material bakeMaterial = new Material(
                Shader.Find(BakeShaderName))
            {
                hideFlags = HideFlags.HideAndDontSave
            };

            Texture2DArray generatedArray = null;
            Texture2DArray generatedNoise = null;
            Texture2D generatedFar = null;

            try
            {
                ConfigureBakeMaterial(bakeMaterial, source);
                int totalSteps =
                    TerrainArrayDepth + NoiseLodCount + 1;
                int completedSteps = 0;

                generatedArray = BakeTerrainArray(
                    bakeMaterial,
                    source,
                    settings,
                    (message) =>
                    {
                        bool cancel = ReportProgress(
                            cancelProgress,
                            message,
                            completedSteps,
                            totalSteps);
                        completedSteps++;
                        return cancel;
                    });

                generatedNoise = BakeNoiseArray(
                    bakeMaterial,
                    settings.NoiseResolution,
                    settings.Compression,
                    (lod, count) =>
                    {
                        bool cancel = ReportProgress(
                            cancelProgress,
                            $"正在生成 Noise LOD：Layer {lod}/{count - 1}",
                            completedSteps,
                            totalSteps);
                        completedSteps++;
                        return cancel;
                    });

                if (ReportProgress(
                    cancelProgress,
                    "正在生成 LOD3 远景图",
                    completedSteps,
                    totalSteps))
                {
                    throw new OperationCanceledException();
                }

                generatedFar = RenderColorTexture(
                    bakeMaterial,
                    0,
                    settings.FarBasemapResolution,
                    "FarBasemap",
                    settings.Compression,
                    TextureWrapMode.Clamp);

                EnsureFolder(settings.OutputFolder);
                string[] paths = GetExpectedOutputPaths(settings);
                Texture2DArray savedArray =
                    SaveOrUpdateTextureArray(
                        generatedArray,
                        paths[0]);
                generatedArray = null;
                Texture2DArray savedNoise =
                    SaveOrUpdateTextureArray(
                        generatedNoise,
                        paths[1],
                        true);
                generatedNoise = null;
                Texture2D savedFar =
                    SaveOrUpdateTexture(generatedFar, paths[2]);
                generatedFar = null;

                Material runtimeMaterial =
                    SaveOrUpdateRuntimeMaterial(
                        paths[3],
                        runtimeShader,
                        source,
                        savedArray,
                        savedNoise,
                        savedFar);

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                return new SLGTerrainBakeResult
                {
                    RuntimeMaterial = runtimeMaterial,
                    TerrainArray = savedArray,
                    NoiseArray = savedNoise,
                    FarBasemap = savedFar,
                    MaterialPath = paths[3],
                    Warnings = warnings
                };
            }
            catch (OperationCanceledException)
            {
                error = "已取消烘焙。";
                return null;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                error = $"烘焙失败：{exception.Message}";
                return null;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(bakeMaterial);
                DestroyGenerated(generatedArray);
                DestroyGenerated(generatedNoise);
                DestroyGenerated(generatedFar);
            }
        }

        public static float EstimateRuntimeTextureMemoryMb(
            SLGTerrainBakeSettings settings)
        {
            if (settings == null)
            {
                return 0f;
            }

            double colorBitsPerPixel =
                settings.Compression ==
                SLGTerrainBakeCompression.MobileAstc6x6
                    ? 128.0 / 36.0
                    : 32.0;
            double noiseBitsPerPixel =
                settings.Compression ==
                SLGTerrainBakeCompression.MobileAstc6x6
                    ? 8.0
                    : 32.0;
            double colorPixels =
                Square(settings.TerrainResolution) *
                    TerrainArrayDepth *
                    (4.0 / 3.0) +
                Square(settings.FarBasemapResolution) *
                    (4.0 / 3.0);
            double noisePixels =
                Square(settings.NoiseResolution) *
                NoiseLodCount *
                (4.0 / 3.0);
            return (float)(
                (colorPixels * colorBitsPerPixel +
                 noisePixels * noiseBitsPerPixel) / 8.0 /
                (1024.0 * 1024.0));
        }

        private static Texture2DArray BakeTerrainArray(
            Material bakeMaterial,
            Material source,
            SLGTerrainBakeSettings settings,
            Func<string, bool> cancelSlice)
        {
            var slices = new Texture2D[TerrainArrayDepth];
            bool useVariants = UsesVariants(source);
            try
            {
                for (int slice = 0; slice < TerrainArrayDepth; slice++)
                {
                    int terrainIndex = slice % TerrainTypeCount;
                    bool variant = slice >= TerrainTypeCount;
                    string sourceProperty =
                        variant && useVariants
                            ? VariantProperties[terrainIndex]
                            : BaseProperties[terrainIndex];
                    string label = variant
                        ? $"{TypeNames[terrainIndex]} 次图"
                        : $"{TypeNames[terrainIndex]} 主图";

                    if (cancelSlice != null &&
                        cancelSlice($"正在整理地貌数组：{label}"))
                    {
                        throw new OperationCanceledException();
                    }

                    bakeMaterial.SetTexture(
                        "_CopyTex",
                        source.GetTexture(sourceProperty));
                    slices[slice] = RenderColorTexture(
                        bakeMaterial,
                        2,
                        settings.TerrainResolution,
                        $"TerrainSlice_{slice}",
                        settings.Compression,
                        TextureWrapMode.Repeat);
                }

                TextureFormat format =
                    settings.Compression ==
                    SLGTerrainBakeCompression.MobileAstc6x6
                        ? TextureFormat.ASTC_6x6
                        : TextureFormat.RGB24;
                var array = new Texture2DArray(
                    settings.TerrainResolution,
                    settings.TerrainResolution,
                    TerrainArrayDepth,
                    format,
                    true,
                    false)
                {
                    name = "TerrainArray",
                    filterMode = FilterMode.Trilinear,
                    wrapMode = TextureWrapMode.Repeat,
                    anisoLevel = 1
                };

                for (int slice = 0; slice < TerrainArrayDepth; slice++)
                {
                    Texture2D texture = slices[slice];
                    for (int mip = 0; mip < texture.mipmapCount; mip++)
                    {
                        array.SetPixelData(
                            texture.GetPixelData<byte>(mip),
                            mip,
                            slice);
                    }
                }
                array.Apply(false, false);
                return array;
            }
            finally
            {
                foreach (Texture2D slice in slices)
                {
                    DestroyGenerated(slice);
                }
            }
        }

        private static Texture2DArray BakeNoiseArray(
            Material bakeMaterial,
            int resolution,
            SLGTerrainBakeCompression compression,
            Func<int, int, bool> cancelLayer)
        {
            var slices = new Texture2D[NoiseLodCount];
            try
            {
                for (int lod = 0; lod < NoiseLodCount; lod++)
                {
                    if (cancelLayer != null &&
                        cancelLayer(lod, NoiseLodCount))
                    {
                        throw new OperationCanceledException();
                    }

                    bakeMaterial.SetFloat("_NoiseBakeLod", lod);
                    slices[lod] = RenderNoiseTexture(
                        bakeMaterial,
                        resolution,
                        $"NoiseLod_{lod}",
                        compression);
                }

                TextureFormat format =
                    compression ==
                    SLGTerrainBakeCompression.MobileAstc6x6
                        ? TextureFormat.ASTC_4x4
                        : TextureFormat.RGBA32;
                var output = new Texture2DArray(
                    resolution,
                    resolution,
                    NoiseLodCount,
                    format,
                    true,
                    true);
                output.name = "NoiseLodArray";
                output.filterMode = FilterMode.Trilinear;
                output.wrapMode = TextureWrapMode.Clamp;
                output.anisoLevel = 1;

                for (int slice = 0;
                    slice < NoiseLodCount;
                    slice++)
                {
                    Texture2D texture = slices[slice];
                    for (int mip = 0;
                        mip < texture.mipmapCount;
                        mip++)
                    {
                        output.SetPixelData(
                            texture.GetPixelData<byte>(mip),
                            mip,
                            slice);
                    }
                }

                output.Apply(false, false);
                return output;
            }
            finally
            {
                foreach (Texture2D slice in slices)
                {
                    DestroyGenerated(slice);
                }
            }
        }

        private static Texture2D RenderNoiseTexture(
            Material material,
            int resolution,
            string textureName,
            SLGTerrainBakeCompression compression)
        {
            Color[] pixels = RenderPixels(
                material,
                1,
                resolution,
                true);
            var output = new Texture2D(
                resolution,
                resolution,
                TextureFormat.RGBA32,
                true,
                true)
            {
                name = textureName,
                filterMode = FilterMode.Trilinear,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 1
            };
            output.SetPixels(pixels, 0);
            output.Apply(true, false);

            if (compression ==
                SLGTerrainBakeCompression.MobileAstc6x6)
            {
                EditorUtility.CompressTexture(
                    output,
                    TextureFormat.ASTC_4x4,
                    TextureCompressionQuality.Best);
            }

            return output;
        }

        private static Texture2D RenderColorTexture(
            Material material,
            int pass,
            int resolution,
            string textureName,
            SLGTerrainBakeCompression compression,
            TextureWrapMode wrapMode)
        {
            Color[] pixels = RenderPixels(
                material,
                pass,
                resolution,
                false);
            var output = new Texture2D(
                resolution,
                resolution,
                TextureFormat.RGB24,
                true,
                false)
            {
                name = textureName,
                filterMode = FilterMode.Trilinear,
                wrapMode = wrapMode,
                anisoLevel = 1
            };
            output.SetPixels(pixels, 0);
            output.Apply(true, false);

            if (compression ==
                SLGTerrainBakeCompression.MobileAstc6x6)
            {
                EditorUtility.CompressTexture(
                    output,
                    TextureFormat.ASTC_6x6,
                    TextureCompressionQuality.Best);
            }
            return output;
        }

        private static Color[] RenderPixels(
            Material material,
            int pass,
            int size,
            bool linear)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture renderTexture = RenderTexture.GetTemporary(
                size,
                size,
                0,
                RenderTextureFormat.ARGB32,
                linear
                    ? RenderTextureReadWrite.Linear
                    : RenderTextureReadWrite.sRGB);
            renderTexture.wrapMode = TextureWrapMode.Clamp;
            renderTexture.filterMode = FilterMode.Bilinear;

            Texture2D readback = null;
            try
            {
                Graphics.Blit(
                    Texture2D.whiteTexture,
                    renderTexture,
                    material,
                    pass);
                RenderTexture.active = renderTexture;
                readback = new Texture2D(
                    size,
                    size,
                    TextureFormat.RGBA32,
                    false,
                    linear);
                readback.ReadPixels(
                    new Rect(0, 0, size, size),
                    0,
                    0,
                    false);
                readback.Apply(false, false);
                return readback.GetPixels();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(renderTexture);
                DestroyGenerated(readback);
            }
        }

        private static void ConfigureBakeMaterial(
            Material destination,
            Material source)
        {
            CopyTextureAndTransform(
                source,
                "_ControlMap",
                destination,
                "_ControlMap");
            CopyTextureAndTransform(
                source,
                "_BottomBase",
                destination,
                "_BottomBase");

            for (int i = 0; i < TerrainTypeCount; i++)
            {
                CopyTextureAndTransform(
                    source,
                    BaseProperties[i],
                    destination,
                    BaseProperties[i]);
                CopyTextureAndTransform(
                    source,
                    VariantProperties[i],
                    destination,
                    VariantProperties[i]);
                destination.SetColor(
                    TintProperties[i],
                    GetColor(
                        source,
                        TintProperties[i],
                        Color.white));
                destination.SetFloat(
                    RatioProperties[i],
                    GetFloat(
                        source,
                        RatioProperties[i],
                        0.5f));
            }

            CopyTextureAndTransform(
                source,
                "_BlendNoise",
                destination,
                "_NoiseNear");
            CopyTextureAndTransform(
                source,
                "_BlendNoiseMid",
                destination,
                "_NoiseMiddle");
            CopyTextureAndTransform(
                source,
                "_BlendNoiseFar",
                destination,
                "_NoiseFar");

            destination.SetColor(
                "_BottomTint",
                GetColor(source, "_BottomTint", Color.white));
            destination.SetFloat(
                "_VariantEdgeSoftness",
                GetFloat(source, "_VariantEdgeSoftness", 0.2f));
            destination.SetFloat(
                "_FarVariantStrength",
                GetFloat(source, "_FarVariantStrength", 0.35f));
            destination.SetFloat(
                "_ControlSharpness",
                GetFloat(source, "_ControlSharpness", 1f));
            destination.SetFloat(
                "_NearInfluence",
                GetFloat(
                    source,
                    "_BlendNoiseNearInfluence",
                    0.25f));
            destination.SetFloat(
                "_MiddleInfluence",
                GetFloat(
                    source,
                    "_BlendNoiseMidInfluence",
                    0.5f));
            destination.SetFloat(
                "_UseVariants",
                UsesVariants(source) ? 1f : 0f);
        }

        private static Material SaveOrUpdateRuntimeMaterial(
            string materialPath,
            Shader runtimeShader,
            Material source,
            Texture2DArray terrainArray,
            Texture2DArray noiseArray,
            Texture2D farBasemap)
        {
            UnityEngine.Object existingAsset =
                AssetDatabase.LoadMainAssetAtPath(materialPath);
            if (existingAsset != null &&
                !(existingAsset is Material))
            {
                throw new InvalidOperationException(
                    $"输出路径已被其他类型资源占用：{materialPath}");
            }

            Material material =
                AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                material = new Material(runtimeShader)
                {
                    name = Path.GetFileNameWithoutExtension(materialPath)
                };
                AssetDatabase.CreateAsset(material, materialPath);
            }
            else
            {
                material.shader = runtimeShader;
            }

            material.SetTexture(
                "_ControlMap",
                source.GetTexture("_ControlMap"));
            CopyTextureAndTransform(
                source,
                "_BottomBase",
                material,
                "_BottomBase");
            material.SetTexture("_TerrainArray", terrainArray);
            material.SetTexture("_PackedNoise", noiseArray);
            material.SetTexture("_FarBasemap", farBasemap);

            for (int i = 0; i < TerrainTypeCount; i++)
            {
                material.SetVector(
                    BaseStProperties[i],
                    GetTextureST(source, BaseProperties[i]));
                material.SetVector(
                    VariantStProperties[i],
                    GetTextureST(source, VariantProperties[i]));
                material.SetColor(
                    TintProperties[i],
                    GetColor(
                        source,
                        TintProperties[i],
                        Color.white));
                material.SetFloat(
                    RatioProperties[i],
                    GetFloat(
                        source,
                        RatioProperties[i],
                        0.5f));
            }

            material.SetFloat(
                "_VariantEdgeSoftness",
                GetFloat(source, "_VariantEdgeSoftness", 0.2f));
            material.SetFloat(
                "_FarVariantStrength",
                GetFloat(source, "_FarVariantStrength", 0.35f));
            material.SetFloat(
                "_BottomDissolveSoftness",
                GetFloat(source, "_BottomDissolveSoftness", 0.15f));
            material.SetFloat(
                "_BottomUvDistortion",
                GetFloat(source, "_BottomUvDistortion", 0.3f));
            Vector2 noiseCorrelations =
                CalculatePackedNoiseCorrelations(source);
            material.SetFloat(
                "_NoiseCorrelation01",
                noiseCorrelations.x);
            material.SetFloat(
                "_NoiseCorrelation12",
                noiseCorrelations.y);
            material.SetFloat(
                "_ControlSharpness",
                GetFloat(source, "_ControlSharpness", 1f));
            material.SetColor(
                "_BottomTint",
                GetColor(source, "_BottomTint", Color.white));
            material.SetColor(
                "_GlobalTint",
                GetColor(source, "_GlobalTint", Color.white));
            material.SetFloat(
                "_Saturation",
                GetFloat(source, "_Saturation", 1f));
            material.SetFloat(
                "_LodNearToMiddleAt",
                GetFloat(
                    source,
                    "_VariantNearToMiddleAt",
                    30f));
            material.SetFloat(
                "_LodMiddleToFarAt",
                GetFloat(
                    source,
                    "_VariantMiddleToFarAt",
                    45f));
            material.SetFloat(
                "_LodFarCompleteAt",
                GetFloat(
                    source,
                    "_VariantDisappearAt",
                    60f));
            material.SetFloat(
                "_Lod3ToBottomRange",
                GetFloat(
                    source,
                    "_Lod3ToBottomRange",
                    12f));
            material.SetFloat(
                "_LodTransitionRatio",
                GetFloat(
                    source,
                    "_VariantLodTransitionRatio",
                    0.2f));

            bool useVariants = UsesVariants(source);
            material.SetFloat(
                "_UseVariants",
                useVariants ? 1f : 0f);
            if (useVariants)
            {
                material.EnableKeyword("_SLG_VARIANTS");
            }
            else
            {
                material.DisableKeyword("_SLG_VARIANTS");
            }

            bool useBottomLod =
                GetFloat(source, "_UseBottomLod", 0f) > 0.5f;
            material.SetFloat(
                "_UseBottomLod",
                useBottomLod ? 1f : 0f);
            if (useBottomLod)
            {
                material.EnableKeyword("_SLG_BOTTOM_LOD");
            }
            else
            {
                material.DisableKeyword("_SLG_BOTTOM_LOD");
            }

            material.enableInstancing = source.enableInstancing;
            material.renderQueue = source.renderQueue;
            EditorUtility.SetDirty(material);
            return material;
        }

        private static Texture2D SaveOrUpdateTexture(
            Texture2D generated,
            string assetPath)
        {
            UnityEngine.Object existingAsset =
                AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (existingAsset != null &&
                !(existingAsset is Texture2D))
            {
                throw new InvalidOperationException(
                    $"输出路径已被其他类型资源占用：{assetPath}");
            }

            Texture2D existing =
                AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            if (existing == null)
            {
                generated.name =
                    Path.GetFileNameWithoutExtension(assetPath);
                AssetDatabase.CreateAsset(generated, assetPath);
                generated.Apply(false, true);
                EditorUtility.SetDirty(generated);
                return generated;
            }

            string existingName = existing.name;
            EditorUtility.CopySerialized(generated, existing);
            existing.name = existingName;
            existing.Apply(false, true);
            EditorUtility.SetDirty(existing);
            DestroyGenerated(generated);
            return existing;
        }

        private static Texture2DArray SaveOrUpdateTextureArray(
            Texture2DArray generated,
            string assetPath,
            bool replaceLegacyTexture = false)
        {
            UnityEngine.Object existingAsset =
                AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (replaceLegacyTexture &&
                existingAsset is Texture2D)
            {
                if (!AssetDatabase.DeleteAsset(assetPath))
                {
                    throw new InvalidOperationException(
                        $"无法替换旧版Noise资源：{assetPath}");
                }

                existingAsset = null;
            }

            if (existingAsset != null &&
                !(existingAsset is Texture2DArray))
            {
                throw new InvalidOperationException(
                    $"输出路径已被其他类型资源占用：{assetPath}");
            }

            Texture2DArray existing =
                AssetDatabase.LoadAssetAtPath<Texture2DArray>(assetPath);
            if (existing == null)
            {
                generated.name =
                    Path.GetFileNameWithoutExtension(assetPath);
                AssetDatabase.CreateAsset(generated, assetPath);
                generated.Apply(false, true);
                EditorUtility.SetDirty(generated);
                return generated;
            }

            string existingName = existing.name;
            EditorUtility.CopySerialized(generated, existing);
            existing.name = existingName;
            existing.Apply(false, true);
            EditorUtility.SetDirty(existing);
            DestroyGenerated(generated);
            return existing;
        }

        private static void ValidateMaterialTextures(
            Material source,
            List<string> errors,
            List<string> warnings)
        {
            ValidateRequiredTexture(
                source,
                "_ControlMap",
                "控制图",
                errors);
            ValidateRequiredTexture(
                source,
                "_BottomBase",
                "Bottom底图",
                errors);

            for (int i = 0; i < TerrainTypeCount; i++)
            {
                ValidateRequiredTexture(
                    source,
                    BaseProperties[i],
                    $"{TypeNames[i]}主图",
                    errors);
            }

            if (UsesVariants(source))
            {
                for (int i = 0; i < TerrainTypeCount; i++)
                {
                    ValidateRequiredTexture(
                        source,
                        VariantProperties[i],
                        $"{TypeNames[i]}次图",
                        errors);
                }
                ValidateRequiredTexture(
                    source,
                    "_BlendNoise",
                    "近景Noise",
                    errors);
                ValidateRequiredTexture(
                    source,
                    "_BlendNoiseMid",
                    "中景Noise",
                    errors);
                ValidateRequiredTexture(
                    source,
                    "_BlendNoiseFar",
                    "远景Noise",
                    errors);
            }
            else
            {
                warnings.Add(
                    "制作材质未启用次图；数组后四层会复制对应主图。");
            }
        }

        private static Shader FindRuntimeShader()
        {
            Shader shader = Shader.Find(RuntimeShaderName);
            if (shader != null)
            {
                return shader;
            }

            string[] guids = AssetDatabase.FindAssets(
                $"{RuntimeShaderAssetSearchName} t:Shader");
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
                if (shader != null)
                {
                    return shader;
                }
            }

            return null;
        }

        private static void ValidateExistingOutputs(
            SLGTerrainBakeSettings settings,
            List<string> errors)
        {
            string[] paths = GetExpectedOutputPaths(settings);
            for (int i = 0; i < paths.Length; i++)
            {
                UnityEngine.Object existing =
                    AssetDatabase.LoadMainAssetAtPath(paths[i]);
                if (existing == null)
                {
                    continue;
                }

                bool valid =
                    (i == 0 && existing is Texture2DArray) ||
                    (i == 1 &&
                        (existing is Texture2DArray ||
                         existing is Texture2D)) ||
                    (i == 2 && existing is Texture2D) ||
                    (i == 3 && existing is Material);
                if (!valid)
                {
                    errors.Add(
                        $"输出路径已有其他类型资源，工具不会覆盖：{paths[i]}");
                }
            }
        }

        private static void ValidateRequiredTexture(
            Material material,
            string property,
            string displayName,
            List<string> errors)
        {
            if (!material.HasProperty(property) ||
                material.GetTexture(property) == null)
            {
                errors.Add($"{displayName}未设置。");
            }
        }

        private static void ValidateResolution(
            int value,
            int minimum,
            int maximum,
            string displayName,
            List<string> errors)
        {
            if (!Mathf.IsPowerOfTwo(value) ||
                value < minimum ||
                value > maximum)
            {
                errors.Add(
                    $"{displayName}必须是{minimum}～{maximum}之间的二次幂。");
            }
        }

        private static bool ReportProgress(
            Func<string, float, bool> callback,
            string message,
            int completed,
            int total)
        {
            return callback != null &&
                callback(
                    message,
                    Mathf.Clamp01((float)completed /
                        Mathf.Max(total, 1)));
        }

        private static void CopyTextureAndTransform(
            Material source,
            string sourceProperty,
            Material destination,
            string destinationProperty)
        {
            if (!source.HasProperty(sourceProperty))
            {
                return;
            }

            destination.SetTexture(
                destinationProperty,
                source.GetTexture(sourceProperty));
            destination.SetTextureScale(
                destinationProperty,
                source.GetTextureScale(sourceProperty));
            destination.SetTextureOffset(
                destinationProperty,
                source.GetTextureOffset(sourceProperty));
        }

        private static Vector4 GetTextureST(
            Material material,
            string property)
        {
            if (material == null || !material.HasProperty(property))
            {
                return new Vector4(1f, 1f, 0f, 0f);
            }

            Vector2 scale = material.GetTextureScale(property);
            Vector2 offset = material.GetTextureOffset(property);
            return new Vector4(
                scale.x,
                scale.y,
                offset.x,
                offset.y);
        }

        private static bool UsesVariants(Material source)
        {
            return source != null &&
                (source.IsKeywordEnabled("_SLG_VARIANTS") ||
                 GetFloat(source, "_UseVariants", 0f) > 0.5f);
        }

        private static Vector2 CalculatePackedNoiseCorrelations(
            Material source)
        {
            float nearInfluence = Mathf.Clamp01(
                GetFloat(
                    source,
                    "_BlendNoiseNearInfluence",
                    0.25f));
            float middleInfluence = Mathf.Clamp01(
                GetFloat(
                    source,
                    "_BlendNoiseMidInfluence",
                    0.5f));

            float middleVariance =
                1f + middleInfluence * middleInfluence;
            float nearVariance =
                middleVariance +
                nearInfluence * nearInfluence;
            float correlation01 = Mathf.Sqrt(
                middleVariance / nearVariance);
            float correlation12 = 1f / Mathf.Sqrt(
                middleVariance);
            return new Vector2(correlation01, correlation12);
        }

        private static float GetFloat(
            Material material,
            string property,
            float fallback)
        {
            return material != null && material.HasProperty(property)
                ? material.GetFloat(property)
                : fallback;
        }

        private static Color GetColor(
            Material material,
            string property,
            Color fallback)
        {
            return material != null && material.HasProperty(property)
                ? material.GetColor(property)
                : fallback;
        }

        private static double Square(int value)
        {
            return (double)value * value;
        }

        private static void EnsureFolder(string assetFolder)
        {
            string normalized = NormalizeAssetPath(assetFolder);
            if (AssetDatabase.IsValidFolder(normalized))
            {
                return;
            }

            string[] segments = normalized.Split('/');
            string current = segments[0];
            for (int i = 1; i < segments.Length; i++)
            {
                string next = $"{current}/{segments[i]}";
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, segments[i]);
                }
                current = next;
            }
        }

        private static bool IsProjectFolder(string path)
        {
            return path == "Assets" ||
                path.StartsWith(
                    "Assets/",
                    StringComparison.Ordinal);
        }

        private static string NormalizeAssetPath(string path)
        {
            return string.IsNullOrWhiteSpace(path)
                ? string.Empty
                : path.Trim().Replace('\\', '/').TrimEnd('/');
        }

        private static string SanitizeFileName(string value)
        {
            string result = string.IsNullOrWhiteSpace(value)
                ? "SLG_Terrain"
                : value.Trim();
            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                result = result.Replace(invalid, '_');
            }
            return result;
        }

        private static void DestroyGenerated(
            UnityEngine.Object value)
        {
            if (value != null &&
                !AssetDatabase.Contains(value))
            {
                UnityEngine.Object.DestroyImmediate(value);
            }
        }
    }
}
