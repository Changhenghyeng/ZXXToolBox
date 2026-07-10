using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace ArtCode
{
    public class DayChangeTransitionController : MonoBehaviour
    {
        [System.Serializable]
        public struct LightLook
        {
            public Vector3 EulerAngles;
            public Color Color;
            public float Intensity;
        }

        public enum MaterialPropertyValueType
        {
            Float,
            Color,
            Vector
        }

        [System.Serializable]
        public class MaterialPropertyTransition
        {
            public Renderer Renderer;
            public int MaterialIndex = -1;
            public string PropertyName = "_BaseColor";
            public MaterialPropertyValueType ValueType = MaterialPropertyValueType.Color;
            public bool UseCurrentValueAsDay = true;
            public bool UseDuskValue = true;
            public float DayFloat;
            public float DuskFloat = 0.5f;
            public float NightFloat;
            public Color DayColor = Color.white;
            public Color DuskColor = new Color(1f, 0.72f, 0.42f, 1f);
            public Color NightColor = new Color(0.62f, 0.72f, 1f, 1f);
        }

        [System.Serializable]
        public class MaterialAssetPropertyTransition
        {
            public string PropertyName = "_BaseColor";
            public MaterialPropertyValueType ValueType = MaterialPropertyValueType.Color;
            public bool UseCurrentValueAsDay = true;
            public bool UseDuskValue = true;
            public float DayFloat;
            public float DuskFloat = 0.5f;
            public float NightFloat;
            public Color DayColor = Color.white;
            public Color DuskColor = new Color(1f, 0.72f, 0.42f, 1f);
            public Color NightColor = new Color(0.62f, 0.72f, 1f, 1f);
            public Vector4 DayVector;
            public Vector4 DuskVector = Vector4.one;
            public Vector4 NightVector;
        }

        [System.Serializable]
        public class MaterialAssetTransition
        {
            public Material Material;
            public MaterialAssetPropertyTransition[] Properties;
        }

        private struct MaterialPropertySnapshot
        {
            public Renderer Renderer;
            public int MaterialIndex;
            public MaterialPropertyBlock Block;
        }

        private struct MaterialAssetPropertySnapshot
        {
            public Material Material;
            public string PropertyName;
            public MaterialPropertyValueType ValueType;
            public float FloatValue;
            public Color ColorValue;
            public Vector4 VectorValue;
        }

        private enum TransitionState
        {
            Day,
            FadeToNight,
            NightHold,
            FadeToDay
        }

        [Header("References")]
        public Light MainLight;
        public Component DuskVolume;
        public Component NightVolume;

        [Header("Post Process Transition")]
        public bool UseDuskVolume = true;

        [Header("Baked Shadow Swap")]
        public bool SwapBakedShadowsDuringTransition = true;
        public Renderer[] RealtimeShadowRenderers;
        public GameObject[] BakedShadowObjects;

        [Header("Material Property Transitions")]
        public MaterialPropertyTransition[] MaterialPropertyTransitions;
        public MaterialAssetTransition[] MaterialTransitions;

        [Header("Light Looks")]
        public LightLook DayLight = new LightLook
        {
            EulerAngles = new Vector3(50f, -30f, 0f),
            Color = Color.white,
            Intensity = 1f
        };

        public LightLook NightLight = new LightLook
        {
            EulerAngles = new Vector3(35f, 140f, 0f),
            Color = new Color(0.62f, 0.72f, 1f, 1f),
            Intensity = 0.45f
        };

        [Header("Light Rotation")]
        public bool UseFullCycleLightRotation = true;
        public Vector3 FullCycleEulerOffset = new Vector3(360f, 0f, 0f);
        public Vector3 FullCycleWorldAxis = Vector3.up;

        [Header("Timing")]
        [Min(0f)] public float FadeToNightSeconds = 8f;
        public float NightHoldSeconds = 3f;
        [Min(0f)] public float FadeToDaySeconds = 8f;
        public AnimationCurve TransitionCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        [Header("Events")]
        public UnityEvent OnNightReached;
        public UnityEvent OnNewDay;
        public UnityEvent OnTransitionFinished;

        [SerializeField, Range(0f, 1f)] private float _nightBlend;
        [SerializeField] private TransitionState _state = TransitionState.Day;

        private float _fadeTimer;
        private float _fadeDuration;
        private float _fadeStartBlend;
        private float _fadeTargetBlend;
        private float _cycleProgress;
        private float _cycleStartProgress;
        private float _cycleTargetProgress;
        private float _holdTimer;
        private object[] _rendererShadowModes;
        private bool[] _rendererReceiveShadows;
        private bool[] _bakedShadowActiveStates;
        private bool _shadowSwapActive;
        private bool _lightSnapshotValid;
        private Quaternion _originalLightRotation;
        private Color _originalLightColor;
        private float _originalLightIntensity;
        private List<MaterialPropertySnapshot> _materialSnapshots;
        private List<MaterialAssetPropertySnapshot> _materialAssetSnapshots;
        private bool _materialSnapshotValid;

        public float NightBlend => _nightBlend;

        private void Reset()
        {
            MainLight = FindMainLight();
        }

        private void OnDisable()
        {
            SetPostProcessWeights(0f, 0f);
            RestoreMaterialPropertySnapshots();
            SetShadowSwapActive(false);
            RestoreLightSnapshot();
        }

        private void OnValidate()
        {
            FadeToNightSeconds = Mathf.Max(0f, FadeToNightSeconds);
            FadeToDaySeconds = Mathf.Max(0f, FadeToDaySeconds);

            if (!Application.isPlaying && MainLight != null)
            {
                SetBlend(_nightBlend);
            }
        }

        private void Update()
        {
            if (_state == TransitionState.FadeToNight || _state == TransitionState.FadeToDay)
            {
                TickFade();
                return;
            }

            if (_state == TransitionState.NightHold)
            {
                TickNightHold();
            }
        }

        public void TriggerDayChange()
        {
            CaptureLightSnapshot();
            if (_nightBlend <= 0.001f)
            {
                _cycleProgress = 0f;
            }

            SetShadowSwapActive(true);
            BeginFade(1f, FadeToNightSeconds, TransitionState.FadeToNight);
        }

        public void ResumeToDay()
        {
            CaptureLightSnapshot();
            SetShadowSwapActive(true);
            BeginFade(0f, FadeToDaySeconds, TransitionState.FadeToDay);
        }

        public void PreviewDay()
        {
            _state = TransitionState.Day;
            SetPostProcessWeights(0f, 0f);
            _nightBlend = 0f;
            _cycleProgress = 0f;
            RestoreMaterialPropertySnapshots();
            SetShadowSwapActive(false);
            RestoreLightSnapshot();
        }

        public void PreviewNight()
        {
            CaptureLightSnapshot();
            _state = TransitionState.NightHold;
            SetShadowSwapActive(true);
            ApplyBlend(1f, 0.5f);
        }

        public void SetBlend(float blend)
        {
            _state = blend >= 1f ? TransitionState.NightHold : TransitionState.Day;
            if (blend > 0.001f)
            {
                CaptureLightSnapshot();
                CaptureMaterialPropertySnapshots();
            }

            SetShadowSwapActive(blend > 0.001f);
            ApplyBlend(blend, blend >= 1f ? 0.5f : 0f);

            if (blend <= 0.001f)
            {
                RestoreMaterialPropertySnapshots();
                RestoreLightSnapshot();
            }
        }

        public void SetTransitionProgress(float progress)
        {
            CaptureLightSnapshot();
            CaptureMaterialPropertySnapshots();
            float clampedProgress = Mathf.Clamp01(progress);
            float blend = clampedProgress <= 0.5f
                ? clampedProgress * 2f
                : (1f - clampedProgress) * 2f;

            _state = blend >= 1f ? TransitionState.NightHold : TransitionState.Day;
            SetShadowSwapActive(blend > 0.001f);
            ApplyBlend(blend, clampedProgress);

            if (clampedProgress <= 0.001f || clampedProgress >= 0.999f)
            {
                RestoreMaterialPropertySnapshots();
                RestoreLightSnapshot();
            }
        }

        private void BeginFade(float targetBlend, float duration, TransitionState state)
        {
            _state = state;
            _fadeTimer = 0f;
            _fadeDuration = Mathf.Max(0.0001f, duration);
            _fadeStartBlend = _nightBlend;
            _fadeTargetBlend = Mathf.Clamp01(targetBlend);
            _cycleStartProgress = _cycleProgress;
            _cycleTargetProgress = _fadeTargetBlend >= 1f ? 0.5f : 1f;

            if (duration <= 0f)
            {
                ApplyBlend(_fadeTargetBlend, _cycleTargetProgress);
                FinishFade();
            }
        }

        private void TickFade()
        {
            _fadeTimer += Time.deltaTime;
            float t = Mathf.Clamp01(_fadeTimer / _fadeDuration);
            float curvedT = TransitionCurve != null ? TransitionCurve.Evaluate(t) : t;
            ApplyBlend(
                Mathf.Lerp(_fadeStartBlend, _fadeTargetBlend, curvedT),
                Mathf.Lerp(_cycleStartProgress, _cycleTargetProgress, curvedT));

            if (t >= 1f)
            {
                FinishFade();
            }
        }

        private void FinishFade()
        {
            if (_fadeTargetBlend >= 1f)
            {
                _state = TransitionState.NightHold;
                _holdTimer = 0f;
                OnNightReached?.Invoke();
                OnNewDay?.Invoke();

                if (Mathf.Approximately(NightHoldSeconds, 0f))
                {
                    ResumeToDay();
                }
            }
            else
            {
                _state = TransitionState.Day;
                ApplyBlend(0f, 1f);
                RestoreMaterialPropertySnapshots();
                SetShadowSwapActive(false);
                RestoreLightSnapshot();
                OnTransitionFinished?.Invoke();
            }
        }

        private void TickNightHold()
        {
            if (NightHoldSeconds < 0f)
            {
                return;
            }

            _holdTimer += Time.deltaTime;
            if (_holdTimer >= NightHoldSeconds)
            {
                ResumeToDay();
            }
        }

        private void ApplyBlend(float blend)
        {
            ApplyBlend(blend, _cycleProgress);
        }

        private void ApplyBlend(float blend, float cycleProgress)
        {
            _nightBlend = Mathf.Clamp01(blend);
            _cycleProgress = Mathf.Clamp01(cycleProgress);

            SetPostProcessWeights(_nightBlend, _cycleProgress);
            ApplyMaterialPropertyTransitions(_cycleProgress);

            if (MainLight != null)
            {
                Quaternion dayRotation = GetDayRotation();
                Quaternion cycleRotation = GetFullCycleWorldRotation(dayRotation, _cycleProgress);
                Quaternion nightRotation = GetFullCycleWorldRotation(dayRotation, 0.5f);
                MainLight.transform.rotation = UseFullCycleLightRotation
                    ? cycleRotation
                    : Quaternion.Slerp(dayRotation, nightRotation, _nightBlend);
                MainLight.color = Color.Lerp(GetDayColor(), NightLight.Color, _nightBlend);
                MainLight.intensity = Mathf.Lerp(GetDayIntensity(), NightLight.Intensity, _nightBlend);
            }
        }

        private void SetPostProcessWeights(float nightBlend, float cycleProgress)
        {
            _nightBlend = Mathf.Clamp01(nightBlend);
            _cycleProgress = Mathf.Clamp01(cycleProgress);

            if (NightVolume != null)
            {
                SetVolumeWeight(NightVolume, GetNightVolumeWeight());
            }

            if (DuskVolume != null)
            {
                SetVolumeWeight(DuskVolume, GetDuskVolumeWeight());
            }
        }

        private void CaptureLightSnapshot()
        {
            if (_lightSnapshotValid || MainLight == null)
            {
                return;
            }

            _originalLightRotation = MainLight.transform.rotation;
            _originalLightColor = MainLight.color;
            _originalLightIntensity = MainLight.intensity;
            _lightSnapshotValid = true;
        }

        private void CaptureMaterialPropertySnapshots()
        {
            if (_materialSnapshotValid)
            {
                return;
            }

            _materialSnapshots = new List<MaterialPropertySnapshot>();
            if (MaterialPropertyTransitions == null)
            {
                _materialSnapshotValid = true;
            }
            else
            {
                for (int i = 0; i < MaterialPropertyTransitions.Length; i++)
                {
                    MaterialPropertyTransition transition = MaterialPropertyTransitions[i];
                    if (transition == null || transition.Renderer == null)
                    {
                        continue;
                    }

                    int materialCount = transition.Renderer.sharedMaterials != null ? transition.Renderer.sharedMaterials.Length : 0;
                    if (materialCount <= 0)
                    {
                        continue;
                    }

                    if (transition.MaterialIndex >= 0)
                    {
                        CaptureMaterialPropertySnapshot(transition.Renderer, transition.MaterialIndex);
                    }
                    else
                    {
                        for (int materialIndex = 0; materialIndex < materialCount; materialIndex++)
                        {
                            CaptureMaterialPropertySnapshot(transition.Renderer, materialIndex);
                        }
                    }
                }
            }

            CaptureMaterialAssetSnapshots();
            _materialSnapshotValid = true;
        }

        private void CaptureMaterialPropertySnapshot(Renderer renderer, int materialIndex)
        {
            if (renderer == null || materialIndex < 0)
            {
                return;
            }

            MaterialPropertyBlock block = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block, materialIndex);
            _materialSnapshots.Add(new MaterialPropertySnapshot
            {
                Renderer = renderer,
                MaterialIndex = materialIndex,
                Block = block
            });
        }

        private void RestoreMaterialPropertySnapshots()
        {
            if (!_materialSnapshotValid)
            {
                return;
            }

            if (_materialSnapshots != null)
            {
                for (int i = 0; i < _materialSnapshots.Count; i++)
                {
                    MaterialPropertySnapshot snapshot = _materialSnapshots[i];
                    if (snapshot.Renderer != null)
                    {
                        snapshot.Renderer.SetPropertyBlock(snapshot.Block, snapshot.MaterialIndex);
                    }
                }
            }

            _materialSnapshots = null;
            RestoreMaterialAssetSnapshots();
            _materialSnapshotValid = false;
        }

        private void CaptureMaterialAssetSnapshots()
        {
            _materialAssetSnapshots = new List<MaterialAssetPropertySnapshot>();
            if (MaterialTransitions == null)
            {
                return;
            }

            for (int i = 0; i < MaterialTransitions.Length; i++)
            {
                MaterialAssetTransition transition = MaterialTransitions[i];
                if (transition == null || transition.Material == null || transition.Properties == null)
                {
                    continue;
                }

                for (int j = 0; j < transition.Properties.Length; j++)
                {
                    MaterialAssetPropertyTransition property = transition.Properties[j];
                    if (property == null || string.IsNullOrEmpty(property.PropertyName) || !transition.Material.HasProperty(property.PropertyName))
                    {
                        continue;
                    }

                    MaterialAssetPropertySnapshot snapshot = new MaterialAssetPropertySnapshot
                    {
                        Material = transition.Material,
                        PropertyName = property.PropertyName,
                        ValueType = property.ValueType
                    };

                    if (property.ValueType == MaterialPropertyValueType.Float)
                    {
                        snapshot.FloatValue = transition.Material.GetFloat(property.PropertyName);
                    }
                    else if (property.ValueType == MaterialPropertyValueType.Color)
                    {
                        snapshot.ColorValue = transition.Material.GetColor(property.PropertyName);
                    }
                    else
                    {
                        snapshot.VectorValue = transition.Material.GetVector(property.PropertyName);
                    }

                    _materialAssetSnapshots.Add(snapshot);
                }
            }
        }

        private void RestoreMaterialAssetSnapshots()
        {
            if (_materialAssetSnapshots == null)
            {
                return;
            }

            for (int i = 0; i < _materialAssetSnapshots.Count; i++)
            {
                MaterialAssetPropertySnapshot snapshot = _materialAssetSnapshots[i];
                if (snapshot.Material == null || string.IsNullOrEmpty(snapshot.PropertyName) || !snapshot.Material.HasProperty(snapshot.PropertyName))
                {
                    continue;
                }

                if (snapshot.ValueType == MaterialPropertyValueType.Float)
                {
                    snapshot.Material.SetFloat(snapshot.PropertyName, snapshot.FloatValue);
                }
                else if (snapshot.ValueType == MaterialPropertyValueType.Color)
                {
                    snapshot.Material.SetColor(snapshot.PropertyName, snapshot.ColorValue);
                }
                else
                {
                    snapshot.Material.SetVector(snapshot.PropertyName, snapshot.VectorValue);
                }
            }

            _materialAssetSnapshots = null;
        }

        private void ApplyMaterialPropertyTransitions(float cycleProgress)
        {
            if (MaterialPropertyTransitions == null || MaterialPropertyTransitions.Length == 0)
            {
                ApplyMaterialAssetTransitions(Mathf.Clamp01(cycleProgress));
                return;
            }

            float progress = Mathf.Clamp01(cycleProgress);
            for (int i = 0; i < MaterialPropertyTransitions.Length; i++)
            {
                MaterialPropertyTransition transition = MaterialPropertyTransitions[i];
                if (transition == null || transition.Renderer == null || string.IsNullOrEmpty(transition.PropertyName))
                {
                    continue;
                }

                int materialCount = transition.Renderer.sharedMaterials != null ? transition.Renderer.sharedMaterials.Length : 0;
                if (materialCount <= 0)
                {
                    continue;
                }

                if (transition.MaterialIndex >= 0)
                {
                    ApplyMaterialPropertyTransition(transition, transition.MaterialIndex, progress);
                }
                else
                {
                    for (int materialIndex = 0; materialIndex < materialCount; materialIndex++)
                    {
                        ApplyMaterialPropertyTransition(transition, materialIndex, progress);
                    }
                }
            }

            ApplyMaterialAssetTransitions(progress);
        }

        private void ApplyMaterialAssetTransitions(float progress)
        {
            if (MaterialTransitions == null)
            {
                return;
            }

            for (int i = 0; i < MaterialTransitions.Length; i++)
            {
                MaterialAssetTransition transition = MaterialTransitions[i];
                if (transition == null || transition.Material == null || transition.Properties == null)
                {
                    continue;
                }

                for (int j = 0; j < transition.Properties.Length; j++)
                {
                    MaterialAssetPropertyTransition property = transition.Properties[j];
                    if (property == null || string.IsNullOrEmpty(property.PropertyName) || !transition.Material.HasProperty(property.PropertyName))
                    {
                        continue;
                    }

                    if (property.ValueType == MaterialPropertyValueType.Float)
                    {
                        float dayValue = property.UseCurrentValueAsDay ? GetOriginalMaterialFloat(transition.Material, property.PropertyName) : property.DayFloat;
                        transition.Material.SetFloat(property.PropertyName, EvaluateMaterialAssetFloat(property, dayValue, progress));
                    }
                    else if (property.ValueType == MaterialPropertyValueType.Color)
                    {
                        Color dayValue = property.UseCurrentValueAsDay ? GetOriginalMaterialColor(transition.Material, property.PropertyName) : property.DayColor;
                        transition.Material.SetColor(property.PropertyName, EvaluateMaterialAssetColor(property, dayValue, progress));
                    }
                    else
                    {
                        Vector4 dayValue = property.UseCurrentValueAsDay ? GetOriginalMaterialVector(transition.Material, property.PropertyName) : property.DayVector;
                        transition.Material.SetVector(property.PropertyName, EvaluateMaterialAssetVector(property, dayValue, progress));
                    }
                }
            }
        }

        private float GetOriginalMaterialFloat(Material material, string propertyName)
        {
            if (_materialAssetSnapshots != null)
            {
                for (int i = 0; i < _materialAssetSnapshots.Count; i++)
                {
                    MaterialAssetPropertySnapshot snapshot = _materialAssetSnapshots[i];
                    if (snapshot.Material == material && snapshot.PropertyName == propertyName)
                    {
                        return snapshot.FloatValue;
                    }
                }
            }

            return material.GetFloat(propertyName);
        }

        private Color GetOriginalMaterialColor(Material material, string propertyName)
        {
            if (_materialAssetSnapshots != null)
            {
                for (int i = 0; i < _materialAssetSnapshots.Count; i++)
                {
                    MaterialAssetPropertySnapshot snapshot = _materialAssetSnapshots[i];
                    if (snapshot.Material == material && snapshot.PropertyName == propertyName)
                    {
                        return snapshot.ColorValue;
                    }
                }
            }

            return material.GetColor(propertyName);
        }

        private Vector4 GetOriginalMaterialVector(Material material, string propertyName)
        {
            if (_materialAssetSnapshots != null)
            {
                for (int i = 0; i < _materialAssetSnapshots.Count; i++)
                {
                    MaterialAssetPropertySnapshot snapshot = _materialAssetSnapshots[i];
                    if (snapshot.Material == material && snapshot.PropertyName == propertyName)
                    {
                        return snapshot.VectorValue;
                    }
                }
            }

            return material.GetVector(propertyName);
        }

        private void ApplyMaterialPropertyTransition(MaterialPropertyTransition transition, int materialIndex, float progress)
        {
            if (materialIndex < 0 || transition.Renderer == null)
            {
                return;
            }

            Material[] materials = transition.Renderer.sharedMaterials;
            if (materials == null || materialIndex >= materials.Length)
            {
                return;
            }

            Material material = materials[materialIndex];
            if (material == null || !material.HasProperty(transition.PropertyName))
            {
                return;
            }

            MaterialPropertyBlock block = new MaterialPropertyBlock();
            transition.Renderer.GetPropertyBlock(block, materialIndex);

            if (transition.ValueType == MaterialPropertyValueType.Float)
            {
                float dayValue = transition.UseCurrentValueAsDay ? material.GetFloat(transition.PropertyName) : transition.DayFloat;
                block.SetFloat(transition.PropertyName, EvaluateMaterialFloat(transition, dayValue, progress));
            }
            else
            {
                Color dayValue = transition.UseCurrentValueAsDay ? material.GetColor(transition.PropertyName) : transition.DayColor;
                block.SetColor(transition.PropertyName, EvaluateMaterialColor(transition, dayValue, progress));
            }

            transition.Renderer.SetPropertyBlock(block, materialIndex);
        }

        private float EvaluateMaterialFloat(MaterialPropertyTransition transition, float dayValue, float progress)
        {
            if (UseDuskVolume && transition.UseDuskValue)
            {
                if (progress < 0.25f)
                {
                    return Mathf.Lerp(dayValue, transition.DuskFloat, SmoothStep(0f, 0.25f, progress));
                }

                if (progress < 0.5f)
                {
                    return Mathf.Lerp(transition.DuskFloat, transition.NightFloat, SmoothStep(0.25f, 0.5f, progress));
                }
            }

            float nightWeight = progress <= 0.5f ? progress * 2f : (1f - progress) * 2f;
            return Mathf.Lerp(dayValue, transition.NightFloat, Mathf.Clamp01(nightWeight));
        }

        private Color EvaluateMaterialColor(MaterialPropertyTransition transition, Color dayValue, float progress)
        {
            if (UseDuskVolume && transition.UseDuskValue)
            {
                if (progress < 0.25f)
                {
                    return Color.Lerp(dayValue, transition.DuskColor, SmoothStep(0f, 0.25f, progress));
                }

                if (progress < 0.5f)
                {
                    return Color.Lerp(transition.DuskColor, transition.NightColor, SmoothStep(0.25f, 0.5f, progress));
                }
            }

            float nightWeight = progress <= 0.5f ? progress * 2f : (1f - progress) * 2f;
            return Color.Lerp(dayValue, transition.NightColor, Mathf.Clamp01(nightWeight));
        }

        private float EvaluateMaterialAssetFloat(MaterialAssetPropertyTransition transition, float dayValue, float progress)
        {
            if (UseDuskVolume && transition.UseDuskValue)
            {
                if (progress < 0.25f)
                {
                    return Mathf.Lerp(dayValue, transition.DuskFloat, SmoothStep(0f, 0.25f, progress));
                }

                if (progress < 0.5f)
                {
                    return Mathf.Lerp(transition.DuskFloat, transition.NightFloat, SmoothStep(0.25f, 0.5f, progress));
                }
            }

            float nightWeight = progress <= 0.5f ? progress * 2f : (1f - progress) * 2f;
            return Mathf.Lerp(dayValue, transition.NightFloat, Mathf.Clamp01(nightWeight));
        }

        private Color EvaluateMaterialAssetColor(MaterialAssetPropertyTransition transition, Color dayValue, float progress)
        {
            if (UseDuskVolume && transition.UseDuskValue)
            {
                if (progress < 0.25f)
                {
                    return Color.Lerp(dayValue, transition.DuskColor, SmoothStep(0f, 0.25f, progress));
                }

                if (progress < 0.5f)
                {
                    return Color.Lerp(transition.DuskColor, transition.NightColor, SmoothStep(0.25f, 0.5f, progress));
                }
            }

            float nightWeight = progress <= 0.5f ? progress * 2f : (1f - progress) * 2f;
            return Color.Lerp(dayValue, transition.NightColor, Mathf.Clamp01(nightWeight));
        }

        private Vector4 EvaluateMaterialAssetVector(MaterialAssetPropertyTransition transition, Vector4 dayValue, float progress)
        {
            if (UseDuskVolume && transition.UseDuskValue)
            {
                if (progress < 0.25f)
                {
                    return Vector4.Lerp(dayValue, transition.DuskVector, SmoothStep(0f, 0.25f, progress));
                }

                if (progress < 0.5f)
                {
                    return Vector4.Lerp(transition.DuskVector, transition.NightVector, SmoothStep(0.25f, 0.5f, progress));
                }
            }

            float nightWeight = progress <= 0.5f ? progress * 2f : (1f - progress) * 2f;
            return Vector4.Lerp(dayValue, transition.NightVector, Mathf.Clamp01(nightWeight));
        }

        private void RestoreLightSnapshot()
        {
            if (!_lightSnapshotValid || MainLight == null)
            {
                _lightSnapshotValid = false;
                return;
            }

            MainLight.transform.rotation = _originalLightRotation;
            MainLight.color = _originalLightColor;
            MainLight.intensity = _originalLightIntensity;
            _lightSnapshotValid = false;
        }

        private Quaternion GetDayRotation()
        {
            if (_lightSnapshotValid)
            {
                return _originalLightRotation;
            }

            return MainLight != null ? MainLight.transform.rotation : Quaternion.Euler(DayLight.EulerAngles);
        }

        private Color GetDayColor()
        {
            return _lightSnapshotValid ? _originalLightColor : DayLight.Color;
        }

        private float GetDayIntensity()
        {
            return _lightSnapshotValid ? _originalLightIntensity : DayLight.Intensity;
        }

        private Quaternion GetFullCycleWorldRotation(Quaternion dayRotation, float progress)
        {
            Vector3 axis = FullCycleWorldAxis.sqrMagnitude > 0.0001f ? FullCycleWorldAxis.normalized : Vector3.up;
            float angle = FullCycleEulerOffset.magnitude * Mathf.Clamp01(progress);
            return Quaternion.AngleAxis(angle, axis) * dayRotation;
        }

        private float GetNightVolumeWeight()
        {
            if (!UseDuskVolume || DuskVolume == null)
            {
                return _nightBlend;
            }

            float progress = Mathf.Clamp01(_cycleProgress);
            if (progress < 0.25f)
            {
                return 0f;
            }

            if (progress < 0.5f)
            {
                return SmoothStep(0.25f, 0.5f, progress);
            }

            return 1f - SmoothStep(0.5f, 1f, progress);
        }

        private float GetDuskVolumeWeight()
        {
            if (!UseDuskVolume)
            {
                return 0f;
            }

            float progress = Mathf.Clamp01(_cycleProgress);
            if (progress < 0.25f)
            {
                return SmoothStep(0f, 0.25f, progress);
            }

            if (progress < 0.5f)
            {
                return 1f - SmoothStep(0.25f, 0.5f, progress);
            }

            return 0f;
        }

        private static float SmoothStep(float from, float to, float value)
        {
            float t = Mathf.Clamp01((value - from) / Mathf.Max(0.0001f, to - from));
            return t * t * (3f - 2f * t);
        }

        private static Light FindMainLight()
        {
            Light best = null;
            float bestIntensity = float.MinValue;
            foreach (Light light in FindObjectsOfType<Light>())
            {
                if (light == null || light.type != LightType.Directional || !light.enabled)
                {
                    continue;
                }

                if (light.intensity > bestIntensity)
                {
                    best = light;
                    bestIntensity = light.intensity;
                }
            }

            return best;
        }

        private static void SetVolumeWeight(Component volume, float weight)
        {
            System.Type type = volume.GetType();
            System.Reflection.PropertyInfo property = type.GetProperty("weight");
            if (property != null && property.CanWrite)
            {
                property.SetValue(volume, weight, null);
                return;
            }

            System.Reflection.FieldInfo field = type.GetField("weight");
            if (field != null)
            {
                field.SetValue(volume, weight);
            }
        }

        private void SetShadowSwapActive(bool active)
        {
            if (!SwapBakedShadowsDuringTransition)
            {
                return;
            }

            if (active == _shadowSwapActive)
            {
                return;
            }

            if (active)
            {
                CaptureShadowState();
                SetBakedShadowObjectsActive(false);
                SetRealtimeRendererShadows(true);
                _shadowSwapActive = true;
            }
            else
            {
                RestoreShadowState();
                _shadowSwapActive = false;
            }
        }

        private void CaptureShadowState()
        {
            if (RealtimeShadowRenderers != null)
            {
                _rendererShadowModes = new object[RealtimeShadowRenderers.Length];
                _rendererReceiveShadows = new bool[RealtimeShadowRenderers.Length];
                for (int i = 0; i < RealtimeShadowRenderers.Length; i++)
                {
                    Renderer renderer = RealtimeShadowRenderers[i];
                    if (renderer == null)
                    {
                        continue;
                    }

                    _rendererShadowModes[i] = GetRendererShadowMode(renderer);
                    _rendererReceiveShadows[i] = renderer.receiveShadows;
                }
            }

            if (BakedShadowObjects != null)
            {
                _bakedShadowActiveStates = new bool[BakedShadowObjects.Length];
                for (int i = 0; i < BakedShadowObjects.Length; i++)
                {
                    GameObject go = BakedShadowObjects[i];
                    _bakedShadowActiveStates[i] = go != null && go.activeSelf;
                }
            }
        }

        private void RestoreShadowState()
        {
            if (RealtimeShadowRenderers != null && _rendererShadowModes != null)
            {
                int count = Mathf.Min(RealtimeShadowRenderers.Length, _rendererShadowModes.Length);
                for (int i = 0; i < count; i++)
                {
                    Renderer renderer = RealtimeShadowRenderers[i];
                    if (renderer == null)
                    {
                        continue;
                    }

                    SetRendererShadowMode(renderer, _rendererShadowModes[i]);
                    if (_rendererReceiveShadows != null && i < _rendererReceiveShadows.Length)
                    {
                        renderer.receiveShadows = _rendererReceiveShadows[i];
                    }
                }
            }

            if (BakedShadowObjects != null && _bakedShadowActiveStates != null)
            {
                int count = Mathf.Min(BakedShadowObjects.Length, _bakedShadowActiveStates.Length);
                for (int i = 0; i < count; i++)
                {
                    if (BakedShadowObjects[i] != null)
                    {
                        BakedShadowObjects[i].SetActive(_bakedShadowActiveStates[i]);
                    }
                }
            }
        }

        private void SetBakedShadowObjectsActive(bool active)
        {
            if (BakedShadowObjects == null)
            {
                return;
            }

            foreach (GameObject go in BakedShadowObjects)
            {
                if (go != null)
                {
                    go.SetActive(active);
                }
            }
        }

        private void SetRealtimeRendererShadows(bool enabled)
        {
            if (RealtimeShadowRenderers == null)
            {
                return;
            }

            foreach (Renderer renderer in RealtimeShadowRenderers)
            {
                if (renderer == null)
                {
                    continue;
                }

                SetRendererShadowMode(renderer, enabled ? "On" : "Off");
                renderer.receiveShadows = true;
            }
        }

        private static object GetRendererShadowMode(Renderer renderer)
        {
            System.Reflection.PropertyInfo property = typeof(Renderer).GetProperty("shadowCastingMode");
            return property != null ? property.GetValue(renderer, null) : null;
        }

        private static void SetRendererShadowMode(Renderer renderer, object value)
        {
            System.Reflection.PropertyInfo property = typeof(Renderer).GetProperty("shadowCastingMode");
            if (property == null || !property.CanWrite)
            {
                return;
            }

            object finalValue = value;
            if (value is string modeName)
            {
                finalValue = System.Enum.Parse(property.PropertyType, modeName);
            }

            property.SetValue(renderer, finalValue, null);
        }
    }
}
