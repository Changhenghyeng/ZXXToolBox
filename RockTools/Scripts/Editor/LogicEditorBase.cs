using System;
using UnityEditor;

namespace RockTools
{
    public class LogicEditorBase : Editor
    {
        public event Action OnPropertyChanged;

        protected void PropertyChanged()
        {
            OnPropertyChanged?.Invoke();
        }
    }
}