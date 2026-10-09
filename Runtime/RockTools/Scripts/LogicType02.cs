using System;

namespace RockTools
{
    [Serializable]
    public class LogicType02 : LogicType01
    {
        protected override ERockType pRockType => ERockType.Sharp;
    }
}