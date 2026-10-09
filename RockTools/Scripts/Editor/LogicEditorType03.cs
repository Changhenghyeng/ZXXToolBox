using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;

namespace RockTools
{
    [CustomEditor(typeof(LogicType03), true)]
    public class LogicEditorType03 : LogicEditorBase
    {
        private List<FieldInfo> cachedFields = new List<FieldInfo>(20);
        private readonly List<object> cachedValues = new List<object>(20);

        private void OnEnable()
        {
            CacheFields();
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            DrawProperties();
            serializedObject.ApplyModifiedProperties();

            CheckTempValues();
            UpdateTempValues();
        }

        private void DrawProperties()
        {
            var iterator = serializedObject.GetIterator();
            var propertyToExclude = new[] {"m_Script"};
            var enterChildren = true;
            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;

                // exclusions
                if (propertyToExclude.Contains(iterator.name))
                {
                    continue;
                }

                EditorGUILayout.PropertyField(iterator, true);
            }
        }

        private void CacheFields()
        {
            cachedFields = GetFieldsWithAttribute<NotifyChangeAttribute>();

            if (cachedFields.Count == 0)
                return;

            foreach (var fieldInfo in cachedFields)
            {
                cachedValues.Add(fieldInfo.GetValue(target));
            }
        }

        private List<FieldInfo> GetFieldsWithAttribute<T>() where T : Attribute
        {
            var result = new List<FieldInfo>();
            var type = target.GetType();
            while (type != null && type != typeof(UnityEngine.ScriptableObject))
            {
                result.AddRange(type
                    .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(field => field.GetCustomAttributes().OfType<T>().Any()));
                type = type.BaseType;
            }
            return result;
        }

        private void UpdateTempValues()
        {
            if (cachedFields.Count == 0)
                return;

            for (var i = 0; i < cachedFields.Count; i++)
            {
                cachedValues[i] = cachedFields[i].GetValue(target);
            }
        }

        private void CheckTempValues()
        {
            if (cachedFields.Count == 0)
                return;

            for (var i = 0; i < cachedFields.Count; i++)
            {
                if (!Equals(cachedValues[i], cachedFields[i].GetValue(target)))
                {
                    PropertyChanged();
                }
            }
        }
    }
}
