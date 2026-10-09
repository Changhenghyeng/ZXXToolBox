using System;
using System.Diagnostics;

namespace RockTools
{
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
    [Conditional("UNITY_EDITOR")]
    public class NotifyChangeAttribute : Attribute
    {
        public string name { get; set; }

        public NotifyChangeAttribute(string name = "")
        {
            this.name = name;
        }
    }
}