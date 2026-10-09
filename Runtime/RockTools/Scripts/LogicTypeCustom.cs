using System;

namespace RockTools
{
    [Serializable]
    public class LogicTypeCustom : LogicType01
    {
        protected override ERockType pRockType => ERockType.Custom;
    }

    [Serializable]
    public class LogicTypeCustomCrystal : LogicType03
    {
        protected override ERockType pRockType => ERockType.Custom;
    }
}
