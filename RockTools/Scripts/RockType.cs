namespace RockTools
{
    public enum CustomAssetMode
    {
        MeshCombine,
        PrefabInstances,
    }

    public enum CustomSimulationMode
    {
        RockCluster,
        CrystalCluster,
    }

    // -------------------------------------------------------------------------------------------------------------

    public enum ERockType
    {
        Cubic = 0,
        Sharp = 1,
        Crystal = 2,

        Custom = 255,
    }

    // -------------------------------------------------------------------------------------------------------------

    public static class RockTypeExtensions
    {
        public static string GetResourcesPath(this ERockType rockType)
        {
            switch (rockType)
            {
                case ERockType.Cubic:
                    return "Meshes/Type01";
                case ERockType.Sharp:
                    return "Meshes/Type02";
                case ERockType.Crystal:
                    return "Meshes/Type03";
                case ERockType.Custom:
                default:
                    return "Meshes/Custom";
            }
        }
    }

    // -------------------------------------------------------------------------------------------------------------
}
