using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace RockTools
{
    [CustomEditor(typeof(LogicType01), true)]
    public class LogicEditorType01 : LogicEditorBase
    {
        private const float KChangeSensitivity = 0.0001f;

        private LogicType01 logic;

        private SerializedProperty density;
        private SerializedProperty radius;
        private SerializedProperty asymmetry;
        private SerializedProperty wave;
        private SerializedProperty decentralize;
        private SerializedProperty scaleLocal;
        private SerializedProperty scaleRandom;
        private SerializedProperty scaleByDistance;
        private SerializedProperty tallness;
        private SerializedProperty flatness;
        private SerializedProperty wideness;
        private SerializedProperty rotation;
        private SerializedProperty rotationLocal;
        private SerializedProperty rotationRnd;

        private int tmpDensity;
        private float tmpRadius;
        private float tmpAsymmetry;
        private float tmpWave;
        private float tmpDecentralize;
        private float tmpScaleLocal;
        private AnimationCurve tmpScaleByDistance;
        private float tmpTallness;
        private float tmpFlatness;
        private float tmpWideness;
        private float tmpRotation;
        private float tmpRotationLocal;
        private float tmpRotationZRnd;

        private void OnEnable()
        {
            InitializeProperties();
            UpdateTmpValues();
        }

        private void InitializeProperties()
        {
            density = serializedObject.FindProperty("density");
            radius = serializedObject.FindProperty("radius");
            asymmetry = serializedObject.FindProperty("asymmetry");
            wave = serializedObject.FindProperty("wave");
            decentralize = serializedObject.FindProperty("decentralize");
            scaleLocal = serializedObject.FindProperty("scaleLocal");
            scaleByDistance = serializedObject.FindProperty("scaleByDistance");
            tallness = serializedObject.FindProperty("tallness");
            flatness = serializedObject.FindProperty("flatness");
            wideness = serializedObject.FindProperty("wideness");
            rotation = serializedObject.FindProperty("rotation");
            rotationLocal = serializedObject.FindProperty("rotationLocal");
            rotationRnd = serializedObject.FindProperty("rotationRnd");
        }

        private void UpdateTmpValues()
        {
            tmpDensity = density.intValue;
            tmpRadius = radius.floatValue;
            tmpAsymmetry = asymmetry.floatValue;
            tmpWave = wave.floatValue;
            tmpDecentralize = decentralize.floatValue;
            tmpScaleLocal = scaleLocal.floatValue;
            tmpScaleByDistance = scaleByDistance.animationCurveValue;
            tmpTallness = tallness.floatValue;
            tmpFlatness = flatness.floatValue;
            tmpWideness = wideness.floatValue;
            tmpRotation = rotation.floatValue;
            tmpRotationLocal = rotationLocal.floatValue;
            tmpRotationZRnd = rotationRnd.floatValue;
        }

        public override void OnInspectorGUI()
        {

            serializedObject.Update();
            DrawProperties();
            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space();

            if (tmpDensity != density.intValue)
            {
                PropertyChanged();
            }
            else if (Math.Abs(tmpRadius - radius.floatValue) > KChangeSensitivity)
            {
                PropertyChanged();
            }
            else if (Math.Abs(tmpAsymmetry - asymmetry.floatValue) > KChangeSensitivity)
            {
                PropertyChanged();
            }
            else if (Math.Abs(tmpWave - wave.floatValue) > KChangeSensitivity)
            {
                PropertyChanged();
            }
            else if (Math.Abs(tmpDecentralize - decentralize.floatValue) > KChangeSensitivity)
            {
                PropertyChanged();
            }
            else if (Math.Abs(tmpScaleLocal - scaleLocal.floatValue) > KChangeSensitivity)
            {
                PropertyChanged();
            }
            else if (!tmpScaleByDistance.Equals(scaleByDistance.animationCurveValue))
            {
                PropertyChanged();
            }
            else if (Math.Abs(tmpTallness - tallness.floatValue) > KChangeSensitivity)
            {
                PropertyChanged();
            }
            else if (Math.Abs(tmpFlatness - flatness.floatValue) > KChangeSensitivity)
            {
                PropertyChanged();
            }
            else if (Math.Abs(tmpWideness - wideness.floatValue) > KChangeSensitivity)
            {
                PropertyChanged();
            }

            else if (Math.Abs(tmpRotation - rotation.floatValue) > KChangeSensitivity)
            {
                PropertyChanged();
            }
            else if (Math.Abs(tmpRotationLocal - rotationLocal.floatValue) > KChangeSensitivity)
            {
                PropertyChanged();
            }
            else if (Math.Abs(tmpRotationZRnd - rotationRnd.floatValue) > KChangeSensitivity)
            {
                PropertyChanged();
            }

            UpdateTmpValues();
        }

        private void DrawProperties()
        {
            var iterator = serializedObject.GetIterator();
            var propertyToExclude = new[] {"m_Script"};
            var enterChildren = true;
            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (!propertyToExclude.Contains(iterator.name))
                {
                    EditorGUILayout.PropertyField(iterator, true);
                }
            }
        }

        private void DuringSceneGui(SceneView obj)
        {
            // if (rockGen != null)
            // {
            //     Handles.DrawWireDisc(rockGen.transform.position, Vector3.up, tmpRadius);
            // }
        }
    }
}